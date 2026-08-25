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
    // Rule 24: Reconcile Qualification Datasets — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. Ported from the Rule23 pattern. Every
    // STUD row is joined (via its qualification code) to QUAL, then QUAL's code is cross-checked
    // against an Audit dataset and an H16 dataset. STUD is the driving table so the population is
    // one row per STUD record — the original SQL Server design pulled every one of those rows into
    // memory with zero cap on the FAIL side (only the 100-row PASS sample was bounded), which is
    // the same unbounded-load risk that caused Rule18's OutOfMemoryException. FailRowLimit is
    // introduced here from the start, matching the safety cap already proven on Rule23.
    public class Rule24Service : IRule24Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int PassSampleLimit = 100;
        private const int FailRowLimit = 5000;
        private const string StudQualCodeColumn = "_001";

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule24Service(
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

        public async Task<Rule24TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule24TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule24TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "dbo_stud", "STUD"], ["stud"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "dbo_qual", "QUAL", "dbo_QUAL_VALIDATION_DETAIL"], ["qual"]),
                    AutoAuditTable = FindFirst(tables, ["MT-audit-prod-QUAL", "MT_AUDIT_PROD_QUAL", "mt_audit_prod_qual"], ["audit", "qual"]),
                    AutoH16Table = FindFirst(tables, ["H16QUAL", "H16QUA", "h16qual"], ["h16", "qual"])
                };
            }
            catch (Exception ex)
            {
                return new Rule24TableDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule24AuditColumnResult> GetAuditColumnsAsync(int clientId, string auditTable)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, auditTable);
                return new Rule24AuditColumnResult
                {
                    Success = true,
                    Columns = columns,
                    AutoQualCodeColumn = FindFirst(columns, ["IAIQUAL", "QUALCODE", "_001"], ["iaiqual", "qualcode", "qual"])
                };
            }
            catch (Exception ex)
            {
                return new Rule24AuditColumnResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule24VerifyResult> VerifyTablesAsync(Rule24VerifyRequest request)
        {
            try
            {
                EnsureAuditTableLooksCorrect(request.AuditTable);
                await EnsureColumnsExistAsync(request.ClientId, request);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                return new Rule24VerifyResult
                {
                    Success = true,
                    QualCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.QualTable}\";"),
                    AuditCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.AuditTable}\";"),
                    H16Count = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.H16Table}\";")
                };
            }
            catch (Exception ex)
            {
                return new Rule24VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule24ValidationSummary> RunValidationAsync(Rule24ValidationRequest request, string? userEmail = null, string? userName = null)
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
                return new Rule24ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule24WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 24);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule24WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = "dbo_STUD",
                QualTable = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                AuditTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
                H16Table = string.IsNullOrWhiteSpace(row.StudColumn) ? "" : row.StudColumn,
                AuditQualCodeColumn = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "IAIQUAL" : row.DeceasedColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (deserializedSummary != null)
            {
                workspace.StudTable = deserializedSummary.StudTable;
                workspace.QualCodeColumn = deserializedSummary.QualCodeColumn;
                workspace.ApprovalStatusColumn = deserializedSummary.ApprovalStatusColumn;
                workspace.ExcludedApprovalStatusValue = deserializedSummary.ExcludedApprovalStatusValue;
                workspace.Control1OnlyMode = deserializedSummary.Control1OnlyMode;
                workspace.H16QualCodeColumn = deserializedSummary.H16QualCodeColumn;
                workspace.AuditQualCodeColumn = string.IsNullOrWhiteSpace(workspace.AuditQualCodeColumn)
                    ? deserializedSummary.AuditQualCodeColumn
                    : workspace.AuditQualCodeColumn;
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

        public async Task<Rule24RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 24);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule24RunReviewViewModel
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

        public async Task<Rule24WorkspaceSaveResult> SaveWorkspaceAsync(Rule24ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0)
                    return new Rule24WorkspaceSaveResult { Success = false, Error = "Run the validation first so the workspace can be saved." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule24WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.QualTable,
                    DeceasedTable = request.AuditTable,
                    StudColumn = request.H16Table,
                    DeceasedColumn = request.AuditQualCodeColumn
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule24WorkspaceSaveResult
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
                return new Rule24WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule24WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule24WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule24WorkspaceSaveResult
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
                return new Rule24WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 24 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 24 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 24 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule24ValidationRequest request)
        {
            EnsureAuditTableLooksCorrect(request.AuditTable);
            await EnsureColumnsExistAsync(request.ClientId, ToVerifyRequest(request));

            var sql = $@"-- HEMIS RULE 24: RECONCILE QUALIFICATION DATASETS
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- STUD Table : ""{request.StudTable}""  |  Join key: STUD.""{StudQualCodeColumn}""
-- QUAL Table : ""{request.QualTable}""  |  Code: ""{request.QualCodeColumn}""  |  Approval: ""{request.ApprovalStatusColumn}""
-- Audit Table: ""{request.AuditTable}""  |  Code: ""{request.AuditQualCodeColumn}""
-- H16 Table  : ""{request.H16Table}""  |  Code: ""{request.H16QualCodeColumn}""
-- Validation Mode: {(request.Control1OnlyMode ? "CONTROL_1_ONLY" : "STANDARD")}

{BuildRule24PrepSql("{schema}", request.StudTable, request.QualTable, request.AuditTable, request.H16Table, request)}

-- Mismatch rows
SELECT * FROM rule24_population WHERE reconciliation_status <> '{(request.Control1OnlyMode ? "NO_BLANKS" : "MATCH")}' ORDER BY row_number;

-- Summary
SELECT reconciliation_status, COUNT(*) AS issue_count
FROM rule24_population
GROUP BY reconciliation_status
ORDER BY issue_count DESC;";

            return sql.Trim();
        }

        // Re-runs the same analysis a fresh "Run Validation" would, without the save-run side
        // effect - used by the Excel export path.
        public async Task<Rule24ValidationSummary> GetExportSummaryAsync(Rule24ValidationRequest request) =>
            await AnalyseAsync(request);

        // Cheap population size check - stops at a COUNT(*), no result rows loaded.
        public async Task<int> GetPopulationCountAsync(Rule24ValidationRequest request)
        {
            EnsureAuditTableLooksCorrect(request.AuditTable);
            await EnsureColumnsExistAsync(request.ClientId, ToVerifyRequest(request));

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule24PrepSql(schema, request.StudTable, request.QualTable, request.AuditTable, request.H16Table, request);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var passStatus = request.Control1OnlyMode ? "NO_BLANKS" : "MATCH";
            var statusCounts = await GetStatusCountsAsync(connection);
            var totalValidated = statusCounts.Values.Sum();
            var matches = statusCounts.GetValueOrDefault(passStatus);
            var mismatches = totalValidated - matches;
            return mismatches + Math.Min(matches, PassSampleLimit);
        }

        // Bypasses AnalyseAsync/LoadRowsAsync entirely for the mismatch side - no cap. The
        // matching side stays a deliberate fixed-size sample (PassSampleLimit). Mirrors
        // Rule12Service.StreamCsvExportAsync.
        public async Task StreamCsvExportAsync(Rule24ValidationRequest request, Stream outputStream)
        {
            EnsureAuditTableLooksCorrect(request.AuditTable);
            await EnsureColumnsExistAsync(request.ClientId, ToVerifyRequest(request));

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule24PrepSql(schema, request.StudTable, request.QualTable, request.AuditTable, request.H16Table, request);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var passStatus = request.Control1OnlyMode ? "NO_BLANKS" : "MATCH";

            await using var writer = new StreamWriter(outputStream, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = false };

            await using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT * FROM ( SELECT * FROM rule24_population WHERE reconciliation_status <> @status ORDER BY row_number ) mismatches
UNION ALL
SELECT * FROM ( SELECT * FROM rule24_population WHERE reconciliation_status = @status ORDER BY row_number LIMIT {PassSampleLimit} ) match_sample;";
            command.Parameters.AddWithValue("status", passStatus);
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

        private async Task<Rule24ValidationSummary> AnalyseAsync(Rule24ValidationRequest request)
        {
            EnsureAuditTableLooksCorrect(request.AuditTable);
            await EnsureColumnsExistAsync(request.ClientId, ToVerifyRequest(request));

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var qualCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.QualTable}\";");
            var auditCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.AuditTable}\";");
            var h16Count = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.H16Table}\";");

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule24PrepSql(schema, request.StudTable, request.QualTable, request.AuditTable, request.H16Table, request);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var passStatus = request.Control1OnlyMode ? "NO_BLANKS" : "MATCH";
            var statusCounts = await GetStatusCountsAsync(connection);
            var totalValidated = statusCounts.Values.Sum();
            var matches = statusCounts.GetValueOrDefault(passStatus);
            var mismatches = totalValidated - matches;

            var passSampleRows = await LoadRowsAsync(connection, passStatus, matchOnly: true, PassSampleLimit);
            var failRows = await LoadRowsAsync(connection, passStatus, matchOnly: false, FailRowLimit);

            var exceptionRate = totalValidated == 0 ? 0m : Math.Round((decimal)mismatches / totalValidated * 100m, 2);
            var matchRate = totalValidated == 0 ? 0m : Math.Round((decimal)matches / totalValidated * 100m, 2);
            var failRowsTruncated = mismatches > failRows.Count;

            return new Rule24ValidationSummary
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
                StudTable = request.StudTable,
                QualTable = request.QualTable,
                AuditTable = request.AuditTable,
                H16Table = request.H16Table,
                QualCodeColumn = request.QualCodeColumn,
                ApprovalStatusColumn = request.ApprovalStatusColumn,
                ExcludedApprovalStatusValue = request.ExcludedApprovalStatusValue,
                Control1OnlyMode = request.Control1OnlyMode,
                AuditQualCodeColumn = request.AuditQualCodeColumn,
                H16QualCodeColumn = request.H16QualCodeColumn,
                QualCount = qualCount,
                AuditCount = auditCount,
                H16Count = h16Count,
                ClientId = request.ClientId,
                PassSampleCount = passSampleRows.Count,
                PassSampleTruncated = matches > passSampleRows.Count,
                SavedFailRowCount = failRows.Count,
                FailRowsTruncated = failRowsTruncated,
                IssueCounts = statusCounts
                    .Where(x => !string.Equals(x.Key, passStatus, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new Rule24IssueBreakdownItemViewModel { Status = x.Key, Count = x.Value })
                    .ToList(),
                PassSampleRows = passSampleRows,
                FailRows = failRows,
                Warning = failRowsTruncated
                    ? $"Only the first {failRows.Count:N0} mismatch rows were saved for browser review and export performance. Total mismatches found: {mismatches:N0}."
                    : null
            };
        }

        private static string BuildRule24PrepSql(string schema, string studTable, string qualTable, string auditTable, string h16Table, Rule24ValidationRequest r)
        {
            var excludedVal = EscapeSqlString(r.ExcludedApprovalStatusValue);

            string reconciliationStatusSql = r.Control1OnlyMode
                ? @"CASE
        WHEN h16_qual_code IS NULL THEN 'MISSING_IN_H16'
        WHEN audit_qual_code IS NULL THEN 'MISSING_IN_AUDIT'
        ELSE 'NO_BLANKS'
    END"
                : $@"CASE
        WHEN h16_qual_code IS NULL THEN 'MISSING_IN_H16'
        WHEN audit_qual_code IS NULL THEN 'MISSING_IN_AUDIT'
        WHEN qual_qual_code = stud_qual_code
         AND qual_approval_status <> '{excludedVal}'
         AND h16_qual_code IS NOT NULL
         AND audit_qual_code IS NOT NULL THEN 'MATCH'
        ELSE 'MISMATCH'
    END";

            string issueDescriptionSql = r.Control1OnlyMode
                ? @"CASE
        WHEN h16_qual_code IS NULL THEN 'H16 qualification record missing'
        WHEN audit_qual_code IS NULL THEN 'Audit qualification record missing'
        ELSE 'No blanks found in H16 or Audit'
    END"
                : $@"CASE
        WHEN h16_qual_code IS NULL THEN 'H16 qualification record missing'
        WHEN audit_qual_code IS NULL THEN 'Audit qualification record missing'
        WHEN qual_qual_code = stud_qual_code
         AND qual_approval_status <> '{excludedVal}'
         AND h16_qual_code IS NOT NULL
         AND audit_qual_code IS NOT NULL THEN 'All qualifications match'
        ELSE 'Qualification code mismatch'
    END";

            string controlTypeSql = r.Control1OnlyMode
                ? "'CONTROL_1'"
                : $@"CASE
        WHEN h16_qual_code IS NULL OR audit_qual_code IS NULL THEN 'CONTROL_1'
        WHEN qual_qual_code = stud_qual_code
         AND qual_approval_status <> '{excludedVal}' THEN 'CONTROL_2'
        ELSE 'CONTROL_1'
    END";

            return $@"
DROP TABLE IF EXISTS rule24_qual;
CREATE TEMP TABLE rule24_qual AS
SELECT DISTINCT ON (norm_code) norm_code, qual_code, approval_status FROM (
    SELECT
        UPPER(TRIM(CAST(""{r.QualCodeColumn}"" AS text))) AS norm_code,
        TRIM(CAST(""{r.QualCodeColumn}"" AS text)) AS qual_code,
        TRIM(CAST(""{r.ApprovalStatusColumn}"" AS text)) AS approval_status
    FROM ""{schema}"".""{qualTable}""
    WHERE ""{r.QualCodeColumn}"" IS NOT NULL
) x ORDER BY norm_code;
CREATE INDEX ON rule24_qual(norm_code);
ANALYZE rule24_qual;

DROP TABLE IF EXISTS rule24_audit;
CREATE TEMP TABLE rule24_audit AS
SELECT DISTINCT ON (norm_code) norm_code, qual_code FROM (
    SELECT
        UPPER(TRIM(CAST(""{r.AuditQualCodeColumn}"" AS text))) AS norm_code,
        TRIM(CAST(""{r.AuditQualCodeColumn}"" AS text)) AS qual_code
    FROM ""{schema}"".""{auditTable}""
    WHERE ""{r.AuditQualCodeColumn}"" IS NOT NULL
) x ORDER BY norm_code;
CREATE INDEX ON rule24_audit(norm_code);
ANALYZE rule24_audit;

DROP TABLE IF EXISTS rule24_h16;
CREATE TEMP TABLE rule24_h16 AS
SELECT DISTINCT ON (norm_code) norm_code, qual_code FROM (
    SELECT
        UPPER(TRIM(CAST(""{r.H16QualCodeColumn}"" AS text))) AS norm_code,
        TRIM(CAST(""{r.H16QualCodeColumn}"" AS text)) AS qual_code
    FROM ""{schema}"".""{h16Table}""
    WHERE ""{r.H16QualCodeColumn}"" IS NOT NULL
) x ORDER BY norm_code;
CREATE INDEX ON rule24_h16(norm_code);
ANALYZE rule24_h16;

DROP TABLE IF EXISTS rule24_population;
CREATE TEMP TABLE rule24_population AS
WITH src AS (
    SELECT
        TRIM(CAST(s.""{StudQualCodeColumn}"" AS text)) AS stud_qual_code,
        q.qual_code AS qual_qual_code,
        q.approval_status AS qual_approval_status,
        a.qual_code AS audit_qual_code,
        h.qual_code AS h16_qual_code
    FROM ""{schema}"".""{studTable}"" s
    LEFT JOIN rule24_qual q ON UPPER(TRIM(CAST(s.""{StudQualCodeColumn}"" AS text))) = q.norm_code
    LEFT JOIN rule24_audit a ON q.norm_code = a.norm_code
    LEFT JOIN rule24_h16 h ON q.norm_code = h.norm_code
)
SELECT
    ROW_NUMBER() OVER (ORDER BY stud_qual_code) AS row_number,
    stud_qual_code, qual_qual_code, qual_approval_status, audit_qual_code, h16_qual_code,
    {reconciliationStatusSql} AS reconciliation_status,
    {issueDescriptionSql} AS issue_description,
    {controlTypeSql} AS control_type
FROM src;

CREATE INDEX ON rule24_population(reconciliation_status);
ANALYZE rule24_population;";
        }

        private static async Task<Dictionary<string, int>> GetStatusCountsAsync(NpgsqlConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT reconciliation_status, COUNT(*) FROM rule24_population GROUP BY reconciliation_status;";
            await using var reader = await command.ExecuteReaderAsync();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
                counts[reader.GetString(0)] = Convert.ToInt32(reader.GetValue(1));
            return counts;
        }

        private static async Task<List<Rule24ReconciliationRowViewModel>> LoadRowsAsync(NpgsqlConnection connection, string passStatus, bool matchOnly, int limit)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = matchOnly
                ? "SELECT * FROM rule24_population WHERE reconciliation_status = @status ORDER BY row_number LIMIT @limit;"
                : "SELECT * FROM rule24_population WHERE reconciliation_status <> @status ORDER BY row_number LIMIT @limit;";
            command.Parameters.AddWithValue("status", passStatus);
            command.Parameters.AddWithValue("limit", limit);

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule24ReconciliationRowViewModel>();
            while (await reader.ReadAsync())
            {
                rows.Add(new Rule24ReconciliationRowViewModel
                {
                    ValidationNumber = rows.Count + 1,
                    QualQualCode = GetString(reader, "qual_qual_code") ?? "",
                    QualApprovalStatus = GetString(reader, "qual_approval_status") ?? "",
                    StudQualCode = GetString(reader, "stud_qual_code") ?? "",
                    AuditQualCode = GetString(reader, "audit_qual_code") ?? "",
                    H16QualCode = GetString(reader, "h16_qual_code") ?? "",
                    ReconciliationStatus = GetString(reader, "reconciliation_status") ?? "",
                    IssueDescription = GetString(reader, "issue_description") ?? "",
                    ControlType = GetString(reader, "control_type") ?? ""
                });
            }
            return rows;
        }

        private static void ApplyBrowserPreview(Rule24ValidationSummary summary)
        {
            summary.PassSampleRows = summary.PassSampleRows.Take(BrowserPreviewRowLimit).ToList();
            summary.FailRows = summary.FailRows.Take(BrowserPreviewRowLimit).ToList();
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule24ValidationRequest request, Rule24ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 24);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 24,
                RuleName = "Reconcile Qualification Datasets",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.QualTable,
                DeceasedTable = request.AuditTable,
                StudColumn = request.H16Table,
                DeceasedColumn = request.AuditQualCodeColumn,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.FailRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Validation / helpers ─────────────────────────────────────────────────────────

        private static Rule24VerifyRequest ToVerifyRequest(Rule24ValidationRequest request) => new()
        {
            ClientId = request.ClientId,
            StudTable = request.StudTable,
            QualTable = request.QualTable,
            AuditTable = request.AuditTable,
            H16Table = request.H16Table,
            QualCodeColumn = request.QualCodeColumn,
            ApprovalStatusColumn = request.ApprovalStatusColumn,
            ExcludedApprovalStatusValue = request.ExcludedApprovalStatusValue,
            Control1OnlyMode = request.Control1OnlyMode,
            AuditQualCodeColumn = request.AuditQualCodeColumn,
            H16QualCodeColumn = request.H16QualCodeColumn
        };

        private async Task EnsureColumnsExistAsync(int clientId, Rule24VerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.StudTable)) throw new InvalidOperationException("STUD table is required.");
            if (string.IsNullOrWhiteSpace(request.QualTable)) throw new InvalidOperationException("QUAL table is required.");
            if (string.IsNullOrWhiteSpace(request.AuditTable)) throw new InvalidOperationException("Audit table is required.");
            if (string.IsNullOrWhiteSpace(request.H16Table)) throw new InvalidOperationException("H16 table is required.");

            var studColumns = await _datasets.GetValidatedColumnsAsync(clientId, request.StudTable);
            var qualColumns = await _datasets.GetValidatedColumnsAsync(clientId, request.QualTable);
            var auditColumns = await _datasets.GetValidatedColumnsAsync(clientId, request.AuditTable);
            var h16Columns = await _datasets.GetValidatedColumnsAsync(clientId, request.H16Table);

            if (!studColumns.Contains(StudQualCodeColumn, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Column '{StudQualCodeColumn}' was not found on table '{request.StudTable}'.");
            if (!qualColumns.Contains(request.QualCodeColumn, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Column '{request.QualCodeColumn}' was not found on table '{request.QualTable}'.");
            if (!qualColumns.Contains(request.ApprovalStatusColumn, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Column '{request.ApprovalStatusColumn}' was not found on table '{request.QualTable}'.");
            if (!auditColumns.Contains(request.AuditQualCodeColumn, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Column '{request.AuditQualCodeColumn}' was not found on table '{request.AuditTable}'.");
            if (!h16Columns.Contains(request.H16QualCodeColumn, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Column '{request.H16QualCodeColumn}' was not found on table '{request.H16Table}'.");
        }

        private static void EnsureAuditTableLooksCorrect(string auditTable)
        {
            var value = auditTable ?? "";
            if (value.Contains("STUD", StringComparison.OrdinalIgnoreCase) || value.Contains("CRSE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected audit table is not a qualification audit table. Use MT-audit-prod-QUAL.");
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

        private static string EscapeSqlString(string? value) => (value ?? "").Replace("'", "''");

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

        private static Rule24ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule24ValidationSummary>(decoded);
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
