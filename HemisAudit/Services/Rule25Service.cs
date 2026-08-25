using System.Globalization;
using System.Security.Cryptography;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 25: Reconcile Course Datasets — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. Ported from the Rule23/24 pattern.
    // CRSE is the driving table (one row per course) LEFT JOINed by course code to an Audit
    // dataset and an H16 dataset. Like Rule24, the original SQL Server design pulled every CRSE
    // row into memory with the FAIL side completely uncapped (only the 100-row PASS sample was
    // bounded) — the same unbounded-load risk that caused Rule18's OutOfMemoryException.
    // FailRowLimit is introduced here from the start, matching Rule23/24's safety cap.
    public class Rule25Service : IRule25Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int PassSampleLimit = 100;
        private const int FailRowLimit = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule25Service(
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

        // ── Engagement data source (uploaded tables, not a live SQL Server) ────────────────

        public async Task<Rule25TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule25TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule25TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoCrseTable = FindFirst(tables, ["dbo_CRSE", "dbo_crse", "CRSE", "dbo_CRSE_VALIDATION_DETAIL"], ["crse"]),
                    AutoAuditTable = FindFirst(tables, ["MT-audit-prod-CRSE", "MT_AUDIT_PROD_CRSE", "mt_audit_prod_crse"], ["audit", "crse"]),
                    AutoH16Table = FindFirst(tables, ["H16CRSE", "H16CRS", "h16crse"], ["h16", "crse"])
                };
            }
            catch (Exception ex)
            {
                return new Rule25TableDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule25AuditColumnResult> GetAuditColumnsAsync(int clientId, string auditTable)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, auditTable);
                return new Rule25AuditColumnResult
                {
                    Success = true,
                    Columns = columns,
                    AutoCourseCodeColumn = FindFirst(columns, ["IALSUBJ", "_030", "CRSECODE"], ["ialsubj", "crse", "subject"])
                };
            }
            catch (Exception ex)
            {
                return new Rule25AuditColumnResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule25VerifyResult> VerifyTablesAsync(Rule25VerifyRequest request)
        {
            try
            {
                await EnsureColumnsExistAsync(request.ClientId, request);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                return new Rule25VerifyResult
                {
                    Success = true,
                    CrseCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CrseTable}\";"),
                    AuditCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.AuditTable}\";"),
                    H16Count = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.H16Table}\";")
                };
            }
            catch (Exception ex)
            {
                return new Rule25VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule25ValidationSummary> RunValidationAsync(Rule25ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                var summary = await AnalyseAsync(request);
                if (summary.Success && request.ClientId > 0)
                {
                    try
                    {
                        summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName);
                    }
                    catch (Exception ex)
                    {
                        summary.Success = false;
                        summary.Error = $"Analysis completed, but the saved run could not be written to the system database: {ex.Message}";
                        return summary;
                    }
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule25ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule25WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 25);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule25WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                CrseTable = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                AuditTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
                H16Table = string.IsNullOrWhiteSpace(row.StudColumn) ? "" : row.StudColumn,
                AuditCourseCodeColumn = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "IALSUBJ" : row.DeceasedColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (deserializedSummary != null)
            {
                workspace.CrseCourseCodeColumn = deserializedSummary.CrseCourseCodeColumn;
                workspace.H16CourseCodeColumn = deserializedSummary.H16CourseCodeColumn;
                workspace.AuditCourseCodeColumn = string.IsNullOrWhiteSpace(workspace.AuditCourseCodeColumn)
                    ? deserializedSummary.AuditCourseCodeColumn
                    : workspace.AuditCourseCodeColumn;
                workspace.CurrentStatus = deserializedSummary.Status;
            }

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            workspace.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(clientId, currentUser.Id) ?? ""
                : "";

            var signoffs = await _systemDb.GetRuleRunSignoffsAsync(workspace.RunId!.Value, currentUser?.Id);
            workspace.HasDataAnalystSignoff = signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            var currentRoleSignoff = signoffs.FirstOrDefault(s =>
                ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, workspace.CurrentUserEngagementRole));
            workspace.CurrentUserHasSignedOff = currentRoleSignoff != null;
            workspace.CurrentUserSignoffComment = currentRoleSignoff?.Comment ?? "";
            workspace.IsWorkspaceSaved = await _systemDb.IsWorkspaceSavedAsync(workspace.RunId!.Value);

            return workspace;
        }

        public async Task<Rule25RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 25);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule25RunReviewViewModel
            {
                RunId = row.RunId,
                ClientId = row.ClientId,
                IsCurrentRun = row.IsCurrent,
                EngagementName = row.EngagementName,
                MaconomyNumber = row.MaconomyNumber,
                Summary = summary
            };

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            review.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(review.ClientId, currentUser.Id) ?? ""
                : "";

            review.Signoffs = await _systemDb.GetRuleRunSignoffsAsync(runId, currentUser?.Id);
            review.HasDataAnalystSignoff = review.Signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));

            return review;
        }

        public async Task<Rule25WorkspaceSaveResult> SaveWorkspaceAsync(Rule25ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0)
                    return new Rule25WorkspaceSaveResult { Success = false, Error = "Run the validation first so the workspace can be saved." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule25WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.CrseTable,
                    DeceasedTable = request.AuditTable,
                    StudColumn = request.H16Table,
                    DeceasedColumn = request.AuditCourseCodeColumn
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule25WorkspaceSaveResult
                {
                    Success = true,
                    Message = clearedSignoffs > 0
                        ? "Workspace saved. Existing signoffs were removed and the run must be reviewed again."
                        : "Workspace saved and marked for review again.",
                    SignoffsCleared = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace = workspace
                };
            }
            catch (Exception ex)
            {
                return new Rule25WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule25WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule25WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule25WorkspaceSaveResult
                {
                    Success = true,
                    Message = clearedSignoffs > 0
                        ? "Editing has begun. Existing signoffs were removed."
                        : "Editing has begun. Save the workspace when you are ready.",
                    SignoffsCleared = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace = workspace
                };
            }
            catch (Exception ex)
            {
                return new Rule25WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 25 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 25 run.");

            if (!string.Equals(signoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase) &&
                !await _systemDb.HasRuleSignoffRoleAsync(runId, "DataAnalyst"))
            {
                throw new InvalidOperationException("The assigned data analyst must sign off before this review can be completed.");
            }

            await _systemDb.AddOrUpdateRuleSignoffAsync(runId, clientId, reviewer.Id, signoffRole!, comment);
        }

        public async Task RemoveSignoffAsync(int runId, string reviewerEmail)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 25 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule25ValidationRequest request)
        {
            await EnsureColumnsExistAsync(request.ClientId, ToVerifyRequest(request));

            var sql = $@"-- HEMIS RULE 25: RECONCILE COURSE DATASETS
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- CRSE Table : ""{request.CrseTable}""  |  Code: ""{request.CrseCourseCodeColumn}""
-- Audit Table: ""{request.AuditTable}""  |  Code: ""{request.AuditCourseCodeColumn}""
-- H16 Table  : ""{request.H16Table}""  |  Code: ""{request.H16CourseCodeColumn}""

{BuildRule25PrepSql("{schema}", request.CrseTable, request.AuditTable, request.H16Table, request)}

-- Mismatch rows
SELECT * FROM rule25_population WHERE reconciliation_status <> 'MATCH' ORDER BY row_number;

-- Summary
SELECT reconciliation_status, COUNT(*) AS issue_count
FROM rule25_population
GROUP BY reconciliation_status
ORDER BY issue_count DESC;";

            return sql.Trim();
        }

        // Re-runs the same analysis a fresh "Run Validation" would, without the save-run side
        // effect - used by the Excel export path.
        public async Task<Rule25ValidationSummary> GetExportSummaryAsync(Rule25ValidationRequest request) =>
            await AnalyseAsync(request);

        // Cheap population size check - stops at a COUNT(*), no result rows loaded.
        public async Task<int> GetPopulationCountAsync(Rule25ValidationRequest request)
        {
            await EnsureColumnsExistAsync(request.ClientId, ToVerifyRequest(request));

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule25PrepSql(schema, request.CrseTable, request.AuditTable, request.H16Table, request);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var statusCounts = await GetStatusCountsAsync(connection);
            var totalValidated = statusCounts.Values.Sum();
            var matches = statusCounts.GetValueOrDefault("MATCH");
            var mismatches = totalValidated - matches;
            return mismatches + Math.Min(matches, PassSampleLimit);
        }

        // Bypasses AnalyseAsync/LoadRowsAsync entirely for the mismatch side - no cap. The
        // matching side stays a deliberate fixed-size sample (PassSampleLimit). Mirrors
        // Rule12Service.StreamCsvExportAsync.
        public async Task StreamCsvExportAsync(Rule25ValidationRequest request, Stream outputStream)
        {
            await EnsureColumnsExistAsync(request.ClientId, ToVerifyRequest(request));

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule25PrepSql(schema, request.CrseTable, request.AuditTable, request.H16Table, request);
                await prepCommand.ExecuteNonQueryAsync();
            }

            await using var writer = new StreamWriter(outputStream, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = false };

            await using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT * FROM ( SELECT * FROM rule25_population WHERE reconciliation_status <> 'MATCH' ORDER BY row_number ) mismatches
