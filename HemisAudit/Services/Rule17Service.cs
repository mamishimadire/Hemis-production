using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;
using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    // Rule 17 pilot: validates against the engagement's own uploaded Supabase data instead of a
    // live SQL Server connection, and saves its results through the shared, Postgres-native
    // persistence layer in SystemDatabaseService instead of a private connection to the now-
    // orphaned "SystemDatabase" SQL Server. This is the reference implementation the remaining
    // rule engines migrate to next.
    public class Rule17Service : IRule17Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule17Service(
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

                var tableNames = tables.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                return new TableListResult
                {
                    Success = true,
                    Tables = tableNames,
                    AutoStudTable = FindFirst(tableNames, ["dbo_STUD", "dbo_stud", "STUD", "stud"], ["stud"]),
                    AutoQualTable = FindFirst(tableNames, ["dbo_QUAL", "dbo_qual", "QUAL", "qual"], ["qual"])
                };
            }
            catch (Exception ex)
            {
                return new TableListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule17ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                // "_025" is the same fulfilled/unfulfilled-indicator column used HEMIS-wide
                // (see Rule 16's UnfulfilledCol) — a reliable default for Rule 17's filter
                // column too. Falls back to the first available column when it's not present.
                var autoFilter = columns.FirstOrDefault(c => c.Equals("_025", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault();
                return new Rule17ColumnSelectionResult
                {
                    Success = true,
                    Columns = columns,
                    AutoFilterColumn = autoFilter,
                    AutoBreakdownColumn = autoFilter
                };
            }
            catch (Exception ex)
            {
                return new Rule17ColumnSelectionResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule17FilterValueResult> GetFilterValuesAsync(int clientId, string tableName, string filterColumn)
        {
            try
            {
                var values = await _datasets.GetDistinctColumnValuesAsync(clientId, tableName, filterColumn, take: 20);
                var options = values.Select(v => new Rule17FilterValueOption
                {
                    Value = v.Value,
                    Count = (int)Math.Min(v.Count, int.MaxValue),
                    Label = $"{v.Value} ({v.Count:N0} records)"
                }).ToList();

                return new Rule17FilterValueResult
                {
                    Success = true,
                    Options = options,
                    DefaultValue = options.FirstOrDefault()?.Value
                };
            }
            catch (Exception ex)
            {
                return new Rule17FilterValueResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule17VerifyResult> VerifyTableAsync(Rule17VerifyRequest request)
        {
            try
            {
                ValidateVerifyRequest(request);
                await _datasets.GetValidatedColumnsAsync(request.ClientId, request.TableName);
                await ValidateColumnAsync(request.ClientId, request.TableName, request.FilterColumn);

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
                var sampleRows = new List<Rule17SampleRowViewModel>();
                while (await reader.ReadAsync())
                {
                    var row = new Rule17SampleRowViewModel();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (string.Equals(reader.GetName(i), "RowId", StringComparison.OrdinalIgnoreCase)) continue;
                        row.Values[reader.GetName(i)] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    }
                    sampleRows.Add(row);
                }

                return new Rule17VerifyResult
                {
                    Success = true,
                    TotalRecords = totalRecords,
                    MatchingCount = matchingCount,
                    SampleRows = sampleRows
                };
            }
            catch (Exception ex)
            {
                return new Rule17VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule17ValidationSummary> RunValidationAsync(Rule17ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                await ValidateRequestAsync(request);

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
                return new Rule17ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule17WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 17);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule17WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                TableName = row.StudTable,
                FilterColumn = row.DeceasedTable,
                QualTable = row.StudColumn,
                FilterValue = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "" : row.DeceasedColumn,
                StudJoinCol = deserializedSummary?.StudJoinCol ?? "",
                QualJoinCol = deserializedSummary?.QualJoinCol ?? "",
                QualNameCol = deserializedSummary?.QualNameCol ?? "",
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

        public async Task<Rule17RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 17);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            if (includeFullResults)
            {
                var fullRows = DeserializeRows(row.ExceptionsJSON);
                if (fullRows.Count < summary.MatchingCount)
                {
                    try { fullRows = await RegenerateFullRowsAsync(row.ClientId, summary); }
                    catch { /* keep whatever was persisted if regeneration fails (e.g. table since deleted) */ }
                }

                summary.MatchingRows = fullRows;
                summary.DisplayedCount = summary.MatchingRows.Count;
                summary.IsPreviewOnly = false;
                summary.PreviewLimit = 0;
            }
            else
            {
                ApplyBrowserPreview(summary);
            }

            var viewModel = new Rule17RunReviewViewModel
            {
                RunId = row.RunId,
                ClientId = row.ClientId,
                IsCurrentRun = row.IsCurrent,
                EngagementName = row.EngagementName,
                MaconomyNumber = row.MaconomyNumber,
                Summary = summary
            };

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            viewModel.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(viewModel.ClientId, currentUser.Id) ?? ""
                : "";
            viewModel.Signoffs = await _systemDb.GetRuleRunSignoffsAsync(runId, currentUser?.Id);
            viewModel.HasDataAnalystSignoff = viewModel.Signoffs.Any(s =>
                string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));

            return viewModel;
        }

        public async Task<Rule17WorkspaceSaveResult> SaveWorkspaceAsync(Rule17ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                {
                    return new Rule17WorkspaceSaveResult { Success = false, Error = "Run the filter first so the workspace can be saved." };
                }

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                {
                    return new Rule17WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };
                }

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.TableName,
                    DeceasedTable = request.FilterColumn,
                    StudColumn = request.QualTable,
                    DeceasedColumn = request.FilterValue
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule17WorkspaceSaveResult
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
                return new Rule17WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule17WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                {
                    return new Rule17WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };
                }

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule17WorkspaceSaveResult
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
                return new Rule17WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Validation run was not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(engagementRole))
                throw new InvalidOperationException("Only assigned data analysts, managers, and directors can sign off a validation run.");

            if (!string.Equals(engagementRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase) &&
                !await _systemDb.HasRuleSignoffRoleAsync(runId, "DataAnalyst"))
            {
                throw new InvalidOperationException("The assigned data analyst must sign off before this review can be completed.");
            }

            await _systemDb.AddOrUpdateRuleSignoffAsync(runId, clientId, reviewer.Id, engagementRole!, comment);
        }

        public async Task RemoveSignoffAsync(int runId, string reviewerEmail)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Validation run was not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public Task<string> GenerateSqlAsync(Rule17ValidationRequest request)
        {
            ValidateSqlRequest(request);

            var parsedValues = ParseFilterValues(request.FilterValue);
            var rawList = string.Join(", ", parsedValues.Select(v => $"'{EscapeSqlString(v)}'"));
            var trimExpr = $"TRIM(CAST(s.\"{request.FilterColumn}\" AS text))";
            var sqlPredicate = $"{trimExpr} IN ({rawList})";

            var sql = $@"-- ============================================================================
-- HEMIS RULE 17: GRADUATE STUDENTS FULFILLED QUALIFICATION VALIDATION
-- Engagement's own uploaded data (schema engagement_{{ClientId}}), not a live SQL Server.
-- STUD Table : ""{request.TableName}""
-- QUAL Table : ""{request.QualTable}""
-- Join       : s.""{request.StudJoinCol}"" = q.""{request.QualJoinCol}""
-- QUAL Name  : q.""{request.QualNameCol}"" AS qualification_name
-- Filter     : s.""{request.FilterColumn}"" IN ({string.Join(", ", parsedValues)})
-- ============================================================================

SELECT s.*, q.""{request.QualNameCol}"" AS qualification_name, 'PASS' AS validation_result
FROM ""{{schema}}"".""{request.TableName}"" s
INNER JOIN ""{{schema}}"".""{request.QualTable}"" q ON s.""{request.StudJoinCol}"" = q.""{request.QualJoinCol}""
WHERE {sqlPredicate}
ORDER BY s.""{request.StudJoinCol}"";";

            return Task.FromResult(sql.Trim());
        }

        // ── Analysis (Postgres, against the engagement's uploaded tables) ─────────────────

        private async Task<Rule17ValidationSummary> AnalyseAsync(Rule17ValidationRequest request)
        {
            var parsedValues = ParseFilterValues(request.FilterValue);
            var normalizedValues = parsedValues.Select(NormalizeComparableValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using var countCommand = connection.CreateCommand();
            var countPredicate = BuildFilterPredicate(countCommand, request.FilterColumn, parsedValues, normalizedValues, "s");
            countCommand.CommandText = $@"
SELECT COUNT(*)
FROM ""{schema}"".""{request.TableName}"" s
INNER JOIN ""{schema}"".""{request.QualTable}"" q ON s.""{request.StudJoinCol}"" = q.""{request.QualJoinCol}""
WHERE {countPredicate};";
            var filteredCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
            var passCount = filteredCount;

            var breakdown = new List<Rule17BreakdownItemViewModel>();
            await using (var bdCmd = connection.CreateCommand())
            {
                var bdPredicate = BuildFilterPredicate(bdCmd, request.FilterColumn, parsedValues, normalizedValues, "s", "bd");
                bdCmd.CommandText = $@"
SELECT TRIM(CAST(s.""{request.FilterColumn}"" AS text)) AS breakdown_value, COUNT(*) AS record_count
FROM ""{schema}"".""{request.TableName}"" s
INNER JOIN ""{schema}"".""{request.QualTable}"" q ON s.""{request.StudJoinCol}"" = q.""{request.QualJoinCol}""
WHERE {bdPredicate}
GROUP BY TRIM(CAST(s.""{request.FilterColumn}"" AS text))
ORDER BY COUNT(*) DESC, breakdown_value ASC;";
                await using var bdReader = await bdCmd.ExecuteReaderAsync();
                while (await bdReader.ReadAsync())
                    breakdown.Add(new Rule17BreakdownItemViewModel
                    {
                        Value = bdReader.IsDBNull(0) ? "(blank)" : bdReader.GetString(0),
                        Count = bdReader.IsDBNull(1) ? 0 : Convert.ToInt32(bdReader.GetValue(1))
                    });
            }

            var matchingRows = filteredCount > 0
                ? await LoadFilteredRowsAsync(connection, schema, request.TableName, request.QualTable, request.StudJoinCol, request.QualJoinCol, request.QualNameCol, request.FilterColumn, parsedValues, normalizedValues, BrowserPreviewRowLimit)
                : new List<Rule17ValidationRowRecord>();

            var displayedCount = matchingRows.Count;

            return new Rule17ValidationSummary
            {
                Success = true,
                TotalValidated = filteredCount,
                MatchingCount = passCount,
                DisplayedCount = displayedCount,
                PassCount = passCount,
                FailCount = 0,
                ExceptionRate = filteredCount > 0 ? 100m : 0m,
                Status = filteredCount > 0 ? "COMPLETE" : "NO MATCHING DATA",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                TableName = request.TableName,
                StudJoinCol = request.StudJoinCol,
                QualTable = request.QualTable,
                QualJoinCol = request.QualJoinCol,
                QualNameCol = request.QualNameCol,
                FilterColumn = request.FilterColumn,
                FilterValue = string.Join(", ", parsedValues),
                BreakdownColumn = request.FilterColumn,
                SampleSize = Math.Max(request.SampleSize, 1),
                ShowAllRecords = true,
                Sampled = false,
                IsPreviewOnly = filteredCount > displayedCount,
                PreviewLimit = filteredCount > displayedCount ? displayedCount : 0,
                ClientId = request.ClientId,
                Breakdown = breakdown,
                MatchingRows = matchingRows
            };
        }

        private async Task<int> SaveValidationRunAsync(Rule17ValidationRequest request, Rule17ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 17);

            var persistedSummary = CreateBrowserPreview(summary);
            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 17,
                RuleName = "Graduate Students Fulfilled Qualification Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.TableName,
                DeceasedTable = request.FilterColumn,
                StudColumn = request.QualTable,
                DeceasedColumn = request.FilterValue.Trim(),
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.MatchingRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(persistedSummary))
            }, userEmail, userName);

            summary.SavedRunId = runId;
            return runId;
        }

        private async Task<List<Rule17ValidationRowRecord>> RegenerateFullRowsAsync(int clientId, Rule17ValidationSummary summary)
        {
            var parsedValues = ParseFilterValues(summary.FilterValue);
            var normalizedValues = parsedValues.Select(NormalizeComparableValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var (conn, schema) = await OpenEngagementConnectionAsync(clientId);
            await using var connection = conn;

            return await LoadFilteredRowsAsync(
                connection, schema,
                summary.TableName,
                string.IsNullOrWhiteSpace(summary.QualTable) ? summary.TableName : summary.QualTable,
                summary.StudJoinCol,
                summary.QualJoinCol,
                summary.QualNameCol,
                summary.FilterColumn,
                parsedValues, normalizedValues, null);
        }

        private static async Task<List<Rule17ValidationRowRecord>> LoadFilteredRowsAsync(
            NpgsqlConnection connection, string schema, string studTable, string qualTable,
            string studJoinCol, string qualJoinCol, string qualNameCol,
            string filterColumn, List<string> parsedValues, List<string> normalizedValues, int? maxRows)
        {
            await using var dataCommand = connection.CreateCommand();
            var dataPredicate = BuildFilterPredicate(dataCommand, filterColumn, parsedValues, normalizedValues, "s");
            var limitClause = maxRows.HasValue && maxRows.Value > 0 ? $" LIMIT {maxRows.Value}" : "";
            dataCommand.CommandText = $@"
SELECT s.*, q.""{qualNameCol}"" AS qualification_name
FROM ""{schema}"".""{studTable}"" s
INNER JOIN ""{schema}"".""{qualTable}"" q ON s.""{studJoinCol}"" = q.""{qualJoinCol}""
WHERE {dataPredicate}
ORDER BY s.""{studJoinCol}""{limitClause};";

            await using var reader = await dataCommand.ExecuteReaderAsync();
            var rows = new List<Rule17ValidationRowRecord>();
            var validationNumber = 0;
            while (await reader.ReadAsync())
            {
                validationNumber++;
                var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    if (string.Equals(name, "RowId", StringComparison.OrdinalIgnoreCase)) continue;
                    displayValues[name] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }
                displayValues["Validation_Result"] = "PASS";
                displayValues.TryGetValue(filterColumn, out var filterValue);

                rows.Add(new Rule17ValidationRowRecord
                {
                    ValidationNumber = validationNumber,
                    FilterValue = filterValue ?? "",
                    BreakdownValue = "PASS",
                    DisplayValues = displayValues
                });
            }
            return rows;
        }

        // ── Engagement connection / validation helpers ─────────────────────────────────────

        private async Task<(NpgsqlConnection Connection, string Schema)> OpenEngagementConnectionAsync(int clientId)
        {
            var database = await _datasets.GetDatabaseAsync(clientId)
                ?? throw new InvalidOperationException("Create a database for this engagement before running this rule.");

            // Unlimited command timeout: validation queries build temp tables and run multi-way
            // joins over the engagement's full dataset, which can legitimately take longer than
            // the app's normal 60s query timeout on large uploads.
            var connectionString = HemisAudit.Data.PostgresConnectionStringHelper.WithResiliencyDefaults(
                _configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."),
                commandTimeoutSeconds: 0);

            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var setTimeout = connection.CreateCommand())
            {
                // Belt-and-braces: also clear any server/role-level statement_timeout default
                // for this session, in case one is set independently of Npgsql's own timeout.
                setTimeout.CommandText = "SET statement_timeout = 0;";
                await setTimeout.ExecuteNonQueryAsync();
            }
            // A handful of engagements' database rows predate the SchemaName column and still
            // have it blank — engagement_{clientId} is always the actual schema regardless, so
            // fall back to it rather than building "".table queries.
            var schema = string.IsNullOrWhiteSpace(database.SchemaName) ? $"engagement_{clientId}" : database.SchemaName;
            return (connection, schema);
        }

        private async Task ValidateColumnAsync(int clientId, string tableName, string columnName)
        {
            var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
            if (!columns.Contains(columnName, StringComparer.Ordinal))
                throw new InvalidOperationException($"Column '{columnName}' was not found in table '{tableName}'.");
        }

        private async Task ValidateRequestAsync(Rule17ValidationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName))
                throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.QualTable))
                throw new InvalidOperationException("QUAL table is required.");
            if (string.IsNullOrWhiteSpace(request.StudJoinCol))
                throw new InvalidOperationException("STUD join column is required.");
            if (string.IsNullOrWhiteSpace(request.QualJoinCol))
                throw new InvalidOperationException("QUAL join column is required.");
            if (string.IsNullOrWhiteSpace(request.QualNameCol))
                throw new InvalidOperationException("QUAL name column is required.");
            if (string.IsNullOrWhiteSpace(request.FilterColumn))
                throw new InvalidOperationException("Filter column is required.");
            if (string.IsNullOrWhiteSpace(request.FilterValue))
                throw new InvalidOperationException("Filter value is required.");
            if (ParseFilterValues(request.FilterValue).Count == 0)
                throw new InvalidOperationException("Enter at least one filter value.");

            if (request.SampleSize <= 0) request.SampleSize = 1;
            request.ShowAllRecords = true;

            var studColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.TableName);
            if (!studColumns.Contains(request.StudJoinCol, StringComparer.Ordinal))
                throw new InvalidOperationException($"Column '{request.StudJoinCol}' was not found in table '{request.TableName}'.");
            if (!studColumns.Contains(request.FilterColumn, StringComparer.Ordinal))
                throw new InvalidOperationException($"Column '{request.FilterColumn}' was not found in table '{request.TableName}'.");

            var qualColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.QualTable);
            if (!qualColumns.Contains(request.QualJoinCol, StringComparer.Ordinal))
                throw new InvalidOperationException($"Column '{request.QualJoinCol}' was not found in table '{request.QualTable}'.");
            if (!qualColumns.Contains(request.QualNameCol, StringComparer.Ordinal))
                throw new InvalidOperationException($"Column '{request.QualNameCol}' was not found in table '{request.QualTable}'.");
        }

        private static void ValidateSqlRequest(Rule17ValidationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName)) throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.QualTable)) throw new InvalidOperationException("QUAL table is required.");
            if (string.IsNullOrWhiteSpace(request.StudJoinCol)) throw new InvalidOperationException("STUD join column is required.");
            if (string.IsNullOrWhiteSpace(request.QualJoinCol)) throw new InvalidOperationException("QUAL join column is required.");
            if (string.IsNullOrWhiteSpace(request.QualNameCol)) throw new InvalidOperationException("QUAL name column is required.");
            if (string.IsNullOrWhiteSpace(request.FilterColumn)) throw new InvalidOperationException("Filter column is required.");
            if (string.IsNullOrWhiteSpace(request.FilterValue)) throw new InvalidOperationException("Filter value is required.");
            if (ParseFilterValues(request.FilterValue).Count == 0) throw new InvalidOperationException("Enter at least one filter value.");
        }

        private static void ValidateVerifyRequest(Rule17VerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName)) throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.FilterColumn)) throw new InvalidOperationException("Filter column is required.");
            if (string.IsNullOrWhiteSpace(request.FilterValue)) throw new InvalidOperationException("Filter value is required.");
            if (ParseFilterValues(request.FilterValue).Count == 0) throw new InvalidOperationException("Enter at least one filter value.");
        }

        private static List<string> ParseFilterValues(string? value) => NumericFilterValueHelper.ParseValues(value);
        private static string NormalizeComparableValue(string? value) => NumericFilterValueHelper.NormalizeNumericLikeValue(value);

        private static string BuildFilterPredicate(NpgsqlCommand command, string filterColumn, IReadOnlyList<string> rawValues, IReadOnlyList<string> normalizedValues, string tableAlias = "", string paramPrefix = "")
        {
            var colRef = string.IsNullOrEmpty(tableAlias) ? $"\"{filterColumn}\"" : $"{tableAlias}.\"{filterColumn}\"";
            var trimmedExpression = $"TRIM(CAST({colRef} AS text))";
            var normalizedExpression = PostgresNumericFilterValueHelper.BuildNormalizedSqlExpression(trimmedExpression);

            var rawParameterNames = new List<string>();
            for (var i = 0; i < rawValues.Count; i++)
            {
                var parameterName = $"{paramPrefix}rawvalue{i}";
                command.Parameters.AddWithValue(parameterName, rawValues[i]);
                rawParameterNames.Add(parameterName);
            }

            var normalizedParameterNames = new List<string>();
            for (var i = 0; i < normalizedValues.Count; i++)
            {
                var parameterName = $"{paramPrefix}normalizedvalue{i}";
                command.Parameters.AddWithValue(parameterName, normalizedValues[i]);
                normalizedParameterNames.Add(parameterName);
            }

            return $"({trimmedExpression} IN ({string.Join(", ", rawParameterNames.Select(n => "@" + n))}) OR {normalizedExpression} IN ({string.Join(", ", normalizedParameterNames.Select(n => "@" + n))}))";
        }

        private static string EscapeSqlString(string value) => value.Replace("'", "''");

        private static string? FindFirst(IEnumerable<string> values, string[] exactMatches, string[] containsMatches)
        {
            foreach (var exact in exactMatches)
            {
                var match = values.FirstOrDefault(c => c.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
            foreach (var fragment in containsMatches)
            {
                var match = values.FirstOrDefault(c => c.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
            return values.FirstOrDefault();
        }

        private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static Rule17ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule17ValidationSummary>(decoded);
            }
            catch { return null; }
        }

        private static List<Rule17ValidationRowRecord> DeserializeRows(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<Rule17ValidationRowRecord>();
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<List<Rule17ValidationRowRecord>>(decoded) ?? new List<Rule17ValidationRowRecord>();
            }
            catch { return new List<Rule17ValidationRowRecord>(); }
        }

        private static Rule17ValidationSummary CreateBrowserPreview(Rule17ValidationSummary summary)
        {
            var previewRows = summary.MatchingRows
                .Where(row => string.Equals(row.BreakdownValue, "PASS", StringComparison.OrdinalIgnoreCase))
                .Take(BrowserPreviewRowLimit)
                .ToList();

            if (previewRows.Count == 0)
                previewRows = summary.MatchingRows.Take(BrowserPreviewRowLimit).ToList();

            var sourceRowCount = Math.Max(summary.MatchingCount, summary.MatchingRows.Count);
            var isPreviewOnly = summary.IsPreviewOnly || sourceRowCount > previewRows.Count;
            var previewLimit = isPreviewOnly ? previewRows.Count : 0;

            return new Rule17ValidationSummary
            {
                Success = summary.Success,
                TotalValidated = summary.TotalValidated,
                MatchingCount = summary.MatchingCount,
                DisplayedCount = previewRows.Count,
                IsPreviewOnly = isPreviewOnly,
                PreviewLimit = previewLimit,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                Status = summary.Status,
                Timestamp = summary.Timestamp,
                TableName = summary.TableName,
                StudJoinCol = summary.StudJoinCol,
                QualTable = summary.QualTable,
                QualJoinCol = summary.QualJoinCol,
                QualNameCol = summary.QualNameCol,
                FilterColumn = summary.FilterColumn,
                FilterValue = summary.FilterValue,
                BreakdownColumn = summary.BreakdownColumn,
                SampleSize = summary.SampleSize,
                ShowAllRecords = summary.ShowAllRecords,
                Sampled = summary.Sampled,
                ClientId = summary.ClientId,
                SavedRunId = summary.SavedRunId,
                Breakdown = summary.Breakdown.Select(item => new Rule17BreakdownItemViewModel { Value = item.Value, Count = item.Count }).ToList(),
                MatchingRows = previewRows,
                Warning = summary.Warning,
                Error = summary.Error
            };
        }

        private static void ApplyBrowserPreview(Rule17ValidationSummary summary)
        {
            var preview = CreateBrowserPreview(summary);
            summary.DisplayedCount = preview.DisplayedCount;
            summary.IsPreviewOnly = preview.IsPreviewOnly;
            summary.PreviewLimit = preview.PreviewLimit;
            summary.MatchingRows = preview.MatchingRows;
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase);
    }
}
