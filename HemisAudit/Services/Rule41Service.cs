using Newtonsoft.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 41: Student ASCII Agreement — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. Canonical shared-type owner for
    // the 41/47/48/60 family (Rule47Service.cs etc. reuse these Rule41* types directly under
    // their own rule numbers and defaults — see each sibling file for its own DefaultPairs/
    // RuleName/table-autodetect values). Loads both tables fully into memory and reconciles them
    // in C# by a configurable join key plus a configurable list of column pairs, unchanged from
    // the original except for the connection layer.
    public class Rule41Service : IRule41Service
    {
        private const int ExceptionRowSaveLimit  = 5000;
        private const int AgreeSampleLimit       = 100;
        private const int BrowserPreviewRowLimit = 10;

        private static readonly List<Rule41ColumnPair> DefaultPairs = new()
        {
            new Rule41ColumnPair { StudCol = "_007", H16Col = "IAGSTNO", Label = "Student No"    },
            new Rule41ColumnPair { StudCol = "_008", H16Col = "IADIDNO", Label = "Birth Date"    },
            new Rule41ColumnPair { StudCol = "_001", H16Col = "IAGQUAL", Label = "Qualification" },
        };

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule41Service(
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

        // ── Table / column discovery ──────────────────────────────────────────

        public async Task<Rule41TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule41TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule41TableDiscoveryResult
                {
                    Success        = true,
                    Tables         = tables,
                    AutoStudTable  = FindFirst(tables, ["dbo_STUD", "STUD"], ["STUD"]),
                    AutoH16Table = FindFirst(tables, ["MT-Audit-prod-std", "MT-Audit"], ["MT-Audit", "Audit"])
                };
            }
            catch (Exception ex) { return new Rule41TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule41ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                return new Rule41ColumnDiscoveryResult
                {
                    Success = true,
                    Columns = cols,
                    AutoKey = FindFirst(cols, ["_007", "IAGSTNO"], ["_007", "IAGSTNO"])
                };
            }
            catch (Exception ex) { return new Rule41ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule41VerifyResult> VerifyTablesAsync(Rule41VerifyRequest request)
        {
            try
            {
                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;
                return new Rule41VerifyResult
                {
                    Success    = true,
                    StudCount  = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";"),
                    H16Count = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.H16Table}\";")
                };
            }
            catch (Exception ex) { return new Rule41VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule41ValidationSummary> RunValidationAsync(Rule41ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ApplyDefaultPairs(request);

                var summary = await AnalyseAsync(request, ExceptionRowSaveLimit);

                if (summary.Success && request.ClientId > 0)
                {
                    try { summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName); }
                    catch (Exception ex)
                    {
                        summary.Success = false;
                        summary.Error   = $"Analysis completed, but the run could not be saved: {ex.Message}";
                        return summary;
                    }
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex) { return new Rule41ValidationSummary { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule41ValidationSummary> GetExportSummaryAsync(Rule41ValidationRequest request)
        {
            ApplyDefaultPairs(request);
            return await AnalyseAsync(request, exceptionLimit: null);
        }

        public async Task<int> GetPopulationCountAsync(Rule41ValidationRequest request)
        {
            var summary = await GetExportSummaryAsync(request);
            return summary.TotalCount;
        }

        private static void ApplyDefaultPairs(Rule41ValidationRequest req)
        {
            if (req.Pairs == null || req.Pairs.Count == 0)
                req.Pairs = DefaultPairs.ToList();
        }

        private static void ApplyBrowserPreview(Rule41ValidationSummary? summary)
        {
            if (summary == null) return;
            TrimReconcRows(summary.Reconc);
        }

        private static void TrimReconcRows(Rule41ReconciliationSummary? reconc)
        {
            if (reconc == null) return;
            reconc.ExceptionRows = reconc.ExceptionRows.Take(BrowserPreviewRowLimit).ToList();
            reconc.Rows          = reconc.Rows.Take(BrowserPreviewRowLimit).ToList();
        }

        private async Task<Rule41ValidationSummary> AnalyseAsync(Rule41ValidationRequest req, int? exceptionLimit)
        {
            var studSecondary  = string.IsNullOrWhiteSpace(req.StudSecondaryKey)  ? null : req.StudSecondaryKey;
            var auditSecondary = string.IsNullOrWhiteSpace(req.H16SecondaryKey) ? null : req.H16SecondaryKey;

            var studCols  = req.Pairs.Select(p => p.StudCol).Distinct().Union([req.StudKey]).ToList();
            var auditCols = req.Pairs.Select(p => p.H16Col).Distinct().Union([req.H16Key]).ToList();
            if (studSecondary  != null) studCols  = studCols.Union([studSecondary]).ToList();
            if (auditSecondary != null) auditCols = auditCols.Union([auditSecondary]).ToList();

            await ValidateColumnsExistAsync(req.ClientId, req.StudTable,  studCols);
            await ValidateColumnsExistAsync(req.ClientId, req.H16Table, auditCols);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var studMap  = await LoadTableAsync(connection, schema, req.StudTable,  req.StudKey,  studCols, studSecondary);
            var auditMap = await LoadTableAsync(connection, schema, req.H16Table, req.H16Key, auditCols, auditSecondary);

            var allKeys = studMap.Keys.Union(auditMap.Keys).ToList();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var reconc = RunReconciliation(studMap, req.StudKey, auditMap, req.H16Key, allKeys, req.Pairs, req.StudTable, req.H16Table, exceptionLimit);
            reconc.StudSecondaryKey  = studSecondary;
            reconc.H16SecondaryKey = auditSecondary;

            return new Rule41ValidationSummary
            {
                Success      = true,
                Timestamp    = now,
                StudTable    = req.StudTable,
                H16Table   = req.H16Table,
                StudKey      = req.StudKey,
                H16Key     = req.H16Key,
                TotalCount   = reconc.TotalCount,
                AgreeCount   = reconc.AgreeCount,
                DisagreeCount= reconc.DisagreeCount,
                MissingCount = reconc.MissingCount,
                ExceptionRate= reconc.ExceptionRate,
                ClientId     = req.ClientId,
                Status       = (reconc.DisagreeCount + reconc.MissingCount == 0) ? "PASS" : "FAIL",
                Reconc       = reconc,
                Warning      = BuildScaleWarning(reconc)
            };
        }

        private static string? BuildScaleWarning(Rule41ReconciliationSummary reconc)
        {
            var exceptionTotal = reconc.DisagreeCount + reconc.MissingCount;
            if (exceptionTotal > reconc.ExceptionRows.Count)
                return $"{exceptionTotal:N0} exception rows were found; only the first {reconc.ExceptionRows.Count:N0} are stored and shown to keep the app responsive. All totals above are exact.";
            if (reconc.AgreeCount > reconc.Rows.Count)
                return $"AGREE rows are stored as a representative sample ({reconc.Rows.Count:N0} of {reconc.AgreeCount:N0}). Exception rows are complete. All totals above are exact.";
            return null;
        }

        private static Rule41ReconciliationSummary RunReconciliation(
            Dictionary<string, Dictionary<string, string?>> studMap,
            string studKey,
            Dictionary<string, Dictionary<string, string?>> auditMap,
            string auditKey,
            List<string> allNormKeys,
            List<Rule41ColumnPair> pairs,
            string studTable, string auditTable, int? exceptionLimit)
        {
            var reconc = new Rule41ReconciliationSummary
            {
                StudTable  = studTable,
                H16Table = auditTable,
                StudKey    = studKey,
                H16Key   = auditKey,
                Pairs      = pairs
            };

            int rowNo = 0;
            foreach (var normKey in allNormKeys.OrderBy(k => k))
            {
                rowNo++;
                var studRow  = studMap.GetValueOrDefault(normKey);
                var auditRow = auditMap.GetValueOrDefault(normKey);
                var rawRef   = studRow?.GetValueOrDefault(studKey) ?? auditRow?.GetValueOrDefault(auditKey) ?? normKey;
                var display  = Disp(rawRef);

                var row = new Rule41ReconcRow { RowNumber = rowNo, StudentRef = display };

                if (studRow == null)
                {
                    foreach (var p in pairs)
                        row.Fields[p.Label] = new Rule41FieldValue { H16Value = Disp(auditRow?.GetValueOrDefault(p.H16Col)), Match = "MISSING" };
                    row.OverallResult  = "MISSING";
                    row.DisagreeDetail = $"Student {display} not in {studTable}";
                }
                else if (auditRow == null)
                {
                    foreach (var p in pairs)
                        row.Fields[p.Label] = new Rule41FieldValue { StudValue = Disp(studRow.GetValueOrDefault(p.StudCol)), Match = "MISSING" };
                    row.OverallResult  = "MISSING";
                    row.DisagreeDetail = $"Student {display} not in {auditTable}";
                }
                else
                {
                    var diffs = new List<string>();
                    foreach (var p in pairs)
                    {
                        var sv    = Disp(studRow.GetValueOrDefault(p.StudCol));
                        var av    = Disp(auditRow.GetValueOrDefault(p.H16Col));
                        var match = ValuesMatch(sv, av) ? "AGREE" : "DISAGREE";
                        row.Fields[p.Label] = new Rule41FieldValue { StudValue = sv, H16Value = av, Match = match };
                        if (match == "DISAGREE")
                            diffs.Add($"{p.Label}: STUD='{sv}' ≠ AUDIT='{av}'");
                    }
                    row.OverallResult  = diffs.Count == 0 ? "AGREE" : "DISAGREE";
                    row.DisagreeDetail = string.Join(" | ", diffs);
                }

                if (row.OverallResult != "AGREE")
                    reconc.ExceptionRows.Add(row);
                else
                    reconc.Rows.Add(row);
            }

            reconc.TotalCount    = rowNo;
            reconc.DisagreeCount = reconc.ExceptionRows.Count(r => r.OverallResult == "DISAGREE");
            reconc.MissingCount  = reconc.ExceptionRows.Count(r => r.OverallResult == "MISSING");
            reconc.AgreeCount    = reconc.TotalCount - reconc.ExceptionRows.Count;
            reconc.ExceptionRate = reconc.TotalCount == 0 ? 0m : Math.Round((decimal)reconc.ExceptionRows.Count / reconc.TotalCount * 100m, 2);

            reconc.ExceptionRows = exceptionLimit.HasValue ? reconc.ExceptionRows.Take(exceptionLimit.Value).ToList() : reconc.ExceptionRows;
            reconc.Rows          = reconc.Rows.Take(AgreeSampleLimit).ToList();

            return reconc;
        }

        // ── Table loading ─────────────────────────────────────────────────────

        // secondaryKeyCol, when set, is combined with keyCol into a composite key (e.g. student
        // number + qualification code) — needed when the primary key alone repeats, such as one
        // row per qualification for the same student.
        private static async Task<Dictionary<string, Dictionary<string, string?>>> LoadTableAsync(
            NpgsqlConnection conn, string schema, string tableName, string keyCol, List<string> columns, string? secondaryKeyCol = null)
        {
            var selCols = columns.Select(c => $"\"{Sanitise(c)}\"").ToList();
            if (!selCols.Any(c => c == $"\"{Sanitise(keyCol)}\""))
                selCols.Insert(0, $"\"{Sanitise(keyCol)}\"");
            if (secondaryKeyCol != null && !selCols.Any(c => c == $"\"{Sanitise(secondaryKeyCol)}\""))
                selCols.Insert(0, $"\"{Sanitise(secondaryKeyCol)}\"");

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {string.Join(", ", selCols)} FROM \"{schema}\".\"{Sanitise(tableName)}\";";
            await using var reader = await cmd.ExecuteReaderAsync();

            var fieldNames  = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i)).ToList();
            var map         = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
            var normKeyCol  = Sanitise(keyCol);
            var normSecondaryKeyCol = secondaryKeyCol == null ? null : Sanitise(secondaryKeyCol);

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                    row[fieldNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString()?.Trim();

                var normKey = Norm(row.GetValueOrDefault(normKeyCol));
                if (normSecondaryKeyCol != null)
                    normKey = $"{normKey}|{Norm(row.GetValueOrDefault(normSecondaryKeyCol))}";
                if (!string.IsNullOrEmpty(normKey) && !map.ContainsKey(normKey))
                    map[normKey] = row;
            }
            return map;
        }

        // ── SQL generation ────────────────────────────────────────────────────

        public string GenerateSql(Rule41ValidationRequest request)
        {
            ApplyDefaultPairs(request);
            var st  = Sanitise(request.StudTable);
            var at  = Sanitise(request.H16Table);
            var sk  = Sanitise(request.StudKey);
            var ak  = Sanitise(request.H16Key);
            var pairs = request.Pairs ?? DefaultPairs;

            var fieldSel  = BuildFieldSelect(pairs, "s", "a");
            var agreeCond = BuildAgreeCondition(pairs, "s", "a");
            var testedCols = string.Join(", ", pairs.Select(p => $"{p.StudCol}<->{p.H16Col}"));

            var hasSecondaryKey = !string.IsNullOrWhiteSpace(request.StudSecondaryKey) && !string.IsNullOrWhiteSpace(request.H16SecondaryKey);
            var sk2 = hasSecondaryKey ? Sanitise(request.StudSecondaryKey!)  : null;
            var ak2 = hasSecondaryKey ? Sanitise(request.H16SecondaryKey!) : null;
            var joinCond = $"UPPER(TRIM(CAST(s.\"{sk}\" AS text))) = UPPER(TRIM(CAST(a.\"{ak}\" AS text)))"
                + (hasSecondaryKey ? $"\n    AND UPPER(TRIM(CAST(s.\"{sk2}\" AS text))) = UPPER(TRIM(CAST(a.\"{ak2}\" AS text)))" : "");
            var keyComment = hasSecondaryKey ? $"\"{sk}\"+\"{sk2}\" <-> \"{ak}\"+\"{ak2}\" (composite)" : $"\"{sk}\" <-> \"{ak}\"";

            return $@"-- ============================================================
-- HEMIS RULE 41 - Student ASCII Agreement
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Tables    : ""{st}"" vs ""{at}""
-- Join key  : {keyComment}
-- Columns   : {testedCols}
-- ============================================================

SELECT
    ROW_NUMBER() OVER (ORDER BY COALESCE(s.""{sk}"", a.""{ak}"")) AS row_no,
    COALESCE(s.""{sk}"", a.""{ak}"") AS student_ref,
{fieldSel},
    CASE
        WHEN s.""{sk}"" IS NULL THEN 'MISSING-STUD'
        WHEN a.""{ak}"" IS NULL THEN 'MISSING-AUDIT'
        WHEN {agreeCond} THEN 'AGREE'
        ELSE 'DISAGREE'
    END AS overall_result
FROM ""{st}"" s
FULL OUTER JOIN ""{at}"" a
    ON {joinCond}
ORDER BY row_no;

-- Summary
SELECT
    COUNT(*) AS total,
    COUNT(*) FILTER (WHERE {agreeCond} AND s.""{sk}"" IS NOT NULL AND a.""{ak}"" IS NOT NULL) AS agree,
    COUNT(*) FILTER (WHERE NOT ({agreeCond}) AND s.""{sk}"" IS NOT NULL AND a.""{ak}"" IS NOT NULL) AS disagree,
    COUNT(*) FILTER (WHERE s.""{sk}"" IS NULL OR a.""{ak}"" IS NULL) AS missing
FROM ""{st}"" s
FULL OUTER JOIN ""{at}"" a
    ON {joinCond};
-- ============================================================
-- END RULE 41
-- ============================================================".Trim();
        }

        private static string BuildFieldSelect(List<Rule41ColumnPair> pairs, string sAlias, string aAlias)
        {
            var lines = new List<string>();
            foreach (var p in pairs)
            {
                var sc = Sanitise(p.StudCol);
                var ac = Sanitise(p.H16Col);
                var compare = BuildEquivalentSqlComparison($"{sAlias}.\"{sc}\"", $"{aAlias}.\"{ac}\"");
                lines.Add($"    {sAlias}.\"{sc}\" AS \"STUD_{p.Label}\",\n    {aAlias}.\"{ac}\" AS \"AUDIT_{p.Label}\",\n    CASE WHEN {compare} THEN 'AGREE' ELSE 'DISAGREE' END AS \"MATCH_{p.Label}\"");
            }
            return string.Join(",\n", lines);
        }

        private static string BuildAgreeCondition(List<Rule41ColumnPair> pairs, string sAlias, string aAlias)
        {
            var conds = pairs.Select(p =>
            {
                var sc = Sanitise(p.StudCol);
                var ac = Sanitise(p.H16Col);
                return BuildEquivalentSqlComparison($"{sAlias}.\"{sc}\"", $"{aAlias}.\"{ac}\"");
            });
            return string.Join("\n        AND ", conds);
        }

        private static string BuildEquivalentSqlComparison(string leftExpression, string rightExpression)
        {
            var leftTrim  = $"COALESCE(TRIM(CAST({leftExpression} AS text)), '')";
            var rightTrim = $"COALESCE(TRIM(CAST({rightExpression} AS text)), '')";

            return $@"(
        {leftTrim} = {rightTrim}
        OR (
            {leftTrim} ~ '^[0-9]+(\.[0-9]+)?$' AND {rightTrim} ~ '^[0-9]+(\.[0-9]+)?$'
            AND {leftTrim}::numeric = {rightTrim}::numeric
        )
        OR UPPER({leftTrim}) = UPPER({rightTrim})
    )";
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule41ValidationRequest request, Rule41ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 41);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 41,
                RuleName = "Student ASCII Agreement",
                Status = summary.Status,
                TotalRecords = summary.TotalCount,
                PassCount = summary.AgreeCount,
                FailCount = summary.DisagreeCount + summary.MissingCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.H16Table,
                StudColumn = request.StudKey,
                DeceasedColumn = request.H16Key,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.Reconc.ExceptionRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule41WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 41);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            ApplyBrowserPreview(summary);

            var workspace = new Rule41WorkspaceStateViewModel
            {
                ClientId             = row.ClientId,
                RunId                = row.RunId,
                StudTable            = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable,
                H16Table           = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "MT-Audit-prod-std" : row.DeceasedTable,
                StudKey              = string.IsNullOrWhiteSpace(row.StudColumn) ? "_007" : row.StudColumn,
                H16Key             = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "IAGSTNO" : row.DeceasedColumn,
                CurrentStatus        = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt         = row.LastEditedAt,
                Summary              = summary
            };

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            workspace.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(clientId, currentUser.Id) ?? ""
                : "";

            var signoffs = await _systemDb.GetRuleRunSignoffsAsync(workspace.RunId!.Value, currentUser?.Id);
            workspace.HasDataAnalystSignoff   = signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            var roleSignoff = signoffs.FirstOrDefault(s => ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, workspace.CurrentUserEngagementRole));
            workspace.CurrentUserHasSignedOff    = roleSignoff != null;
            workspace.CurrentUserSignoffComment  = roleSignoff?.Comment ?? "";
            workspace.IsWorkspaceSaved           = await _systemDb.IsWorkspaceSavedAsync(workspace.RunId!.Value);

            return workspace;
        }

        public async Task<Rule41RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 41);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var viewModel = new Rule41RunReviewViewModel
            {
                RunId          = row.RunId,
                ClientId       = row.ClientId,
                IsCurrentRun   = row.IsCurrent,
                EngagementName = row.EngagementName,
                MaconomyNumber = row.MaconomyNumber,
                Summary        = summary
            };

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            viewModel.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(viewModel.ClientId, currentUser.Id) ?? ""
                : "";
            viewModel.Signoffs              = await _systemDb.GetRuleRunSignoffsAsync(runId, currentUser?.Id);
            viewModel.HasDataAnalystSignoff = viewModel.Signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            viewModel.CurrentUserHasSignedOff = viewModel.Signoffs.Any(s => ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, viewModel.CurrentUserEngagementRole));
            viewModel.CanCurrentUserSignOff = ValidationRunAccessPolicy.CanCompleteReviewSignoff(viewModel.CurrentUserEngagementRole, viewModel.CurrentUserEngagementRole, viewModel.HasDataAnalystSignoff);

            return viewModel;
        }

        public async Task<Rule41WorkspaceSaveResult> SaveWorkspaceAsync(Rule41ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule41WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule41WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.H16Table,
                    StudColumn = request.StudKey,
                    DeceasedColumn = request.H16Key
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule41WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = clearedSignoffs > 0 ? "Workspace saved. Existing signoffs were removed." : "Workspace saved.",
                    SignoffsCleared     = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule41WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule41WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule41WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule41WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = clearedSignoffs > 0 ? "Editing has begun. Signoffs were removed." : "Editing has begun.",
                    SignoffsCleared     = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule41WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Reviewer could not be resolved.");
            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Validation run was not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(engagementRole))
                throw new InvalidOperationException("Only assigned data analysts, managers, and directors can sign off.");

            if (!string.Equals(engagementRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase) &&
                !await _systemDb.HasRuleSignoffRoleAsync(runId, "DataAnalyst"))
                throw new InvalidOperationException("The assigned data analyst must sign off first.");

            await _systemDb.AddOrUpdateRuleSignoffAsync(runId, clientId, reviewer.Id, engagementRole!, comment);
        }

        public async Task RemoveSignoffAsync(int runId, string reviewerEmail)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Reviewer could not be resolved.");
            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Validation run was not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned analyst, manager, or director can remove signoff.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static string Norm(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "";
            var s = Regex.Replace(v.Trim().ToUpperInvariant(), @"\s+", " ");
            // Treat leading-zero numeric codes as equal: "05" -> "5", "005" -> "5", "0" -> "0"
            if (s.Length > 0 && s.All(char.IsDigit))
                s = s.TrimStart('0') is { Length: > 0 } stripped ? stripped : "0";
            return s;
        }

        private static bool ValuesMatch(string? left, string? right)
        {
            var leftValue  = left?.Trim() ?? string.Empty;
            var rightValue = right?.Trim() ?? string.Empty;

            if (leftValue.Length == 0 || rightValue.Length == 0)
                return leftValue.Length == rightValue.Length;

            if (TryParseNumericLike(leftValue, out var leftNumeric) &&
                TryParseNumericLike(rightValue, out var rightNumeric))
                return leftNumeric == rightNumeric;

            return string.Equals(Norm(leftValue), Norm(rightValue), StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseNumericLike(string value, out decimal parsed) =>
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed) ||
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed);

        private static string Disp(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();

        private static string Sanitise(string name) =>
            name.Replace("\"", "").Replace("'", "").Replace(";", "").Trim();

        private static async Task<int> CountAsync(NpgsqlConnection conn, string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        // Column names (join keys, pair columns) come from defaults or a previously-saved
        // workspace and may not match this engagement's actual uploaded table — check before
        // querying so a mismatch surfaces as a clear message instead of a raw Postgres
        // "column does not exist" error.
        private async Task ValidateColumnsExistAsync(int clientId, string tableName, IEnumerable<string> requiredColumns)
        {
            var actual  = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
            var missing = requiredColumns.Where(c => !actual.Contains(c, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Column(s) {string.Join(", ", missing.Select(m => $"\"{m}\""))} were not found in table \"{tableName}\". " +
                    "Update the join key and column mapping to match your uploaded data, then run again.");
        }

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
            return values.FirstOrDefault();
        }

        private static Rule41ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule41ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
            catch { return null; }
        }

        private static bool CanSignOffAsRole(string? role) =>
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
