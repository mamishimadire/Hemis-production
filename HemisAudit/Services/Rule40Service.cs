using Newtonsoft.Json;
using System.Text.RegularExpressions;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 40: PROF VALPAC vs ASCII Staff Agreement — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. Loads both tables fully
    // into memory (matching the original SQL-Server behavior, which also loaded every row) and
    // reconciles them in C# by a configurable join key plus a configurable list of column pairs
    // to compare field-by-field. The reconciliation logic itself is unchanged from the original.
    public class Rule40Service : IRule40Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int ExceptionSaveLimit     = 5000;
        private const int AgreeSaveLimit         = 200;

        private static readonly Rule40ColumnPair[] DefaultPairs =
        [
            new() { ValpacCol="_011", AsciiCol="_011", Label="Date of Birth" },
            new() { ValpacCol="_012", AsciiCol="_012", Label="Gender" },
            new() { ValpacCol="_013", AsciiCol="_013", Label="Race" },
            new() { ValpacCol="_014", AsciiCol="_014", Label="Nationality" },
            new() { ValpacCol="_038", AsciiCol="_038", Label="Empl. Commencement" },
            new() { ValpacCol="_042", AsciiCol="_042", Label="Full/Part-time" },
        ];

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule40Service(
            IConfiguration configuration,
            IEngagementDatasetService datasets,
            ISystemDatabaseService systemDb,
            UserManager<ApplicationUser> userManager)
        {
            _configuration = configuration;
            _datasets = datasets;
            _systemDb = systemDb;
            _userManager = userManager;
        }

        // ── Discovery ─────────────────────────────────────────────────────────

        public async Task<Rule40TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule40TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule40TableDiscoveryResult
                {
                    Success         = true,
                    Tables          = tables,
                    AutoValpacTable = FindFirst(tables, ["dbo_PROF", "H16PROF"], ["PROF", "VALPAC"]),
                    AutoAsciiTable  = FindFirst(tables, [], ["ASCII", "ascii"])
                };
            }
            catch (Exception ex) { return new Rule40TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<List<string>> GetTableColumnsAsync(int clientId, string tableName)
        {
            try { return await _datasets.GetValidatedColumnsAsync(clientId, tableName); }
            catch { return new List<string>(); }
        }

        public async Task<Rule40VerifyResult> VerifyTablesAsync(Rule40VerifyRequest request)
        {
            try
            {
                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;
                return new Rule40VerifyResult
                {
                    Success     = true,
                    ValpacCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.ValpacTable}\";"),
                    AsciiCount  = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.AsciiTable}\";")
                };
            }
            catch (Exception ex) { return new Rule40VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule40ValidationSummary> RunValidationAsync(Rule40ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                var summary = await AnalyseAsync(request);

                if (summary.Success && request.ClientId > 0)
                    summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName);

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex) { return new Rule40ValidationSummary { Success = false, Error = ex.Message }; }
        }

        private async Task<Rule40ValidationSummary> AnalyseAsync(Rule40ValidationRequest req)
        {
            var valpacKey = string.IsNullOrWhiteSpace(req.ValpacKeyCol) ? "_037" : req.ValpacKeyCol;
            var asciiKey  = string.IsNullOrWhiteSpace(req.AsciiKeyCol)  ? "_037" : req.AsciiKeyCol;
            var pairs     = (req.Pairs?.Count > 0 ? req.Pairs : null) ?? DefaultPairs.ToList();
            var allCols   = pairs.SelectMany(p => new[] { p.ValpacCol, p.AsciiCol }).Append(valpacKey).Append(asciiKey).Distinct().ToList();

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var valpacMap = await LoadTableAsync(connection, schema, req.ValpacTable, valpacKey, allCols);
            var asciiMap  = await LoadTableAsync(connection, schema, req.AsciiTable,  asciiKey,  allCols);

            var allKeys = valpacMap.Keys
                .Union(asciiMap.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k)
                .ToList();

            var exceptionRows = new List<Rule40ReconcRow>();
            var agreeRows     = new List<Rule40ReconcRow>();
            int rowNo = 0;

            foreach (var normKey in allKeys)
            {
                rowNo++;
                var vRow    = valpacMap.GetValueOrDefault(normKey);
                var aRow    = asciiMap.GetValueOrDefault(normKey);
                var staffNo = vRow?.GetValueOrDefault(valpacKey)?.Trim()
                           ?? aRow?.GetValueOrDefault(asciiKey)?.Trim()
                           ?? normKey;

                var row   = new Rule40ReconcRow { RowNumber = rowNo, StaffNumber = staffNo };
                var diffs = new List<string>();

                if (vRow == null)
                {
                    row.OverallResult  = "MISSING-VALPAC";
                    row.DisagreeDetail = $"Staff {staffNo}: in ASCII but not in VALPAC";
                    foreach (var p in pairs)
                        row.Fields[p.Label] = new Rule40FieldValue { AsciiValue = Disp(aRow?.GetValueOrDefault(p.AsciiCol)), Match = "MISSING" };
                }
                else if (aRow == null)
                {
                    row.OverallResult  = "MISSING-ASCII";
                    row.DisagreeDetail = $"Staff {staffNo}: in VALPAC but not in ASCII";
                    foreach (var p in pairs)
                        row.Fields[p.Label] = new Rule40FieldValue { ValpacValue = Disp(vRow.GetValueOrDefault(p.ValpacCol)), Match = "MISSING" };
                }
                else
                {
                    foreach (var p in pairs)
                    {
                        var vv    = Disp(vRow.GetValueOrDefault(p.ValpacCol));
                        var av    = Disp(aRow.GetValueOrDefault(p.AsciiCol));
                        var match = Norm(vv) == Norm(av) ? "AGREE" : "DISAGREE";
                        row.Fields[p.Label] = new Rule40FieldValue { ValpacValue = vv, AsciiValue = av, Match = match };
                        if (match == "DISAGREE") diffs.Add(p.Label);
                    }
                    row.OverallResult  = diffs.Count == 0 ? "AGREE" : "DISAGREE";
                    row.DisagreeDetail = string.Join(", ", diffs);
                }

                if (row.OverallResult == "AGREE") agreeRows.Add(row);
                else                              exceptionRows.Add(row);
            }

            var exCount = exceptionRows.Count;
            return new Rule40ValidationSummary
            {
                Success              = true,
                Timestamp            = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ValpacTable          = req.ValpacTable,
                AsciiTable           = req.AsciiTable,
                ValpacKeyCol         = valpacKey,
                AsciiKeyCol          = asciiKey,
                ClientId             = req.ClientId,
                TotalCount           = rowNo,
                AgreeCount           = agreeRows.Count,
                DisagreeCount        = exceptionRows.Count(r => r.OverallResult == "DISAGREE"),
                MissingInAsciiCount  = exceptionRows.Count(r => r.OverallResult == "MISSING-ASCII"),
                MissingInValpacCount = exceptionRows.Count(r => r.OverallResult == "MISSING-VALPAC"),
                ExceptionRate        = rowNo == 0 ? 0m : Math.Round(exCount * 100m / rowNo, 2),
                Status               = exCount == 0 ? "PASS" : "FAIL",
                Pairs                = pairs,
                ReviewRows           = exceptionRows.Take(ExceptionSaveLimit).ToList(),
                AgreeSample          = agreeRows.Take(AgreeSaveLimit).ToList(),
                Warning              = BuildScaleWarning(exceptionRows.Count, Math.Min(exceptionRows.Count, ExceptionSaveLimit), agreeRows.Count, Math.Min(agreeRows.Count, AgreeSaveLimit))
            };
        }

        private static string? BuildScaleWarning(int exceptionCount, int exceptionLoaded, int agreeCount, int agreeLoaded)
        {
            if (exceptionCount > exceptionLoaded)
                return $"{exceptionCount:N0} exception rows were found; only the first {exceptionLoaded:N0} are stored and shown to keep the app responsive. All totals above are exact.";
            if (agreeCount > agreeLoaded)
                return $"AGREE rows are stored as a representative sample ({agreeLoaded:N0} of {agreeCount:N0}). Exception rows are complete. All totals above are exact.";
            return null;
        }

        private static void ApplyBrowserPreview(Rule40ValidationSummary? summary)
        {
            if (summary == null) return;
            summary.ReviewRows  = summary.ReviewRows.Take(BrowserPreviewRowLimit).ToList();
            summary.AgreeSample = summary.AgreeSample.Take(BrowserPreviewRowLimit).ToList();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule40ValidationRequest req, Rule40ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(req.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(req.ClientId, 40);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = req.ClientId,
                RuleNumber = 40,
                RuleName = "PROF VALPAC vs ASCII Staff Agreement",
                Status = summary.Status,
                TotalRecords = summary.TotalCount,
                PassCount = summary.AgreeCount,
                FailCount = summary.DisagreeCount + summary.MissingInAsciiCount + summary.MissingInValpacCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = req.ValpacTable,
                DeceasedTable = req.AsciiTable,
                StudColumn = summary.ValpacKeyCol,
                DeceasedColumn = summary.AsciiKeyCol,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.ReviewRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<Rule40WorkspaceState?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 40);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            var workspace = new Rule40WorkspaceState
            {
                ClientId             = row.ClientId,
                RunId                = row.RunId,
                ValpacTable          = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                AsciiTable           = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
                ValpacKeyCol         = string.IsNullOrWhiteSpace(row.StudColumn) ? "_037" : row.StudColumn,
                AsciiKeyCol          = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "_037" : row.DeceasedColumn,
                CurrentStatus        = row.Status,
                IsWorkspaceSaved     = await _systemDb.IsWorkspaceSavedAsync(row.RunId),
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt         = row.LastEditedAt,
                Summary              = summary
            };

            ApplyBrowserPreview(summary);

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            workspace.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(clientId, currentUser.Id) ?? ""
                : "";

            var signoffs = await _systemDb.GetRuleRunSignoffsAsync(workspace.RunId!.Value, currentUser?.Id);
            workspace.HasDataAnalystSignoff = signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            var mySignoff = signoffs.FirstOrDefault(s => ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, workspace.CurrentUserEngagementRole));
            workspace.CurrentUserHasSignedOff   = mySignoff != null;
            workspace.CurrentUserSignoffComment = mySignoff?.Comment ?? "";

            return workspace;
        }

        public async Task<bool> SaveWorkspaceStateAsync(int clientId, Rule40ValidationRequest request, string? userEmail)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0) return false;

                await _systemDb.EnsureClientNotArchivedAsync(clientId);
                await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = clientId,
                    StudTable = request.ValpacTable,
                    DeceasedTable = request.AsciiTable,
                    StudColumn = request.ValpacKeyCol,
                    DeceasedColumn = request.AsciiKeyCol
                }, userEmail);

                return true;
            }
            catch { return false; }
        }

        public async Task<Rule40ValidationSummary?> GetFullSummaryByRunIdAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 40);
            if (row == null) return null;
            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.ClientId = row.ClientId;
            return summary;
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Reviewer not found.");
            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Run not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("Save the workspace before signing off.");

            var engRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!IsSignoffRole(engRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off.");

            if (!string.Equals(engRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase) &&
                !await _systemDb.HasRuleSignoffRoleAsync(runId, "DataAnalyst"))
                throw new InvalidOperationException("The data analyst must sign off first.");

            await _systemDb.AddOrUpdateRuleSignoffAsync(runId, clientId, reviewer.Id, engRole!, comment);
        }

        public async Task RemoveSignoffAsync(int runId, string reviewerEmail)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Reviewer not found.");
            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Run not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);
            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        // ── SQL generation ────────────────────────────────────────────────────

        public string GenerateSql(Rule40ValidationRequest request)
        {
            var vt  = Sanitise(request.ValpacTable);
            var at  = Sanitise(request.AsciiTable);
            var vk  = Sanitise(string.IsNullOrWhiteSpace(request.ValpacKeyCol) ? "_037" : request.ValpacKeyCol);
            var ak  = Sanitise(string.IsNullOrWhiteSpace(request.AsciiKeyCol)  ? "_037" : request.AsciiKeyCol);
            var pairs = (request.Pairs?.Count > 0 ? request.Pairs : null) ?? DefaultPairs.ToList();

            var colLines = string.Join(",\n", pairs.Select(p =>
            {
                var vc = Sanitise(p.ValpacCol); var ac = Sanitise(p.AsciiCol);
                return $"    v.\"{vc}\" AS \"VALPAC_{vc}\",\n    a.\"{ac}\" AS \"ASCII_{ac}\",\n" +
                       $"    CASE WHEN UPPER(TRIM(COALESCE(CAST(v.\"{vc}\" AS text),'')))=UPPER(TRIM(COALESCE(CAST(a.\"{ac}\" AS text),''))) THEN 'AGREE' ELSE 'DISAGREE' END AS \"MATCH_{vc}\"  -- {p.Label}";
            }));

            var agreeWhen = string.Join("\n        AND ", pairs.Select(p =>
            {
                var vc = Sanitise(p.ValpacCol); var ac = Sanitise(p.AsciiCol);
                return $"UPPER(TRIM(COALESCE(CAST(v.\"{vc}\" AS text),'')))=UPPER(TRIM(COALESCE(CAST(a.\"{ac}\" AS text),'')))";
            }));

            return $@"-- ============================================================
-- HEMIS RULE 40 - PROF VALPAC vs ASCII Staff Agreement
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- VALPAC    : ""{vt}""  Key: ""{vk}""
-- ASCII     : ""{at}""  Key: ""{ak}""
-- Compared  : {string.Join(", ", pairs.Select(p => p.Label))}
-- ============================================================

SELECT
    COALESCE(v.""{vk}"", a.""{ak}"") AS staff_number,
{colLines},
    CASE
        WHEN v.""{vk}"" IS NULL THEN 'MISSING-VALPAC'
        WHEN a.""{ak}"" IS NULL THEN 'MISSING-ASCII'
        WHEN {agreeWhen} THEN 'AGREE'
        ELSE 'DISAGREE'
    END AS overall_result
FROM ""{vt}"" v
FULL OUTER JOIN ""{at}"" a
    ON UPPER(TRIM(CAST(v.""{vk}"" AS text))) = UPPER(TRIM(CAST(a.""{ak}"" AS text)))
ORDER BY staff_number;

-- Summary
SELECT
    COUNT(*) AS total,
    SUM(CASE WHEN v.""{vk}"" IS NOT NULL AND a.""{ak}"" IS NOT NULL AND ({agreeWhen}) THEN 1 ELSE 0 END) AS agree,
    SUM(CASE WHEN v.""{vk}"" IS NOT NULL AND a.""{ak}"" IS NOT NULL AND NOT ({agreeWhen}) THEN 1 ELSE 0 END) AS disagree,
    SUM(CASE WHEN a.""{ak}"" IS NULL THEN 1 ELSE 0 END) AS missing_in_ascii,
    SUM(CASE WHEN v.""{vk}"" IS NULL THEN 1 ELSE 0 END) AS missing_in_valpac
FROM ""{vt}"" v
FULL OUTER JOIN ""{at}"" a
    ON UPPER(TRIM(CAST(v.""{vk}"" AS text))) = UPPER(TRIM(CAST(a.""{ak}"" AS text)));
-- ============================================================".Trim();
        }

        // ── Internal DB helpers ───────────────────────────────────────────────

        private static async Task<Dictionary<string, Dictionary<string, string?>>> LoadTableAsync(
            NpgsqlConnection conn, string schema, string tableName, string keyCol, IEnumerable<string> columns)
        {
            var selCols = columns.Select(c => $"\"{Sanitise(c)}\"").Distinct().ToList();
            var keyExpr = $"\"{Sanitise(keyCol)}\"";
            if (!selCols.Contains(keyExpr)) selCols.Insert(0, keyExpr);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {string.Join(", ", selCols)} FROM \"{schema}\".\"{Sanitise(tableName)}\";";
            await using var reader = await cmd.ExecuteReaderAsync();
            var fieldNames = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i)).ToList();
            var map        = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                    row[fieldNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString()?.Trim();

                var rawKey  = row.GetValueOrDefault(keyCol);
                var normKey = Norm(rawKey);
                if (!string.IsNullOrEmpty(normKey) && !map.ContainsKey(normKey))
                    map[normKey] = row;
            }
            return map;
        }

        private static async Task<int> CountAsync(NpgsqlConnection conn, string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static string Norm(string? v) =>
            string.IsNullOrWhiteSpace(v) ? "" : Regex.Replace(v.Trim().ToUpperInvariant(), @"\s+", " ");

        private static string Disp(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();

        private static string Sanitise(string name) =>
            name.Replace("\"", "").Replace("'", "").Replace(";", "").Trim();

        private static string? FindFirst(IEnumerable<string> values, string[] exactMatches, string[] containsMatches)
        {
            foreach (var exact in exactMatches)
            {
                var m = values.FirstOrDefault(v => v.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(m)) return m;
            }
            foreach (var fragment in containsMatches)
            {
                var m = values.FirstOrDefault(v => v.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(m)) return m;
            }
            return null;
        }

        private static Rule40ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule40ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
            catch { return null; }
        }

        private static bool IsSignoffRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager",     StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director",    StringComparison.OrdinalIgnoreCase);

        private async Task<(NpgsqlConnection Connection, string Schema)> OpenEngagementConnectionAsync(int clientId)
        {
            var database = await _datasets.GetDatabaseAsync(clientId)
                ?? throw new InvalidOperationException("Create a database for this engagement before running this rule.");

            var connectionString = HemisAudit.Data.PostgresConnectionStringHelper.WithResiliencyDefaults(
                _configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."),
                commandTimeoutSeconds: 0);

            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var setTimeout = connection.CreateCommand())
            {
                setTimeout.CommandText = "SET statement_timeout = 0;";
                await setTimeout.ExecuteNonQueryAsync();
            }
            var schema = string.IsNullOrWhiteSpace(database.SchemaName) ? $"engagement_{clientId}" : database.SchemaName;
            return (connection, schema);
        }
    }
}
