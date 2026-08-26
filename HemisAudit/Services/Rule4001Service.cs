using Newtonsoft.Json;
using System.Text.RegularExpressions;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 40.1: PROF VALPAC vs H16SFTE Staff Presence — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. Simpler sibling of Rule 40:
    // only checks that the join key is present in both tables (no field-by-field comparison).
    public class Rule4001Service : IRule4001Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int ExceptionSaveLimit     = 5000;
        private const int AgreeSaveLimit         = 200;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule4001Service(
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

        public async Task<Rule4001TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule4001TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule4001TableDiscoveryResult
                {
                    Success         = true,
                    Tables          = tables,
                    AutoValpacTable = FindFirst(tables, ["dbo_PROF", "H16PROF"], ["PROF", "VALPAC"]),
                    AutoSfteTable   = FindFirst(tables, ["2025H16SFTE", "H16SFTE"], ["SFTE"])
                };
            }
            catch (Exception ex) { return new Rule4001TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<List<string>> GetTableColumnsAsync(int clientId, string tableName)
        {
            try { return await _datasets.GetValidatedColumnsAsync(clientId, tableName); }
            catch { return new List<string>(); }
        }

        public async Task<Rule4001VerifyResult> VerifyTablesAsync(Rule4001VerifyRequest request)
        {
            try
            {
                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;
                return new Rule4001VerifyResult
                {
                    Success     = true,
                    ValpacCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.ValpacTable}\";"),
                    SfteCount   = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.SfteTable}\";")
                };
            }
            catch (Exception ex) { return new Rule4001VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule4001ValidationSummary> RunValidationAsync(Rule4001ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                var summary = await AnalyseAsync(request, ExceptionSaveLimit);

                if (summary.Success && request.ClientId > 0)
                    summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName);

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex) { return new Rule4001ValidationSummary { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule4001ValidationSummary?> GetExportSummaryByRunIdAsync(int runId)
        {
            var stored = await _systemDb.GetRuleRunByIdAsync(runId, 4001);
            if (stored == null) return null;

            var req = new Rule4001ValidationRequest
            {
                ClientId = stored.ClientId,
                ValpacTable = stored.StudTable,
                SfteTable = stored.DeceasedTable,
                ValpacKeyCol = stored.StudColumn,
                SfteKeyCol = stored.DeceasedColumn
            };

            var summary = await AnalyseAsync(req, exceptionLimit: null);
            summary.SavedRunId = runId;
            return summary;
        }

        public async Task<int> GetPopulationCountByRunIdAsync(int runId)
        {
            var summary = await GetExportSummaryByRunIdAsync(runId);
            return summary?.TotalCount ?? 0;
        }

        private async Task<Rule4001ValidationSummary> AnalyseAsync(Rule4001ValidationRequest req, int? exceptionLimit = null)
        {
            var valpacKey = string.IsNullOrWhiteSpace(req.ValpacKeyCol) ? "_037" : req.ValpacKeyCol;
            var sfteKey   = string.IsNullOrWhiteSpace(req.SfteKeyCol)   ? "_037" : req.SfteKeyCol;

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var valpacKeys = await LoadKeysAsync(connection, schema, req.ValpacTable, valpacKey);
            var sfteKeys   = await LoadKeysAsync(connection, schema, req.SfteTable,   sfteKey);

            var allKeys = valpacKeys.Keys
                .Union(sfteKeys.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k)
                .ToList();

            var exceptionRows = new List<Rule4001ReconcRow>();
            var agreeRows     = new List<Rule4001ReconcRow>();
            int rowNo = 0;

            foreach (var normKey in allKeys)
            {
                rowNo++;
                var inValpac = valpacKeys.TryGetValue(normKey, out var rawValpac);
                var inSfte   = sfteKeys.TryGetValue(normKey,   out var rawSfte);
                var staffNo  = (rawValpac ?? rawSfte ?? normKey).Trim();

                var row = new Rule4001ReconcRow { RowNumber = rowNo, StaffNumber = staffNo };

                if (!inValpac)
                    row.OverallResult = "MISSING-VALPAC";
                else if (!inSfte)
                    row.OverallResult = "MISSING-H16SFTE";
                else
                    row.OverallResult = "AGREE";

                if (row.OverallResult == "AGREE") agreeRows.Add(row);
                else                              exceptionRows.Add(row);
            }

            var exCount = exceptionRows.Count;
            return new Rule4001ValidationSummary
            {
                Success              = true,
                Timestamp            = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ValpacTable          = req.ValpacTable,
                SfteTable            = req.SfteTable,
                ValpacKeyCol         = valpacKey,
                SfteKeyCol           = sfteKey,
                ClientId             = req.ClientId,
                TotalCount           = rowNo,
                AgreeCount           = agreeRows.Count,
                MissingInSfteCount   = exceptionRows.Count(r => r.OverallResult == "MISSING-H16SFTE"),
                MissingInValpacCount = exceptionRows.Count(r => r.OverallResult == "MISSING-VALPAC"),
                ExceptionRate        = rowNo == 0 ? 0m : Math.Round(exCount * 100m / rowNo, 2),
                Status               = exCount == 0 ? "PASS" : "FAIL",
                ReviewRows           = exceptionLimit.HasValue ? exceptionRows.Take(exceptionLimit.Value).ToList() : exceptionRows,
                AgreeSample          = agreeRows.Take(AgreeSaveLimit).ToList(),
                Warning              = BuildScaleWarning(exceptionRows.Count, exceptionLimit.HasValue ? Math.Min(exceptionRows.Count, exceptionLimit.Value) : exceptionRows.Count, agreeRows.Count, Math.Min(agreeRows.Count, AgreeSaveLimit))
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

        private static void ApplyBrowserPreview(Rule4001ValidationSummary? summary)
        {
            if (summary == null) return;
            summary.ReviewRows  = summary.ReviewRows.Take(BrowserPreviewRowLimit).ToList();
            summary.AgreeSample = summary.AgreeSample.Take(BrowserPreviewRowLimit).ToList();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule4001ValidationRequest req, Rule4001ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(req.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(req.ClientId, 4001);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = req.ClientId,
                RuleNumber = 4001,
                RuleName = "PROF VALPAC vs H16SFTE Staff Presence",
                Status = summary.Status,
                TotalRecords = summary.TotalCount,
                PassCount = summary.AgreeCount,
                FailCount = summary.MissingInSfteCount + summary.MissingInValpacCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = req.ValpacTable,
                DeceasedTable = req.SfteTable,
                StudColumn = summary.ValpacKeyCol,
                DeceasedColumn = summary.SfteKeyCol,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.ReviewRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<Rule4001WorkspaceState?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 4001);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            var workspace = new Rule4001WorkspaceState
            {
                ClientId             = row.ClientId,
                RunId                = row.RunId,
                ValpacTable          = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                SfteTable            = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
                ValpacKeyCol         = string.IsNullOrWhiteSpace(row.StudColumn) ? "_037" : row.StudColumn,
                SfteKeyCol           = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "_037" : row.DeceasedColumn,
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

        public async Task<bool> SaveWorkspaceStateAsync(int clientId, Rule4001ValidationRequest request, string? userEmail)
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
                    DeceasedTable = request.SfteTable,
                    StudColumn = request.ValpacKeyCol,
                    DeceasedColumn = request.SfteKeyCol
                }, userEmail);

                return true;
            }
            catch { return false; }
        }

        public async Task<Rule4001ValidationSummary?> GetFullSummaryByRunIdAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 4001);
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

        public string GenerateSql(Rule4001ValidationRequest request)
        {
            var vt = Sanitise(request.ValpacTable);
            var st = Sanitise(request.SfteTable);
            var vk = Sanitise(string.IsNullOrWhiteSpace(request.ValpacKeyCol) ? "_037" : request.ValpacKeyCol);
            var sk = Sanitise(string.IsNullOrWhiteSpace(request.SfteKeyCol)   ? "_037" : request.SfteKeyCol);

            return $@"-- ============================================================
-- HEMIS RULE 40.1 - PROF VALPAC vs H16SFTE Staff Presence
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- VALPAC    : ""{vt}""  Key: ""{vk}""
-- H16SFTE   : ""{st}""  Key: ""{sk}""
-- ============================================================

SELECT
    COALESCE(v.""{vk}"", s.""{sk}"") AS staff_number,
    CASE
        WHEN v.""{vk}"" IS NULL THEN 'MISSING-VALPAC'
        WHEN s.""{sk}"" IS NULL THEN 'MISSING-H16SFTE'
        ELSE 'AGREE'
    END AS overall_result
FROM ""{vt}"" v
FULL OUTER JOIN ""{st}"" s
    ON UPPER(TRIM(CAST(v.""{vk}"" AS text))) = UPPER(TRIM(CAST(s.""{sk}"" AS text)))
ORDER BY staff_number;

-- Summary
SELECT
    COUNT(*) AS total,
    SUM(CASE WHEN v.""{vk}"" IS NOT NULL AND s.""{sk}"" IS NOT NULL THEN 1 ELSE 0 END) AS agree,
    SUM(CASE WHEN s.""{sk}"" IS NULL THEN 1 ELSE 0 END) AS missing_in_h16sfte,
    SUM(CASE WHEN v.""{vk}"" IS NULL THEN 1 ELSE 0 END) AS missing_in_valpac
FROM ""{vt}"" v
FULL OUTER JOIN ""{st}"" s
    ON UPPER(TRIM(CAST(v.""{vk}"" AS text))) = UPPER(TRIM(CAST(s.""{sk}"" AS text)));
-- ============================================================".Trim();
        }

        // ── Internal DB helpers ───────────────────────────────────────────────

        private static async Task<Dictionary<string, string>> LoadKeysAsync(
            NpgsqlConnection conn, string schema, string tableName, string keyCol)
        {
            var col = Sanitise(keyCol);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT \"{col}\" FROM \"{schema}\".\"{Sanitise(tableName)}\" WHERE \"{col}\" IS NOT NULL;";
            await using var reader = await cmd.ExecuteReaderAsync();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (await reader.ReadAsync())
            {
                var raw  = reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString()?.Trim();
                var norm = Norm(raw);
                if (!string.IsNullOrEmpty(norm) && !map.ContainsKey(norm))
                    map[norm] = raw ?? norm;
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

        private static Rule4001ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule4001ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
