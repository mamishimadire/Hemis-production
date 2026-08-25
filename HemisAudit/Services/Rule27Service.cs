using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 27: Error Validation (Dynamic Filtering) — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. A single table is filtered by
    // one column matching any of a set of entered values (with leading-zero numeric normalization),
    // and 100% of matching rows are retrieved (no sampling). The original SQL-Server design loaded
    // every matching row into memory and serialized it with no cap — the same unbounded-load risk
    // that caused Rule18's OutOfMemoryException. MatchingRowLimit is introduced here from the start,
    // matching the house style established for every reconciliation-style rule this session.
    public class Rule27Service : IRule27Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int MatchingRowLimit = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule27Service(
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

                var autoTable = tables.FirstOrDefault(t => t.Equals("dbo_CESM_VALIDATION_DETAIL", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Equals("dbo_CREG_VALIDATION_DETAIL", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Equals("dbo_STUD_VALIDATION_DETAIL", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.EndsWith("VALIDATION_DETAIL", StringComparison.OrdinalIgnoreCase))
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

        public async Task<Rule27ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                var autoFilterColumn = FindFirst(columns,
                    ["Erro_Type", "Error_Type", "ErrorType"],
                    ["error_type", "errortype", "type"]);

                return new Rule27ColumnSelectionResult
                {
                    Success = true,
                    Columns = columns,
                    AutoFilterColumn = autoFilterColumn,
                    AutoBreakdownColumn = null
                };
            }
            catch (Exception ex)
            {
                return new Rule27ColumnSelectionResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule27FilterValueResult> GetFilterValuesAsync(int clientId, string tableName, string filterColumn)
        {
            try
            {
                ValidateObjectName(tableName);
                ValidateObjectName(filterColumn);

                var (conn, schema) = await OpenEngagementConnectionAsync(clientId);
                await using var connection = conn;

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
SELECT TRIM(CAST(""{filterColumn}"" AS text)) AS filter_value, COUNT(*) AS record_count
FROM ""{schema}"".""{tableName}""
WHERE ""{filterColumn}"" IS NOT NULL
GROUP BY TRIM(CAST(""{filterColumn}"" AS text))
ORDER BY COUNT(*) DESC, filter_value ASC
LIMIT 20;";

                await using var reader = await cmd.ExecuteReaderAsync();
                var options = new List<Rule27FilterValueOption>();
                while (await reader.ReadAsync())
                {
                    var value = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var count = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                    options.Add(new Rule27FilterValueOption
                    {
                        Value = value,
                        Count = count,
                        Label = $"{value} ({count:N0} records)"
                    });
                }

                return new Rule27FilterValueResult
                {
                    Success = true,
                    Options = options,
                    DefaultValue = options.FirstOrDefault()?.Value
                };
            }
            catch (Exception ex)
            {
                return new Rule27FilterValueResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule27VerifyResult> VerifyTableAsync(Rule27VerifyRequest request)
        {
            try
            {
                ValidateRequest(request);

                var parsedValues = ParseFilterValues(request.FilterValue);
                var normalizedValues = parsedValues.Select(NormalizeComparableValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var totalRecords = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.TableName}\";");

                await using var countCommand = connection.CreateCommand();
                var countPredicate = BuildFilterPredicate(countCommand, request.FilterColumn, parsedValues, normalizedValues);
                countCommand.CommandText = $"SELECT COUNT(*) FROM \"{schema}\".\"{request.TableName}\" WHERE {countPredicate};";
                var matchingCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

                await using var sampleCommand = connection.CreateCommand();
                var samplePredicate = BuildFilterPredicate(sampleCommand, request.FilterColumn, parsedValues, normalizedValues);
                sampleCommand.CommandText = $"SELECT * FROM \"{schema}\".\"{request.TableName}\" WHERE {samplePredicate} ORDER BY random() LIMIT 5;";

                await using var reader = await sampleCommand.ExecuteReaderAsync();
                var sampleRows = new List<Rule27SampleRowViewModel>();
                while (await reader.ReadAsync())
                {
                    var row = new Rule27SampleRowViewModel();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row.Values[reader.GetName(i)] = reader.IsDBNull(i)
                            ? null
                            : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    }
                    sampleRows.Add(row);
                }

                return new Rule27VerifyResult
                {
                    Success = true,
                    TotalRecords = totalRecords,
                    MatchingCount = matchingCount,
                    SampleRows = sampleRows
                };
            }
            catch (Exception ex)
            {
                return new Rule27VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule27ValidationSummary> RunValidationAsync(Rule27ValidationRequest request, string? userEmail = null, string? userName = null)
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
                return new Rule27ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule27WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 27);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule27WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                TableName = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                FilterColumn = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
                BreakdownColumn = string.IsNullOrWhiteSpace(row.StudColumn) ? "" : row.StudColumn,
                FilterValue = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "Fatal" : row.DeceasedColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                SampleSize = deserializedSummary?.SampleSize ?? 1,
                ShowAllRecords = deserializedSummary?.ShowAllRecords ?? true,
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

            return workspace;
        }

        public async Task<Rule27RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 27);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule27RunReviewViewModel
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

        public async Task<Rule27WorkspaceSaveResult> SaveWorkspaceAsync(Rule27ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0)
                    return new Rule27WorkspaceSaveResult { Success = false, Error = "Run the filter first so the workspace can be saved." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule27WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.TableName,
                    DeceasedTable = request.FilterColumn,
                    StudColumn = request.BreakdownColumn,
                    DeceasedColumn = request.FilterValue
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule27WorkspaceSaveResult
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
                return new Rule27WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule27WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule27WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule27WorkspaceSaveResult
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
                return new Rule27WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 27 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 27 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 27 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public Task<string> GenerateSqlAsync(Rule27ValidationRequest request)
        {
            ValidateSqlRequest(request);

            var parsedValues = ParseFilterValues(request.FilterValue);
            var normalizedValues = parsedValues.Select(NormalizeComparableValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rawValueList = string.Join(", ", parsedValues.Select(value => $"'{EscapeSqlString(value)}'"));
            var normalizedValueList = string.Join(", ", normalizedValues.Select(value => $"'{EscapeSqlString(value)}'"));
            var trimmedExpression = $"TRIM(CAST(\"{request.FilterColumn}\" AS text))";
            var normalizedExpression = PostgresNumericFilterValueHelper.BuildNormalizedSqlExpression(trimmedExpression);
            var sqlFilterPredicate = $"({trimmedExpression} IN ({rawValueList}) OR {normalizedExpression} IN ({normalizedValueList}))";

            var sql = $@"-- ============================================================================
-- HEMIS RULE 27: ERROR VALIDATION (DYNAMIC FILTERING)
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- ============================================================================
-- Table: ""{request.TableName}""
-- Filter Column: ""{request.FilterColumn}""
-- Filter Values (raw): {string.Join(", ", parsedValues)}
-- Filter Values (normalized): {string.Join(", ", normalizedValues)}
-- Logic: ""{request.FilterColumn}"" matches any entered value, with leading-zero normalization for numeric values
-- Testing Coverage: 100% of matching records (no sampling)
-- ============================================================================

-- STEP 1: COUNT TOTAL SOURCE RECORDS
SELECT COUNT(*) AS total_source_records
FROM ""{{schema}}"".""{request.TableName}"";

-- STEP 2: COUNT MATCHING RECORDS
SELECT COUNT(*) AS matching_record_count
FROM ""{{schema}}"".""{request.TableName}""
WHERE {sqlFilterPredicate};

-- STEP 3: VALUE DISTRIBUTION FOR THE SELECTED COLUMN
SELECT
    COALESCE(CAST(""{request.FilterColumn}"" AS text), 'NULL') AS filter_value,
    COUNT(*) AS record_count,
    CAST(COUNT(*) * 100.0 / NULLIF((SELECT COUNT(*) FROM ""{{schema}}"".""{request.TableName}""), 0) AS decimal(10,2)) AS percentage_of_table
FROM ""{{schema}}"".""{request.TableName}""
GROUP BY COALESCE(CAST(""{request.FilterColumn}"" AS text), 'NULL')
ORDER BY COUNT(*) DESC, filter_value ASC;

-- STEP 4: GET ALL MATCHING RECORDS (100% RETRIEVAL)
SELECT *
FROM ""{{schema}}"".""{request.TableName}""
WHERE {sqlFilterPredicate};

-- STEP 5: SUMMARY
SELECT
    (SELECT COUNT(*) FROM ""{{schema}}"".""{request.TableName}"") AS total_source_records,
    (SELECT COUNT(*)
     FROM ""{{schema}}"".""{request.TableName}""
     WHERE {sqlFilterPredicate}) AS matching_record_count,
    '100%' AS testing_coverage;
";

            return Task.FromResult(sql.Trim());
        }

        // Re-runs the same analysis a fresh "Run Validation" would, without the save-run side
        // effect - used by the Excel export path.
        public async Task<Rule27ValidationSummary> GetExportSummaryAsync(Rule27ValidationRequest request) =>
            await AnalyseAsync(request);

        // Cheap population size check - stops at a COUNT(*), no result rows loaded.
        public async Task<int> GetPopulationCountAsync(Rule27ValidationRequest request)
        {
            var parsedValues = ParseFilterValues(request.FilterValue);
            var normalizedValues = parsedValues.Select(NormalizeComparableValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using var countCommand = connection.CreateCommand();
            var countPredicate = BuildFilterPredicate(countCommand, request.FilterColumn, parsedValues, normalizedValues);
            countCommand.CommandText = $"SELECT COUNT(*) FROM \"{schema}\".\"{request.TableName}\" WHERE {countPredicate};";
            return Convert.ToInt32(await countCommand.ExecuteScalarAsync());
        }

        // Bypasses AnalyseAsync entirely - that buffers every matching row as a full
        // Dictionary<string,string?> before anything can be written out, capped at
        // MatchingRowLimit regardless of the real match count. Reads and writes one row at a
        // time, using the query's own column names directly. Mirrors
        // Rule12Service.StreamCsvExportAsync.
        public async Task StreamCsvExportAsync(Rule27ValidationRequest request, Stream outputStream)
        {
            var parsedValues = ParseFilterValues(request.FilterValue);
            var normalizedValues = parsedValues.Select(NormalizeComparableValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using var writer = new StreamWriter(outputStream, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = false };

            await using var command = connection.CreateCommand();
            var predicate = BuildFilterPredicate(command, request.FilterColumn, parsedValues, normalizedValues);
            command.CommandText = $"SELECT * FROM \"{schema}\".\"{request.TableName}\" WHERE {predicate};";
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

        private async Task<Rule27ValidationSummary> AnalyseAsync(Rule27ValidationRequest request)
        {
            var parsedValues = ParseFilterValues(request.FilterValue);
            var normalizedValues = parsedValues.Select(NormalizeComparableValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var totalValidated = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.TableName}\";");

            await using var countCommand = connection.CreateCommand();
            var countPredicate = BuildFilterPredicate(countCommand, request.FilterColumn, parsedValues, normalizedValues);
            countCommand.CommandText = $"SELECT COUNT(*) FROM \"{schema}\".\"{request.TableName}\" WHERE {countPredicate};";
            var matchingCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

            var matchingRows = new List<Rule27ValidationRowRecord>();
            if (matchingCount > 0)
            {
                await using var dataCommand = connection.CreateCommand();
                var dataPredicate = BuildFilterPredicate(dataCommand, request.FilterColumn, parsedValues, normalizedValues);
                dataCommand.CommandText = $"SELECT * FROM \"{schema}\".\"{request.TableName}\" WHERE {dataPredicate} LIMIT @limit;";
                dataCommand.Parameters.AddWithValue("limit", MatchingRowLimit);

                await using var reader = await dataCommand.ExecuteReaderAsync();
                var validationNumber = 0;
                while (await reader.ReadAsync())
                {
                    validationNumber++;
                    var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        displayValues[reader.GetName(i)] = reader.IsDBNull(i)
                            ? null
                            : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    }

                    displayValues.TryGetValue(request.FilterColumn, out var filterValue);
                    matchingRows.Add(new Rule27ValidationRowRecord
                    {
                        ValidationNumber = validationNumber,
                        FilterValue = filterValue ?? "",
                        BreakdownValue = "",
                        DisplayValues = displayValues
                    });
                }
            }

            var matchingRowsTruncated = matchingCount > matchingRows.Count;
            var failCount = matchingCount;
            var passCount = Math.Max(totalValidated - matchingCount, 0);

            return new Rule27ValidationSummary
            {
                Success = true,
                TotalValidated = totalValidated,
                MatchingCount = matchingCount,
                DisplayedCount = matchingRows.Count,
                PassCount = passCount,
                FailCount = failCount,
                ExceptionRate = totalValidated > 0 ? Math.Round((decimal)matchingCount / totalValidated * 100m, 2) : 0m,
                Status = matchingCount > 0 ? "MATCHES FOUND" : "NO MATCHES",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                TableName = request.TableName,
                FilterColumn = request.FilterColumn,
                FilterValue = string.Join(", ", parsedValues),
                BreakdownColumn = "",
                SampleSize = matchingRows.Count,
                ShowAllRecords = true,
                Sampled = false,
                ClientId = request.ClientId,
                MatchingRowsTruncated = matchingRowsTruncated,
                Breakdown = new List<Rule27BreakdownItemViewModel>(),
                MatchingRows = matchingRows,
                Warning = matchingRowsTruncated
                    ? $"Only the first {MatchingRowLimit:N0} matching rows were saved for browser review and export performance. Total matches found: {matchingCount:N0}."
                    : null
            };
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule27ValidationRequest request, Rule27ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 27);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 27,
                RuleName = "Error Validation (Dynamic Filtering)",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.TableName,
                DeceasedTable = request.FilterColumn,
                StudColumn = request.BreakdownColumn,
                DeceasedColumn = request.FilterValue.Trim(),
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.MatchingRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Validation / helpers ─────────────────────────────────────────────────────────

        private static void ValidateRequest(Rule27ValidationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName))
                throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.FilterColumn))
                throw new InvalidOperationException("Filter column is required.");
            if (string.IsNullOrWhiteSpace(request.FilterValue))
                throw new InvalidOperationException("Filter value is required.");

            ValidateObjectName(request.TableName);
            ValidateObjectName(request.FilterColumn);
            if (!string.IsNullOrWhiteSpace(request.BreakdownColumn))
                ValidateObjectName(request.BreakdownColumn);

            if (request.SampleSize <= 0)
                request.SampleSize = 1;

            if (ParseFilterValues(request.FilterValue).Count == 0)
                throw new InvalidOperationException("Enter at least one filter value.");
        }

        private static void ValidateSqlRequest(Rule27ValidationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName))
                throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.FilterColumn))
                throw new InvalidOperationException("Filter column is required.");
            if (string.IsNullOrWhiteSpace(request.FilterValue))
                throw new InvalidOperationException("Filter value is required.");

            ValidateObjectName(request.TableName);
            ValidateObjectName(request.FilterColumn);
            if (!string.IsNullOrWhiteSpace(request.BreakdownColumn))
                ValidateObjectName(request.BreakdownColumn);

            if (ParseFilterValues(request.FilterValue).Count == 0)
                throw new InvalidOperationException("Enter at least one filter value.");
        }

        private static void ValidateRequest(Rule27VerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName))
                throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.FilterColumn))
                throw new InvalidOperationException("Filter column is required.");
            if (string.IsNullOrWhiteSpace(request.FilterValue))
                throw new InvalidOperationException("Filter value is required.");

            ValidateObjectName(request.TableName);
            ValidateObjectName(request.FilterColumn);
            if (ParseFilterValues(request.FilterValue).Count == 0)
                throw new InvalidOperationException("Enter at least one filter value.");
        }

        private static List<string> ParseFilterValues(string? value) => NumericFilterValueHelper.ParseValues(value);

        private static string NormalizeComparableValue(string? value) => NumericFilterValueHelper.NormalizeNumericLikeValue(value);

        private static string BuildFilterPredicate(NpgsqlCommand command, string filterColumn, IReadOnlyList<string> rawValues, IReadOnlyList<string> normalizedValues)
        {
            var trimmedExpression = $"TRIM(CAST(\"{filterColumn}\" AS text))";
            var normalizedExpression = PostgresNumericFilterValueHelper.BuildNormalizedSqlExpression(trimmedExpression);

            var rawParameterNames = new List<string>();
            for (var i = 0; i < rawValues.Count; i++)
            {
                var parameterName = $"rawvalue{i}";
                command.Parameters.AddWithValue(parameterName, rawValues[i]);
                rawParameterNames.Add(parameterName);
            }

            var normalizedParameterNames = new List<string>();
            for (var i = 0; i < normalizedValues.Count; i++)
            {
                var parameterName = $"normalizedvalue{i}";
                command.Parameters.AddWithValue(parameterName, normalizedValues[i]);
                normalizedParameterNames.Add(parameterName);
            }

            return $"({trimmedExpression} IN ({string.Join(", ", rawParameterNames.Select(n => "@" + n))}) OR {normalizedExpression} IN ({string.Join(", ", normalizedParameterNames.Select(n => "@" + n))}))";
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

        private static string? FindFirst(IEnumerable<string> columns, string[] exactMatches, string[] containsMatches)
        {
            foreach (var exact in exactMatches)
            {
                var match = columns.FirstOrDefault(c => c.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }

            foreach (var fragment in containsMatches)
            {
                var match = columns.FirstOrDefault(c => c.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }

            return columns.FirstOrDefault();
        }

        private static string EscapeSqlString(string value) => value.Replace("'", "''");

        private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static Rule27ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule27ValidationSummary>(decoded);
            }
            catch { return null; }
        }

        private static void ApplyBrowserPreview(Rule27ValidationSummary summary)
        {
            summary.MatchingRows = summary.MatchingRows.Take(BrowserPreviewRowLimit).ToList();
            summary.DisplayedCount = Math.Min(summary.DisplayedCount, summary.MatchingRows.Count);
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
