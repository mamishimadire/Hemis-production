using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 36: Deceased Students Validation — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. A student table is LEFT JOINed against
    // a deceased-records table on a matching column; any student found in the deceased list fails.
    // The original SQL-Server design loaded every student row into memory with no cap — RowLimit is
    // introduced here from the start, matching the house style established for every rule this
    // session. Join keys are trimmed (not just converted to text) before comparison, matching the
    // whitespace-false-mismatch fix applied earlier this session to the Rule23/24/25 reconciliation
    // rules.
    public class Rule36Service : IRule36Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int RowLimit = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule36Service(
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

        public async Task<TableListResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new TableListResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                var autoStud = tables.FirstOrDefault(t => t.Equals("dbo_STUD", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Contains("stud", StringComparison.OrdinalIgnoreCase));
                var autoDec = tables.FirstOrDefault(t => t.Contains("deceased", StringComparison.OrdinalIgnoreCase));

                return new TableListResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = autoStud,
                    AutoDeceasedTable = autoDec
                };
            }
            catch (Exception ex)
            {
                return new TableListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule36ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName, bool isStudTable)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);

                string? autoSelected;
                string Normalize(string value) => value.Trim().ToLowerInvariant();
                var normalizedColumns = columns
                    .Select(c => new { Original = c, Normalized = Normalize(c) })
                    .ToList();

                if (isStudTable)
                {
                    autoSelected =
                        columns.FirstOrDefault(c => c.Equals("_007", StringComparison.OrdinalIgnoreCase)) ??
                        normalizedColumns.FirstOrDefault(c =>
                            c.Normalized.Contains("007") ||
                            c.Normalized.Contains("student") ||
                            c.Normalized.Contains("stud") ||
                            c.Normalized.EndsWith("id") ||
                            c.Normalized.Contains("number") ||
                            c.Normalized.Contains("code"))?.Original;
                }
                else
                {
                    var priorities = new[]
                    {
                        "STUDENT_INUMBER",
                        "STUDENT_NUMBER",
                        "STUDENT_ID",
                        "STUDENT_NO",
                        "STUDENTNUMBER",
                        "STUDENT_ID_NO",
                        "_007"
                    };

                    autoSelected = priorities
                        .Select(p => columns.FirstOrDefault(c => c.Equals(p, StringComparison.OrdinalIgnoreCase)))
                        .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

                    autoSelected ??= normalizedColumns.FirstOrDefault(c =>
                        c.Normalized.Contains("student") ||
                        c.Normalized.Contains("stud") ||
                        c.Normalized.Contains("id") ||
                        c.Normalized.Contains("number") ||
                        c.Normalized.Contains("code") ||
                        c.Normalized.Contains("inumber"))?.Original;
                }

                if (string.IsNullOrWhiteSpace(autoSelected) && columns.Count > 0)
                    autoSelected = columns[0];

                return new Rule36ColumnSelectionResult
                {
                    Success = true,
                    Columns = columns,
                    AutoSelected = autoSelected
                };
            }
            catch (Exception ex)
            {
                return new Rule36ColumnSelectionResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule36VerifyResult> VerifyDataAsync(Rule36VerifyRequest request)
        {
            try
            {
                ValidateNames(request.StudTable, request.DeceasedTable, request.StudColumn, request.DeceasedColumn);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var studTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";");
                var deceasedTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.DeceasedTable}\";");

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
WITH deceased_keys AS
(
    SELECT DISTINCT TRIM(CAST(d.""{request.DeceasedColumn}"" AS text)) AS deceased_key
    FROM ""{schema}"".""{request.DeceasedTable}"" d
    WHERE d.""{request.DeceasedColumn}"" IS NOT NULL
)
SELECT COUNT(*)
FROM ""{schema}"".""{request.StudTable}"" s
INNER JOIN deceased_keys dk ON dk.deceased_key = TRIM(CAST(s.""{request.StudColumn}"" AS text));";
                var matching = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                return new Rule36VerifyResult
                {
                    Success = true,
                    StudTotal = studTotal,
                    DeceasedTotal = deceasedTotal,
                    MatchingRecords = matching
                };
            }
            catch (Exception ex)
            {
                return new Rule36VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule36ValidationSummary> RunValidationAsync(Rule36ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateNames(request.StudTable, request.DeceasedTable, request.StudColumn, request.DeceasedColumn);

                var summary = await AnalyseAsync(request);
                if (summary.Success && request.ClientId > 0)
                {
                    try
                    {
                        summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName);
                    }
                    catch (Exception ex)
                    {
                        summary.Warning = $"Analysis completed, but the workspace could not be saved automatically: {ex.Message}";
                    }
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule36ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule36WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 36);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null)
                ApplyBrowserPreview(summary);

            var workspace = new Rule36WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                DeceasedTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
                StudColumn = string.IsNullOrWhiteSpace(row.StudColumn) ? "" : row.StudColumn,
                DeceasedColumn = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "" : row.DeceasedColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

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

            if (workspace.Summary != null)
                workspace.Summary.SavedRunId = workspace.RunId;

            if (string.IsNullOrWhiteSpace(workspace.CurrentStatus))
                workspace.CurrentStatus = workspace.Summary?.Status ?? "";

            return workspace;
        }

        public async Task<Rule36RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 36);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule36RunReviewViewModel
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

        public async Task<Rule36WorkspaceSaveResult> SaveWorkspaceAsync(Rule36ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule36WorkspaceSaveResult { Success = false, Error = "Run validation before saving the workspace." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule36WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.DeceasedTable,
                    StudColumn = request.StudColumn,
                    DeceasedColumn = request.DeceasedColumn
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule36WorkspaceSaveResult
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
                return new Rule36WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule36WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule36WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule36WorkspaceSaveResult
                {
                    Success = true,
                    Message = clearedSignoffs > 0
                        ? "Editing has begun. Existing signoffs were removed so the workspace must be reviewed again."
                        : "Editing has begun.",
                    SignoffsCleared = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace = workspace
                };
            }
            catch (Exception ex)
            {
                return new Rule36WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 36 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 36 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 36 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public string GenerateSql(Rule36ValidationRequest request)
        {
            var schema = $"engagement_{request.ClientId}";
            var st = request.StudTable;
            var dt = request.DeceasedTable;
            var sc = request.StudColumn;
            var dc = request.DeceasedColumn;

            return $@"-- ============================================================================
-- HEMIS 2025 - RULE 36: DECEASED STUDENTS VALIDATION
-- Source: this engagement's own uploaded tables (schema ""{schema}""), not a live SQL Server.
-- ============================================================================
-- Purpose: Identify deceased students by matching ""{sc}"" against ""{dt}""
-- Tables: ""{st}"" and ""{dt}""
-- Columns: ""{st}"".""{sc}"" = ""{dt}"".""{dc}""
-- ============================================================================

DROP TABLE IF EXISTS rule36_validation_results;

WITH deceased_keys AS
(
    SELECT DISTINCT TRIM(CAST(d.""{dc}"" AS text)) AS deceased_key
    FROM ""{schema}"".""{dt}"" d
    WHERE d.""{dc}"" IS NOT NULL
)
SELECT
    ROW_NUMBER() OVER (ORDER BY s.""{sc}"") AS validation_number,
    CASE WHEN dk.deceased_key IS NOT NULL THEN 'FAIL' ELSE 'PASS' END AS validation_result,
    CASE WHEN dk.deceased_key IS NOT NULL THEN 'Student marked as deceased' ELSE NULL END AS exception_reason,
    TRIM(CAST(s.""{sc}"" AS text)) AS stud_column_value
INTO TEMP TABLE rule36_validation_results
FROM ""{schema}"".""{st}"" s
LEFT JOIN deceased_keys dk ON dk.deceased_key = TRIM(CAST(s.""{sc}"" AS text))
ORDER BY s.""{sc}"";

SELECT
    COUNT(*) AS total_validated,
    SUM(CASE WHEN validation_result = 'PASS' THEN 1 ELSE 0 END) AS pass_count,
    SUM(CASE WHEN validation_result = 'FAIL' THEN 1 ELSE 0 END) AS fail_count,
    ROUND(SUM(CASE WHEN validation_result = 'FAIL' THEN 1 ELSE 0 END) * 100.0 / COUNT(*), 2) AS exception_rate_percent
FROM rule36_validation_results;

SELECT
    validation_number,
    stud_column_value,
    exception_reason,
    validation_result
FROM rule36_validation_results
WHERE validation_result = 'FAIL'
ORDER BY validation_number;

DROP TABLE rule36_validation_results;
-- ============================================================================
-- END OF RULE 36 DECEASED STUDENTS VALIDATION
-- ============================================================================
";
        }

        // ── Analysis ─────────────────────────────────────────────────────────────────────

        private async Task<Rule36ValidationSummary> AnalyseAsync(Rule36ValidationRequest request)
        {
            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var total = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";");

            await using var failCountCmd = connection.CreateCommand();
            failCountCmd.CommandText = $@"
WITH deceased_keys AS
(
    SELECT DISTINCT TRIM(CAST(d.""{request.DeceasedColumn}"" AS text)) AS deceased_key
    FROM ""{schema}"".""{request.DeceasedTable}"" d
    WHERE d.""{request.DeceasedColumn}"" IS NOT NULL
)
SELECT COUNT(*)
FROM ""{schema}"".""{request.StudTable}"" s
INNER JOIN deceased_keys dk ON dk.deceased_key = TRIM(CAST(s.""{request.StudColumn}"" AS text));";
            var failCount = Convert.ToInt32(await failCountCmd.ExecuteScalarAsync());
            var passCount = Math.Max(total - failCount, 0);

            var rows = new List<Rule36ValidationRowRecord>();
            var rowsTruncated = false;
            await using (var dataCmd = connection.CreateCommand())
            {
                dataCmd.CommandText = $@"
WITH deceased_keys AS
(
    SELECT DISTINCT TRIM(CAST(d.""{request.DeceasedColumn}"" AS text)) AS deceased_key
    FROM ""{schema}"".""{request.DeceasedTable}"" d
    WHERE d.""{request.DeceasedColumn}"" IS NOT NULL
)
SELECT
    ROW_NUMBER() OVER (ORDER BY s.""{request.StudColumn}"") AS validation_number,
    CASE WHEN dk.deceased_key IS NOT NULL THEN 'FAIL' ELSE 'PASS' END AS validation_result,
    CASE WHEN dk.deceased_key IS NOT NULL THEN 'Student marked as deceased' ELSE NULL END AS exception_reason,
    TRIM(CAST(s.""{request.StudColumn}"" AS text)) AS stud_column_value
FROM ""{schema}"".""{request.StudTable}"" s
LEFT JOIN deceased_keys dk ON dk.deceased_key = TRIM(CAST(s.""{request.StudColumn}"" AS text))
ORDER BY s.""{request.StudColumn}""
LIMIT @limit;";
                dataCmd.Parameters.AddWithValue("limit", RowLimit + 1);

                await using var reader = await dataCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (rows.Count >= RowLimit)
                    {
                        rowsTruncated = true;
                        break;
                    }

                    rows.Add(new Rule36ValidationRowRecord
                    {
                        ValidationNumber = Convert.ToInt32(reader.GetInt64(0)),
                        ValidationResult = reader.GetString(1),
                        ExceptionReason = reader.IsDBNull(2) ? null : reader.GetString(2),
                        StudentId = reader.IsDBNull(3) ? "" : reader.GetString(3)
                    });
                }
            }

            var exceptions = rows
                .Where(r => r.ValidationResult == "FAIL")
                .Select(r => new Rule36ExceptionRecord
                {
                    ValidationNumber = r.ValidationNumber,
                    StudentId = r.StudentId,
                    ExceptionReason = r.ExceptionReason ?? "Student marked as deceased",
                    ValidationResult = r.ValidationResult
                })
                .ToList();

            var rate = total > 0 ? Math.Round((decimal)failCount / total * 100, 2) : 0;

            return new Rule36ValidationSummary
            {
                Success = true,
                TotalValidated = total,
                PassCount = passCount,
                FailCount = failCount,
                ExceptionRate = rate,
                Status = failCount == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable = request.StudTable,
                DeceasedTable = request.DeceasedTable,
                StudColumn = request.StudColumn,
                DeceasedColumn = request.DeceasedColumn,
                ClientId = request.ClientId,
                RowsTruncated = rowsTruncated,
                ValidationRows = rows,
                Exceptions = exceptions,
                Warning = rowsTruncated
                    ? $"Only the first {RowLimit:N0} rows were retained for browser review and export performance. Total records validated: {total:N0}."
                    : null
            };
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule36ValidationRequest request, Rule36ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 36);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 36,
                RuleName = "Deceased Students Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.DeceasedTable,
                StudColumn = request.StudColumn,
                DeceasedColumn = request.DeceasedColumn,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.Exceptions)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Validation / helpers ─────────────────────────────────────────────────────────

        private static void ValidateNames(params string[] values)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException("Table and column selections are required.");

                foreach (var bad in new[] { ";", "'", "\"", "--", "/*", "*/" })
                {
                    if (value.Contains(bad, StringComparison.Ordinal))
                        throw new InvalidOperationException("Unsafe table or column name was provided.");
                }
            }
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase);

        private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static Rule36ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule36ValidationSummary>(decoded);
            }
            catch { return null; }
        }

        private static void ApplyBrowserPreview(Rule36ValidationSummary summary)
        {
            summary.ValidationRows = summary.ValidationRows.Take(BrowserPreviewRowLimit).ToList();
            summary.Exceptions = summary.Exceptions.Take(BrowserPreviewRowLimit).ToList();
        }

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
