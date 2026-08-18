using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 35: Duplicate Check on dbo_CRSE (030 field) — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. Aggregate counts (total,
    // distinct, duplicate) come from independent COUNT queries, so they stay accurate even though
    // the row-level preview/export list is capped. The original SQL-Server design loaded every row
    // of the source table (SELECT *) into memory with no cap — RowLimit is introduced here from the
    // start, matching the house style established for every rule this session.
    public class Rule35Service : IRule35Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int RowLimit = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule35Service(
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

                var autoTable = tables.FirstOrDefault(t => t.Equals("dbo_CRSE", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.EndsWith("CRSE", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault();

                return new TableListResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = autoTable
                };
            }
            catch (Exception ex)
            {
                return new TableListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule35ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                var autoDuplicateColumn = columns.FirstOrDefault(c => c.Equals("030", StringComparison.OrdinalIgnoreCase))
                    ?? columns.FirstOrDefault(c => c.Equals("_030", StringComparison.OrdinalIgnoreCase))
                    ?? columns.FirstOrDefault(c => c.Equals("Field_030", StringComparison.OrdinalIgnoreCase))
                    ?? columns.FirstOrDefault(c => c.Equals("Col_030", StringComparison.OrdinalIgnoreCase))
                    ?? columns.FirstOrDefault(c => c.Contains("030", StringComparison.OrdinalIgnoreCase))
                    ?? columns.FirstOrDefault();

                return new Rule35ColumnSelectionResult
                {
                    Success = true,
                    Columns = columns,
                    AutoDuplicateColumn = autoDuplicateColumn
                };
            }
            catch (Exception ex)
            {
                return new Rule35ColumnSelectionResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule35VerifyResult> VerifyTableAsync(Rule35VerifyRequest request)
        {
            try
            {
                ValidateRequest(request);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var totalRecords = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.TableName}\";");
                var nonNullValues = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.TableName}\" WHERE \"{request.DuplicateColumn}\" IS NOT NULL;");
                var distinctValues = await CountAsync(connection, $"SELECT COUNT(DISTINCT \"{request.DuplicateColumn}\") FROM \"{schema}\".\"{request.TableName}\" WHERE \"{request.DuplicateColumn}\" IS NOT NULL;");

                await using var duplicateGroupCommand = connection.CreateCommand();
                duplicateGroupCommand.CommandText = $@"
SELECT COUNT(*)
FROM (
    SELECT ""{request.DuplicateColumn}""
    FROM ""{schema}"".""{request.TableName}""
    WHERE ""{request.DuplicateColumn}"" IS NOT NULL
    GROUP BY ""{request.DuplicateColumn}""
    HAVING COUNT(*) > 1
) d;";
                var duplicateValues = Convert.ToInt32(await duplicateGroupCommand.ExecuteScalarAsync());

                await using var duplicateRecordCommand = connection.CreateCommand();
                duplicateRecordCommand.CommandText = $@"
WITH duplicate_counts AS
(
    SELECT ""{request.DuplicateColumn}"" AS duplicate_value, COUNT(*) AS occurrence_count
    FROM ""{schema}"".""{request.TableName}""
    WHERE ""{request.DuplicateColumn}"" IS NOT NULL
    GROUP BY ""{request.DuplicateColumn}""
    HAVING COUNT(*) > 1
)
SELECT COALESCE(SUM(occurrence_count), 0) FROM duplicate_counts;";
                var duplicateRecords = Convert.ToInt32(await duplicateRecordCommand.ExecuteScalarAsync());

                await using var sampleCommand = connection.CreateCommand();
                sampleCommand.CommandText = $@"
WITH duplicate_counts AS
(
    SELECT ""{request.DuplicateColumn}"" AS duplicate_value, COUNT(*) AS occurrence_count
    FROM ""{schema}"".""{request.TableName}""
    WHERE ""{request.DuplicateColumn}"" IS NOT NULL
    GROUP BY ""{request.DuplicateColumn}""
    HAVING COUNT(*) > 1
)
SELECT t.*, d.occurrence_count
FROM ""{schema}"".""{request.TableName}"" t
INNER JOIN duplicate_counts d ON d.duplicate_value = t.""{request.DuplicateColumn}""
ORDER BY d.occurrence_count DESC, t.""{request.DuplicateColumn}""
LIMIT 5;";

                await using var reader = await sampleCommand.ExecuteReaderAsync();
                var sampleRows = new List<Rule35SampleRowViewModel>();
                while (await reader.ReadAsync())
                {
                    var row = new Rule35SampleRowViewModel();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row.Values[reader.GetName(i)] = reader.IsDBNull(i)
                            ? null
                            : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    }
                    sampleRows.Add(row);
                }

                return new Rule35VerifyResult
                {
                    Success = true,
                    TotalRecords = totalRecords,
                    NonNullValues = nonNullValues,
                    DistinctValues = distinctValues,
                    DuplicateValues = duplicateValues,
                    DuplicateRecords = duplicateRecords,
                    SampleRows = sampleRows
                };
            }
            catch (Exception ex)
            {
                return new Rule35VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule35ValidationSummary> RunValidationAsync(Rule35ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request);

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
                return new Rule35ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule35WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 35);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule35WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                TableName = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                DuplicateColumn = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
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

            return workspace;
        }

        public async Task<Rule35RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 35);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule35RunReviewViewModel
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

        public async Task<Rule35WorkspaceSaveResult> SaveWorkspaceAsync(Rule35ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule35WorkspaceSaveResult { Success = false, Error = "Run Rule 35 first so the workspace can be saved." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule35WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.TableName,
                    DeceasedTable = request.DuplicateColumn,
                    StudColumn = "",
                    DeceasedColumn = ""
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule35WorkspaceSaveResult
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
                return new Rule35WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule35WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule35WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule35WorkspaceSaveResult
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
                return new Rule35WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 35 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 35 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 35 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public Task<string> GenerateSqlAsync(Rule35ValidationRequest request)
        {
            ValidateSqlRequest(request);

            var schema = $"engagement_{request.ClientId}";
            var sql = $@"-- ============================================================================
-- HEMIS RULE 35: DUPLICATE CHECK ON dbo_CRSE (030 FIELD)
-- Source: this engagement's own uploaded tables (schema ""{schema}""), not a live SQL Server.
-- ============================================================================
-- Table: ""{request.TableName}""
-- Duplicate Field: ""{request.DuplicateColumn}""
-- Logic:
--   COUNT = 1  -> UNIQUE    -> PASS
--   COUNT > 1  -> DUPLICATE -> FAIL
-- ============================================================================

WITH duplicate_analysis AS
(
    SELECT
        t.*,
        COUNT(*) OVER (PARTITION BY ""{request.DuplicateColumn}"") AS occurrence_count,
        CASE
            WHEN COUNT(*) OVER (PARTITION BY ""{request.DuplicateColumn}"") > 1 THEN 'DUPLICATE'
            ELSE 'UNIQUE'
        END AS duplicate_status
    FROM ""{schema}"".""{request.TableName}"" t
)
SELECT *
FROM duplicate_analysis
ORDER BY occurrence_count DESC, ""{request.DuplicateColumn}"";

SELECT
    ""{request.DuplicateColumn}"" AS duplicate_value,
    COUNT(*) AS occurrence_count
FROM ""{schema}"".""{request.TableName}""
WHERE ""{request.DuplicateColumn}"" IS NOT NULL
GROUP BY ""{request.DuplicateColumn}""
HAVING COUNT(*) > 1
ORDER BY COUNT(*) DESC, ""{request.DuplicateColumn}"";

SELECT
    COUNT(*) AS total_records,
    COUNT(DISTINCT ""{request.DuplicateColumn}"") AS distinct_values,
    COUNT(*) - COUNT(DISTINCT ""{request.DuplicateColumn}"") AS duplicate_record_delta
FROM ""{schema}"".""{request.TableName}""
WHERE ""{request.DuplicateColumn}"" IS NOT NULL;
";

            return Task.FromResult(sql.Trim());
        }

        // ── Analysis ─────────────────────────────────────────────────────────────────────

        private async Task<Rule35ValidationSummary> AnalyseAsync(Rule35ValidationRequest request)
        {
            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var totalValidated = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.TableName}\";");
            var distinctValues = await CountAsync(connection, $"SELECT COUNT(DISTINCT \"{request.DuplicateColumn}\") FROM \"{schema}\".\"{request.TableName}\" WHERE \"{request.DuplicateColumn}\" IS NOT NULL;");

            var duplicateSummary = new List<Rule35DuplicateSummaryItemViewModel>();
            await using (var duplicateSummaryCommand = connection.CreateCommand())
            {
                duplicateSummaryCommand.CommandText = $@"
SELECT
    CAST(""{request.DuplicateColumn}"" AS text) AS duplicate_value,
    COUNT(*) AS occurrence_count
FROM ""{schema}"".""{request.TableName}""
WHERE ""{request.DuplicateColumn}"" IS NOT NULL
GROUP BY ""{request.DuplicateColumn}""
HAVING COUNT(*) > 1
ORDER BY COUNT(*) DESC, CAST(""{request.DuplicateColumn}"" AS text);";

                await using var duplicateReader = await duplicateSummaryCommand.ExecuteReaderAsync();
                while (await duplicateReader.ReadAsync())
                {
                    duplicateSummary.Add(new Rule35DuplicateSummaryItemViewModel
                    {
                        Value = duplicateReader.IsDBNull(0) ? "(blank)" : duplicateReader.GetString(0),
                        Count = duplicateReader.IsDBNull(1) ? 0 : Convert.ToInt32(duplicateReader.GetValue(1))
                    });
                }
            }

            var duplicateRecords = 0;
            await using (var duplicateRecordCommand = connection.CreateCommand())
            {
                duplicateRecordCommand.CommandText = $@"
WITH duplicate_counts AS
(
    SELECT ""{request.DuplicateColumn}"" AS duplicate_value, COUNT(*) AS occurrence_count
    FROM ""{schema}"".""{request.TableName}""
    WHERE ""{request.DuplicateColumn}"" IS NOT NULL
    GROUP BY ""{request.DuplicateColumn}""
    HAVING COUNT(*) > 1
)
SELECT COALESCE(SUM(occurrence_count), 0) FROM duplicate_counts;";
                duplicateRecords = Convert.ToInt32(await duplicateRecordCommand.ExecuteScalarAsync());
            }

            var validationRows = new List<Rule35ValidationRowRecord>();
            var rowsTruncated = false;
            await using (var dataCommand = connection.CreateCommand())
            {
                dataCommand.CommandText = $@"
SELECT
    ROW_NUMBER() OVER (ORDER BY occurrence_count DESC, duplicate_status DESC, duplicate_value) AS validation_number,
    occurrence_count,
    duplicate_status,
    duplicate_value,
    x.*
FROM
(
    SELECT
        CAST(t.""{request.DuplicateColumn}"" AS text) AS duplicate_value,
        COUNT(*) OVER (PARTITION BY t.""{request.DuplicateColumn}"") AS occurrence_count,
        CASE
            WHEN COUNT(*) OVER (PARTITION BY t.""{request.DuplicateColumn}"") > 1 THEN 'DUPLICATE'
            ELSE 'UNIQUE'
        END AS duplicate_status,
        t.*
    FROM ""{schema}"".""{request.TableName}"" t
) x
ORDER BY occurrence_count DESC, duplicate_status DESC, validation_number
LIMIT @limit;";
                dataCommand.Parameters.AddWithValue("limit", RowLimit + 1);

                await using var reader = await dataCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (validationRows.Count >= RowLimit)
                    {
                        rowsTruncated = true;
                        break;
                    }

                    var row = new Rule35ValidationRowRecord
                    {
                        ValidationNumber = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0)),
                        OccurrenceCount = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1)),
                        DuplicateStatus = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        DuplicateValue = reader.IsDBNull(3) ? "" : reader.GetString(3)
                    };

                    var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 4; i < reader.FieldCount; i++)
                    {
                        var columnName = reader.GetName(i);
                        if (string.Equals(columnName, "occurrence_count", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(columnName, "duplicate_status", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(columnName, "duplicate_value", StringComparison.OrdinalIgnoreCase))
                            continue;

                        displayValues[columnName] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    }

                    row.DisplayValues = displayValues;
                    validationRows.Add(row);
                }
            }

            var duplicateValues = duplicateSummary.Count;
            var passCount = Math.Max(totalValidated - duplicateRecords, 0);
            var failCount = duplicateRecords;

            return new Rule35ValidationSummary
            {
                Success = true,
                TotalValidated = totalValidated,
                UniqueValues = Math.Max(distinctValues - duplicateValues, 0),
                DuplicateValues = duplicateValues,
                DuplicateRecords = duplicateRecords,
                DisplayedCount = Math.Min(validationRows.Count, BrowserPreviewRowLimit),
                PassCount = passCount,
                FailCount = failCount,
                ExceptionRate = totalValidated > 0 ? Math.Round((decimal)duplicateRecords / totalValidated * 100m, 2) : 0m,
                Status = duplicateValues == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                TableName = request.TableName,
                DuplicateColumn = request.DuplicateColumn,
                ClientId = request.ClientId,
                RowsTruncated = rowsTruncated,
                DuplicateSummary = duplicateSummary,
                ValidationRows = validationRows,
                Warning = rowsTruncated
                    ? $"Only the first {RowLimit:N0} rows were retained for browser review and export performance. Total records validated: {totalValidated:N0}."
                    : null
            };
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule35ValidationRequest request, Rule35ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 35);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 35,
                RuleName = "Duplicate Check on dbo_CRSE",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.TableName,
                DeceasedTable = request.DuplicateColumn,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.DuplicateSummary)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Validation / helpers ─────────────────────────────────────────────────────────

        private static void ValidateRequest(Rule35ValidationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName))
                throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.DuplicateColumn))
                throw new InvalidOperationException("Duplicate field is required.");

            ValidateObjectName(request.TableName);
            ValidateObjectName(request.DuplicateColumn);
        }

        private static void ValidateSqlRequest(Rule35ValidationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName))
                throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.DuplicateColumn))
                throw new InvalidOperationException("Duplicate field is required.");

            ValidateObjectName(request.TableName);
            ValidateObjectName(request.DuplicateColumn);
        }

        private static void ValidateRequest(Rule35VerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName))
                throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.DuplicateColumn))
                throw new InvalidOperationException("Duplicate field is required.");

            ValidateObjectName(request.TableName);
            ValidateObjectName(request.DuplicateColumn);
        }

        private static void ValidateObjectName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Table or column name is required.");

            foreach (var bad in new[] { ";", "'", "\"", "--", "/*", "*/" })
            {
                if (value.Contains(bad, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unsafe table or column name was provided.");
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

        private static Rule35ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule35ValidationSummary>(decoded);
            }
            catch { return null; }
        }

        private static void ApplyBrowserPreview(Rule35ValidationSummary summary)
        {
            summary.ValidationRows = summary.ValidationRows.Take(BrowserPreviewRowLimit).ToList();
            summary.DisplayedCount = Math.Min(summary.DisplayedCount, summary.ValidationRows.Count);
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
