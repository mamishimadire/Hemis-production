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
    // Rule 23: Reconcile Datasets — validates against the engagement's own uploaded Supabase data
    // instead of a live SQL Server connection. Ported from the Rule19/21 pattern. Every STUD row is
    // LEFT JOINed (by normalized student number) to an Audit dataset and an H16 dataset, then
    // classified into MATCH or one of several mismatch reasons (missing/ID mismatch/qualification
    // mismatch, H16 checked before Audit). Unlike the 100%-PASS rules, MATCH is only a sample here
    // (up to PassSampleLimit) while every mismatch (up to FailRowLimit) is kept — this mirrors the
    // original SQL Server design exactly, which already had sane caps baked in from the start.
    public class Rule23Service : IRule23Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int PassSampleLimit = 100;
        private const int FailRowLimit = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule23Service(
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

        public async Task<Rule23TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule23TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule23TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "dbo_stud", "STUD", "dbo_STUD_VALIDATION_DETAIL"], ["stud"]),
                    AutoAuditTable = FindFirst(tables, ["MT-audit-prod-std", "MT_AUDIT_PROD_STD", "mt_audit_prod_std"], ["audit", "std"]),
                    AutoH16Table = FindFirst(tables, ["H16STUD", "H16STD", "h16stud", "h16std"], ["h16", "stud"])
                };
            }
            catch (Exception ex)
            {
                return new Rule23TableDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule23AuditColumnResult> GetAuditColumnsAsync(int clientId, string auditTable)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, auditTable);
                return new Rule23AuditColumnResult
                {
                    Success = true,
                    Columns = columns,
                    AutoStudentNumberColumn = FindFirst(columns, ["IAGSTNO", "STUDNUM", "STUDENTNUMBER"], ["iagstno", "studnum", "student"]),
                    AutoQualificationColumn = FindFirst(columns, ["IAGQUAL", "QUALCODE", "QUAL"], ["iagqual", "qualcode", "qual"]),
                    AutoIdNumberColumn = FindFirst(columns, ["IADIDNO", "SAIDNUM", "IDNUMBER"], ["iadidno", "saidnum", "id"])
                };
            }
            catch (Exception ex)
            {
                return new Rule23AuditColumnResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule23VerifyResult> VerifyTablesAsync(Rule23VerifyRequest request)
        {
            try
            {
                await EnsureColumnsExistAsync(request.ClientId, request);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                return new Rule23VerifyResult
                {
                    Success = true,
                    StudCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";"),
                    AuditCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.AuditTable}\";"),
                    H16Count = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.H16Table}\";")
                };
            }
            catch (Exception ex)
            {
                return new Rule23VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule23ValidationSummary> RunValidationAsync(Rule23ValidationRequest request, string? userEmail = null, string? userName = null)
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
                return new Rule23ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule23WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 23);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule23WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable,
                AuditTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
                H16Table = string.IsNullOrWhiteSpace(row.StudColumn) ? "" : row.StudColumn,
                AuditStudentNumberColumn = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "IAGSTNO" : row.DeceasedColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (deserializedSummary != null)
            {
                workspace.StudStudentNumberColumn = deserializedSummary.StudStudentNumberColumn;
                workspace.StudQualificationColumn = deserializedSummary.StudQualificationColumn;
                workspace.StudIdNumberColumn = deserializedSummary.StudIdNumberColumn;
                workspace.AuditStudentNumberColumn = string.IsNullOrWhiteSpace(workspace.AuditStudentNumberColumn)
                    ? deserializedSummary.AuditStudentNumberColumn
                    : workspace.AuditStudentNumberColumn;
                workspace.AuditQualificationColumn = deserializedSummary.AuditQualificationColumn;
                workspace.AuditIdNumberColumn = deserializedSummary.AuditIdNumberColumn;
                workspace.H16StudentNumberColumn = deserializedSummary.H16StudentNumberColumn;
                workspace.H16QualificationColumn = deserializedSummary.H16QualificationColumn;
                workspace.H16IdNumberColumn = deserializedSummary.H16IdNumberColumn;
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

        public async Task<Rule23RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 23);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule23RunReviewViewModel
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

        public async Task<Rule23WorkspaceSaveResult> SaveWorkspaceAsync(Rule23ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0)
                    return new Rule23WorkspaceSaveResult { Success = false, Error = "Run the validation first so the workspace can be saved." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule23WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.AuditTable,
                    StudColumn = request.H16Table,
                    DeceasedColumn = request.AuditStudentNumberColumn
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule23WorkspaceSaveResult
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
                return new Rule23WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule23WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule23WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule23WorkspaceSaveResult
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
                return new Rule23WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 23 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 23 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 23 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule23ValidationRequest request)
        {
            await EnsureColumnsExistAsync(request.ClientId, ToVerifyRequest(request));

            var sql = $@"-- HEMIS RULE 23: RECONCILE DATASETS
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- STUD Table : ""{request.StudTable}""
-- Audit Table: ""{request.AuditTable}""
-- H16 Table  : ""{request.H16Table}""
-- NOTE: ID comparisons strip leading zeros (0504... = 504...) to ignore ingestion artefacts.

{BuildRule23PrepSql("{schema}", request.StudTable, request.AuditTable, request.H16Table, request)}

-- Mismatch rows
SELECT * FROM rule23_population WHERE reconciliation_status <> 'MATCH' ORDER BY row_number;

-- Summary
SELECT reconciliation_status, COUNT(*) AS issue_count
FROM rule23_population
GROUP BY reconciliation_status
ORDER BY issue_count DESC;";

            return sql.Trim();
        }

        // ── Analysis ─────────────────────────────────────────────────────────────────────

        private async Task<Rule23ValidationSummary> AnalyseAsync(Rule23ValidationRequest request)
        {
            await EnsureColumnsExistAsync(request.ClientId, ToVerifyRequest(request));
            await EnsureRule23IndexesAsync(request);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";");
            var auditCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.AuditTable}\";");
            var h16Count = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.H16Table}\";");

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule23PrepSql(schema, request.StudTable, request.AuditTable, request.H16Table, request);
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
            string? warning = failRowsTruncated
                ? $"Only the first {failRows.Count:N0} mismatch rows were saved for browser review and export performance. Total mismatches found: {mismatches:N0}."
                : null;

            return new Rule23ValidationSummary
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
                AuditTable = request.AuditTable,
                H16Table = request.H16Table,
                StudStudentNumberColumn = request.StudStudentNumberColumn,
                StudQualificationColumn = request.StudQualificationColumn,
                StudIdNumberColumn = request.StudIdNumberColumn,
                AuditStudentNumberColumn = request.AuditStudentNumberColumn,
                AuditQualificationColumn = request.AuditQualificationColumn,
                AuditIdNumberColumn = request.AuditIdNumberColumn,
                H16StudentNumberColumn = request.H16StudentNumberColumn,
                H16QualificationColumn = request.H16QualificationColumn,
                H16IdNumberColumn = request.H16IdNumberColumn,
                StudCount = studCount,
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
                    .Select(x => new Rule23IssueBreakdownItemViewModel { Status = x.Key, Count = x.Value })
                    .ToList(),
                PassSampleRows = passSampleRows,
                FailRows = failRows,
                Warning = warning
            };
        }

        private static string BuildRule23PrepSql(string schema, string studTable, string auditTable, string h16Table, Rule23ValidationRequest r)
        {
            string NormId(string col) => $@"CASE WHEN {col} IS NULL OR {col} = '' THEN {col}
             WHEN {col} ~ '^0+$' THEN '0'
             ELSE regexp_replace({col}, '^0+', '') END";

            return $@"
DROP TABLE IF EXISTS rule23_audit;
CREATE TEMP TABLE rule23_audit AS
SELECT DISTINCT ON (norm_num) norm_num, student_num, qual_code, id_num FROM (
    SELECT
        UPPER(TRIM(CAST(""{r.AuditStudentNumberColumn}"" AS text))) AS norm_num,
        TRIM(CAST(""{r.AuditStudentNumberColumn}"" AS text)) AS student_num,
        TRIM(CAST(""{r.AuditQualificationColumn}"" AS text)) AS qual_code,
        TRIM(CAST(""{r.AuditIdNumberColumn}"" AS text)) AS id_num
    FROM ""{schema}"".""{auditTable}""
    WHERE ""{r.AuditStudentNumberColumn}"" IS NOT NULL
) x
ORDER BY norm_num;
CREATE INDEX ON rule23_audit(norm_num);
ANALYZE rule23_audit;

DROP TABLE IF EXISTS rule23_h16;
CREATE TEMP TABLE rule23_h16 AS
SELECT DISTINCT ON (norm_num) norm_num, student_num, qual_code, id_num FROM (
    SELECT
        UPPER(TRIM(CAST(""{r.H16StudentNumberColumn}"" AS text))) AS norm_num,
        TRIM(CAST(""{r.H16StudentNumberColumn}"" AS text)) AS student_num,
        TRIM(CAST(""{r.H16QualificationColumn}"" AS text)) AS qual_code,
        TRIM(CAST(""{r.H16IdNumberColumn}"" AS text)) AS id_num
    FROM ""{schema}"".""{h16Table}""
    WHERE ""{r.H16StudentNumberColumn}"" IS NOT NULL
) x
ORDER BY norm_num;
CREATE INDEX ON rule23_h16(norm_num);
ANALYZE rule23_h16;

DROP TABLE IF EXISTS rule23_population;
CREATE TEMP TABLE rule23_population AS
WITH src AS (
    SELECT
        TRIM(CAST(s.""{r.StudStudentNumberColumn}"" AS text)) AS stud_student_num,
        TRIM(CAST(s.""{r.StudQualificationColumn}"" AS text)) AS stud_qual_code,
        TRIM(CAST(s.""{r.StudIdNumberColumn}"" AS text)) AS stud_id_num,
        a.student_num AS audit_student_num,
        a.qual_code AS audit_qual_code,
        a.id_num AS audit_id_num,
        h.student_num AS h16_student_num,
        h.qual_code AS h16_qual_code,
        h.id_num AS h16_id_num
    FROM ""{schema}"".""{studTable}"" s
    LEFT JOIN rule23_audit a ON UPPER(TRIM(CAST(s.""{r.StudStudentNumberColumn}"" AS text))) = a.norm_num
    LEFT JOIN rule23_h16 h ON UPPER(TRIM(CAST(s.""{r.StudStudentNumberColumn}"" AS text))) = h.norm_num
),
normalized AS (
    SELECT *,
        {NormId("stud_id_num")} AS stud_id_norm,
        {NormId("audit_id_num")} AS audit_id_norm,
        {NormId("h16_id_num")} AS h16_id_norm
    FROM src
)
SELECT
    ROW_NUMBER() OVER (ORDER BY stud_student_num) AS row_number,
    stud_student_num, stud_qual_code, stud_id_num,
    audit_student_num, audit_qual_code, audit_id_num,
    h16_student_num, h16_qual_code, h16_id_num,
    CASE
        WHEN h16_student_num IS NULL THEN 'MISSING_IN_H16'
        WHEN stud_id_norm <> h16_id_norm THEN 'ID_MISMATCH_H16'
        WHEN stud_qual_code <> h16_qual_code THEN 'QUAL_MISMATCH_H16'
        WHEN audit_student_num IS NULL THEN 'MISSING_IN_AUDIT'
        WHEN stud_id_norm <> audit_id_norm THEN 'ID_MISMATCH_AUDIT'
        WHEN stud_qual_code <> audit_qual_code THEN 'QUAL_MISMATCH_AUDIT'
        ELSE 'MATCH'
    END AS reconciliation_status,
    CASE
        WHEN h16_student_num IS NULL THEN 'H16 record missing'
        WHEN stud_id_norm <> h16_id_norm THEN 'ID mismatch with H16'
        WHEN stud_qual_code <> h16_qual_code THEN 'Qualification mismatch with H16'
        WHEN audit_student_num IS NULL THEN 'Audit record missing'
        WHEN stud_id_norm <> audit_id_norm THEN 'ID mismatch with Audit'
        WHEN stud_qual_code <> audit_qual_code THEN 'Qualification mismatch with Audit'
        ELSE 'All records match'
    END AS issue_description
FROM normalized;

CREATE INDEX ON rule23_population(reconciliation_status);
ANALYZE rule23_population;";
        }

        private static async Task<Dictionary<string, int>> GetStatusCountsAsync(NpgsqlConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT reconciliation_status, COUNT(*) FROM rule23_population GROUP BY reconciliation_status;";
            await using var reader = await command.ExecuteReaderAsync();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
                counts[reader.GetString(0)] = Convert.ToInt32(reader.GetValue(1));
            return counts;
        }

        private static async Task<List<Rule23ReconciliationRowViewModel>> LoadRowsAsync(NpgsqlConnection connection, bool matchOnly, int limit)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = matchOnly
                ? "SELECT * FROM rule23_population WHERE reconciliation_status = 'MATCH' ORDER BY row_number LIMIT @limit;"
                : "SELECT * FROM rule23_population WHERE reconciliation_status <> 'MATCH' ORDER BY row_number LIMIT @limit;";
            command.Parameters.AddWithValue("limit", limit);

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule23ReconciliationRowViewModel>();
            while (await reader.ReadAsync())
            {
                rows.Add(new Rule23ReconciliationRowViewModel
                {
                    ValidationNumber = rows.Count + 1,
                    StudStudentNumber = GetString(reader, "stud_student_num") ?? "",
                    StudQualificationCode = GetString(reader, "stud_qual_code") ?? "",
                    StudIdNumber = GetString(reader, "stud_id_num") ?? "",
                    AuditStudentNumber = GetString(reader, "audit_student_num") ?? "",
                    AuditQualificationCode = GetString(reader, "audit_qual_code") ?? "",
                    AuditIdNumber = GetString(reader, "audit_id_num") ?? "",
                    H16StudentNumber = GetString(reader, "h16_student_num") ?? "",
                    H16QualificationCode = GetString(reader, "h16_qual_code") ?? "",
                    H16IdNumber = GetString(reader, "h16_id_num") ?? "",
                    ReconciliationStatus = GetString(reader, "reconciliation_status") ?? "",
                    IssueDescription = GetString(reader, "issue_description") ?? ""
                });
            }
            return rows;
        }

        private static void ApplyBrowserPreview(Rule23ValidationSummary summary)
        {
            summary.PassSampleRows = summary.PassSampleRows.Take(BrowserPreviewRowLimit).ToList();
            summary.FailRows = summary.FailRows.Take(BrowserPreviewRowLimit).ToList();
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule23ValidationRequest request, Rule23ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 23);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 23,
                RuleName = "Reconcile Datasets",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.AuditTable,
                StudColumn = request.H16Table,
                DeceasedColumn = request.AuditStudentNumberColumn,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.FailRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Validation / helpers ─────────────────────────────────────────────────────────

        private static Rule23VerifyRequest ToVerifyRequest(Rule23ValidationRequest request) => new()
        {
            ClientId = request.ClientId,
            StudTable = request.StudTable,
            AuditTable = request.AuditTable,
            H16Table = request.H16Table,
            StudStudentNumberColumn = request.StudStudentNumberColumn,
            StudQualificationColumn = request.StudQualificationColumn,
            StudIdNumberColumn = request.StudIdNumberColumn,
            AuditStudentNumberColumn = request.AuditStudentNumberColumn,
            AuditQualificationColumn = request.AuditQualificationColumn,
            AuditIdNumberColumn = request.AuditIdNumberColumn,
            H16StudentNumberColumn = request.H16StudentNumberColumn,
            H16QualificationColumn = request.H16QualificationColumn,
            H16IdNumberColumn = request.H16IdNumberColumn
        };

        private async Task EnsureColumnsExistAsync(int clientId, Rule23VerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.StudTable)) throw new InvalidOperationException("STUD table is required.");
            if (string.IsNullOrWhiteSpace(request.AuditTable)) throw new InvalidOperationException("Audit table is required.");
            if (string.IsNullOrWhiteSpace(request.H16Table)) throw new InvalidOperationException("H16 table is required.");

            var studColumns = await _datasets.GetValidatedColumnsAsync(clientId, request.StudTable);
            var auditColumns = await _datasets.GetValidatedColumnsAsync(clientId, request.AuditTable);
            var h16Columns = await _datasets.GetValidatedColumnsAsync(clientId, request.H16Table);

            foreach (var studColumn in new[] { request.StudStudentNumberColumn, request.StudQualificationColumn, request.StudIdNumberColumn })
                if (!studColumns.Contains(studColumn, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Column '{studColumn}' was not found on table '{request.StudTable}'.");

            foreach (var auditColumn in new[] { request.AuditStudentNumberColumn, request.AuditQualificationColumn, request.AuditIdNumberColumn })
                if (!auditColumns.Contains(auditColumn, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Column '{auditColumn}' was not found on table '{request.AuditTable}'.");

            foreach (var h16Column in new[] { request.H16StudentNumberColumn, request.H16QualificationColumn, request.H16IdNumberColumn })
                if (!h16Columns.Contains(h16Column, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Column '{h16Column}' was not found on table '{request.H16Table}'.");
        }

        private async Task EnsureRule23IndexesAsync(Rule23ValidationRequest request)
        {
            await _datasets.EnsureJoinIndexAsync(request.ClientId, request.StudTable, request.StudStudentNumberColumn);
            await _datasets.EnsureJoinIndexAsync(request.ClientId, request.AuditTable, request.AuditStudentNumberColumn);
            await _datasets.EnsureJoinIndexAsync(request.ClientId, request.H16Table, request.H16StudentNumberColumn);
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

        private static Rule23ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule23ValidationSummary>(decoded);
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