UNION ALL
SELECT * FROM ( SELECT * FROM rule25_population WHERE reconciliation_status = 'MATCH' ORDER BY row_number LIMIT {PassSampleLimit} ) match_sample;";
            await using var reader = await command.ExecuteReaderAsync();

            var headerParts = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i)).ToList();
            await writer.WriteLineAsync(string.Join(",", headerParts.Select(StreamCsvEscape)));

            var rowValues = new List<string>(reader.FieldCount);
            while (await reader.ReadAsync())
            {
                rowValues.Clear();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var val = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                    rowValues.Add(StreamCsvEscape(val));
                }
                await writer.WriteLineAsync(string.Join(",", rowValues));
            }

            await writer.FlushAsync();
        }

        private static string StreamCsvEscape(string? val)
        {
            if (string.IsNullOrEmpty(val))
                return "";
            if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
                return $"\"{val.Replace("\"", "\"\"")}\"";
            return val;
        }

        // ── Analysis ─────────────────────────────────────────────────────────────────────

        private async Task<Rule25ValidationSummary> AnalyseAsync(Rule25ValidationRequest request)
        {
            await EnsureColumnsExistAsync(request.ClientId, ToVerifyRequest(request));

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var crseCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CrseTable}\";");
            var auditCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.AuditTable}\";");
            var h16Count = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.H16Table}\";");

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule25PrepSql(schema, request.CrseTable, request.AuditTable, request.H16Table, request);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var statusCounts = await GetStatusCountsAsync(connection);
            var totalValidated = statusCounts.Values.Sum();
            var matches = statusCounts.GetValueOrDefault("MATCH");
            var mismatches = totalValidated - matches;

            var passSampleRows = await LoadRowsAsync(connection, matchOnly: true, PassSampleLimit);
            var failRows = await LoadRowsAsync(connection, matchOnly: false, FailRowLimit);

            var exceptionRate = totalValidated == 0 ? 0m : Math.Round((decimal)mismatches / totalValidated * 100m, 2);
            var matchRate = totalValidated == 0 ? 0m : Math.Round((decimal)matches / totalValidated * 100m, 2);
            var failRowsTruncated = mismatches > failRows.Count;

            return new Rule25ValidationSummary
            {
                Success = true,
                TotalValidated = totalValidated,
                Matches = matches,
                Mismatches = mismatches,
                PassCount = matches,
                FailCount = mismatches,
                ExceptionRate = exceptionRate,
                MatchRate = matchRate,
                Status = mismatches == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                CrseTable = request.CrseTable,
                AuditTable = request.AuditTable,
                H16Table = request.H16Table,
                CrseCourseCodeColumn = request.CrseCourseCodeColumn,
                AuditCourseCodeColumn = request.AuditCourseCodeColumn,
                H16CourseCodeColumn = request.H16CourseCodeColumn,
                CrseCount = crseCount,
                AuditCount = auditCount,
                H16Count = h16Count,
                ClientId = request.ClientId,
                PassSampleCount = passSampleRows.Count,
                PassSampleTruncated = matches > passSampleRows.Count,
                SavedFailRowCount = failRows.Count,
                FailRowsTruncated = failRowsTruncated,
                IssueCounts = statusCounts
                    .Where(x => !string.Equals(x.Key, "MATCH", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new Rule25IssueBreakdownItemViewModel { Status = x.Key, Count = x.Value })
                    .ToList(),
                PassSampleRows = passSampleRows,
                FailRows = failRows,
                Warning = failRowsTruncated
                    ? $"Only the first {failRows.Count:N0} mismatch rows were saved for browser review and export performance. Total mismatches found: {mismatches:N0}."
                    : null
            };
        }

        private static string BuildRule25PrepSql(string schema, string crseTable, string auditTable, string h16Table, Rule25ValidationRequest r)
        {
            return $@"
DROP TABLE IF EXISTS rule25_audit;
CREATE TEMP TABLE rule25_audit AS
SELECT DISTINCT ON (norm_code) norm_code, course_code FROM (
    SELECT
        UPPER(TRIM(CAST(""{r.AuditCourseCodeColumn}"" AS text))) AS norm_code,
        TRIM(CAST(""{r.AuditCourseCodeColumn}"" AS text)) AS course_code
    FROM ""{schema}"".""{auditTable}""
    WHERE ""{r.AuditCourseCodeColumn}"" IS NOT NULL
) x ORDER BY norm_code;
CREATE INDEX ON rule25_audit(norm_code);
ANALYZE rule25_audit;

DROP TABLE IF EXISTS rule25_h16;
CREATE TEMP TABLE rule25_h16 AS
SELECT DISTINCT ON (norm_code) norm_code, course_code FROM (
    SELECT
        UPPER(TRIM(CAST(""{r.H16CourseCodeColumn}"" AS text))) AS norm_code,
        TRIM(CAST(""{r.H16CourseCodeColumn}"" AS text)) AS course_code
    FROM ""{schema}"".""{h16Table}""
    WHERE ""{r.H16CourseCodeColumn}"" IS NOT NULL
) x ORDER BY norm_code;
CREATE INDEX ON rule25_h16(norm_code);
ANALYZE rule25_h16;

DROP TABLE IF EXISTS rule25_population;
CREATE TEMP TABLE rule25_population AS
WITH src AS (
    SELECT
        TRIM(CAST(c.""{r.CrseCourseCodeColumn}"" AS text)) AS crse_course_code,
        a.course_code AS audit_course_code,
        h.course_code AS h16_course_code
    FROM ""{schema}"".""{crseTable}"" c
    LEFT JOIN rule25_audit a ON UPPER(TRIM(CAST(c.""{r.CrseCourseCodeColumn}"" AS text))) = a.norm_code
    LEFT JOIN rule25_h16 h ON UPPER(TRIM(CAST(c.""{r.CrseCourseCodeColumn}"" AS text))) = h.norm_code
)
SELECT
    ROW_NUMBER() OVER (ORDER BY crse_course_code) AS row_number,
    crse_course_code, audit_course_code, h16_course_code,
    CASE
        WHEN h16_course_code IS NULL THEN 'MISSING_IN_H16'
        WHEN audit_course_code IS NULL THEN 'MISSING_IN_AUDIT'
        WHEN crse_course_code = h16_course_code AND crse_course_code = audit_course_code THEN 'MATCH'
        ELSE 'MISMATCH'
    END AS reconciliation_status,
    CASE
        WHEN h16_course_code IS NULL THEN 'H16 course record missing'
        WHEN audit_course_code IS NULL THEN 'Audit course record missing'
        WHEN crse_course_code = h16_course_code AND crse_course_code = audit_course_code THEN 'All course codes match'
        ELSE 'Course code mismatch across tables'
    END AS issue_description
FROM src;

CREATE INDEX ON rule25_population(reconciliation_status);
ANALYZE rule25_population;";
        }

        private static async Task<Dictionary<string, int>> GetStatusCountsAsync(NpgsqlConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT reconciliation_status, COUNT(*) FROM rule25_population GROUP BY reconciliation_status;";
            await using var reader = await command.ExecuteReaderAsync();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
                counts[reader.GetString(0)] = Convert.ToInt32(reader.GetValue(1));
            return counts;
        }

        private static async Task<List<Rule25ReconciliationRowViewModel>> LoadRowsAsync(NpgsqlConnection connection, bool matchOnly, int limit)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = matchOnly
                ? "SELECT * FROM rule25_population WHERE reconciliation_status = 'MATCH' ORDER BY row_number LIMIT @limit;"
                : "SELECT * FROM rule25_population WHERE reconciliation_status <> 'MATCH' ORDER BY row_number LIMIT @limit;";
            command.Parameters.AddWithValue("limit", limit);

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule25ReconciliationRowViewModel>();
            while (await reader.ReadAsync())
            {
                rows.Add(new Rule25ReconciliationRowViewModel
                {
                    ValidationNumber = rows.Count + 1,
                    CrseCourseCode = GetString(reader, "crse_course_code") ?? "",
                    AuditCourseCode = GetString(reader, "audit_course_code") ?? "",
                    H16CourseCode = GetString(reader, "h16_course_code") ?? "",
                    ReconciliationStatus = GetString(reader, "reconciliation_status") ?? "",
                    IssueDescription = GetString(reader, "issue_description") ?? ""
                });
            }
            return rows;
        }

        private static void ApplyBrowserPreview(Rule25ValidationSummary summary)
        {
            summary.PassSampleRows = summary.PassSampleRows.Take(BrowserPreviewRowLimit).ToList();
            summary.FailRows = summary.FailRows.Take(BrowserPreviewRowLimit).ToList();
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule25ValidationRequest request, Rule25ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 25);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 25,
                RuleName = "Reconcile Course Datasets",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.CrseTable,
                DeceasedTable = request.AuditTable,
                StudColumn = request.H16Table,
                DeceasedColumn = request.AuditCourseCodeColumn,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.FailRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Validation / helpers ─────────────────────────────────────────────────────────

        private static Rule25VerifyRequest ToVerifyRequest(Rule25ValidationRequest request) => new()
        {
            ClientId = request.ClientId,
            CrseTable = request.CrseTable,
            AuditTable = request.AuditTable,
            H16Table = request.H16Table,
            CrseCourseCodeColumn = request.CrseCourseCodeColumn,
            AuditCourseCodeColumn = request.AuditCourseCodeColumn,
            H16CourseCodeColumn = request.H16CourseCodeColumn
        };

        private async Task EnsureColumnsExistAsync(int clientId, Rule25VerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.CrseTable)) throw new InvalidOperationException("CRSE table is required.");
            if (string.IsNullOrWhiteSpace(request.AuditTable)) throw new InvalidOperationException("Audit table is required.");
            if (string.IsNullOrWhiteSpace(request.H16Table)) throw new InvalidOperationException("H16 table is required.");

            var crseColumns = await _datasets.GetValidatedColumnsAsync(clientId, request.CrseTable);
            var auditColumns = await _datasets.GetValidatedColumnsAsync(clientId, request.AuditTable);
            var h16Columns = await _datasets.GetValidatedColumnsAsync(clientId, request.H16Table);

            if (!crseColumns.Contains(request.CrseCourseCodeColumn, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Column '{request.CrseCourseCodeColumn}' was not found on table '{request.CrseTable}'.");
            if (!auditColumns.Contains(request.AuditCourseCodeColumn, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Column '{request.AuditCourseCodeColumn}' was not found on table '{request.AuditTable}'.");
            if (!h16Columns.Contains(request.H16CourseCodeColumn, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Column '{request.H16CourseCodeColumn}' was not found on table '{request.H16Table}'.");
        }

        private static string? GetString(System.Data.Common.DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            if (reader.IsDBNull(ordinal)) return null;
            var value = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static string? FindFirst(IEnumerable<string> values, IEnumerable<string> preferredExact, IEnumerable<string> preferredContains)
        {
            var list = values.ToList();
            foreach (var exact in preferredExact)
            {
                var match = list.FirstOrDefault(value => value.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            foreach (var contains in preferredContains)
            {
                var match = list.FirstOrDefault(value => value.Contains(contains, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return list.FirstOrDefault();
        }

        private static Rule25ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule25ValidationSummary>(decoded);
            }
            catch { return null; }
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase);

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
