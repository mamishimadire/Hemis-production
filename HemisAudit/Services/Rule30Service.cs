using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 30: Fatal Errors with Exclusions (PROF) — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. A single table is filtered
    // by an "error type" column matching a configured value (default "Fatal"), then every matching
    // row is classified EXCLUDED or REMAINING based on whether its error code (raw or leading-zero
    // normalized) is in the configured exclusion list; PASS only when zero rows remain. The original
    // SQL-Server design loaded every fatal-type row into memory with no cap on either the excluded
    // or remaining lists — the same unbounded-load risk that caused Rule18's OutOfMemoryException.
    // RowLimit is introduced here from the start, matching house style. Mechanically identical to
    // the canonical Rule32Service (STUD) — reuses Rule32's shared ViewModels/views, differing only
    // in RuleNumber and the PROF table auto-detect priority.
    public class Rule30Service : IRule30Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int RowLimit = 5000;
        private static readonly string[] DefaultExclusions = ["02202", "02301", "02302", "00708", "07201", "01501", "1501"];

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule30Service(
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

                var autoTable = tables.FirstOrDefault(t => t.Equals("dbo_PROF_VALIDATION_DETAIL", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Equals("PROF_VALIDATION_DETAIL", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.EndsWith("PROF_VALIDATION_DETAIL", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Contains("PROF_VALIDATION_DETAIL", StringComparison.OrdinalIgnoreCase));

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

        public async Task<Rule32ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                var autoErrorType = FindFirst(columns,
                    ["Error_Type", "ErrorType", "Erro_Type"],
                    ["error_type", "errortype", "fatal"]);
                var autoError = FindFirst(columns,
                    ["Error", "Erro", "Error_Code", "ErrorCode"],
                    ["error", "code"]);

                return new Rule32ColumnSelectionResult
                {
                    Success = true,
                    Columns = columns,
                    AutoErrorTypeColumn = autoErrorType,
                    AutoErrorColumn = autoError
                };
            }
            catch (Exception ex)
            {
                return new Rule32ColumnSelectionResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule32FilterValueResult> GetFilterValuesAsync(int clientId, string tableName, string errorTypeColumn)
        {
            try
            {
                ValidateObjectName(tableName);
                ValidateObjectName(errorTypeColumn);

                var (conn, schema) = await OpenEngagementConnectionAsync(clientId);
                await using var connection = conn;

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
SELECT TRIM(CAST(""{errorTypeColumn}"" AS text)) AS filter_value, COUNT(*) AS record_count
FROM ""{schema}"".""{tableName}""
WHERE ""{errorTypeColumn}"" IS NOT NULL
  AND TRIM(CAST(""{errorTypeColumn}"" AS text)) <> ''
GROUP BY TRIM(CAST(""{errorTypeColumn}"" AS text))
ORDER BY COUNT(*) DESC, filter_value ASC;";

                await using var reader = await cmd.ExecuteReaderAsync();
                var options = new List<Rule32FilterValueOption>();
                while (await reader.ReadAsync())
                {
                    var value = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var count = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                    options.Add(new Rule32FilterValueOption
                    {
                        Value = value,
                        Count = count,
                        Label = $"{value.ToUpperInvariant()} ({count:N0} records)"
                    });
                }

                if (!options.Any(o => string.Equals(o.Value, "Fatal", StringComparison.OrdinalIgnoreCase)))
                {
                    options.Insert(0, new Rule32FilterValueOption
                    {
                        Value = "Fatal",
                        Count = 0,
                        Label = "FATAL (0 records)"
                    });
                }

                var defaultValue = options.FirstOrDefault(o =>
                    string.Equals(o.Value, "Fatal", StringComparison.OrdinalIgnoreCase))?.Value ?? options.FirstOrDefault()?.Value;

                return new Rule32FilterValueResult
                {
                    Success = true,
                    Options = options,
                    DefaultValue = defaultValue
                };
            }
            catch (Exception ex)
            {
                return new Rule32FilterValueResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule32VerifyResult> VerifyTableAsync(Rule32VerifyRequest request)
        {
            try
            {
                ValidateRequest(request);

                var exclusions = ParseExclusions(request.ExclusionCodes);
                var normalizedExclusions = exclusions.Select(NormalizeErrorCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var totalRecords = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.TableName}\";");

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
SELECT CAST(""{request.ErrorColumn}"" AS text) AS error_code
FROM ""{schema}"".""{request.TableName}""
WHERE UPPER(TRIM(CAST(""{request.ErrorTypeColumn}"" AS text))) = UPPER(@filterValue);";
                cmd.Parameters.AddWithValue("filterValue", request.ErrorTypeValue.Trim());

                await using var reader = await cmd.ExecuteReaderAsync();
                var totalFatal = 0;
                var excluded = 0;
                while (await reader.ReadAsync())
                {
                    totalFatal++;
                    var errorCode = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    if (IsExcluded(errorCode, normalizedExclusions))
                        excluded++;
                }

                return new Rule32VerifyResult
                {
                    Success = true,
                    TotalRecords = totalRecords,
                    TotalFatal = totalFatal,
                    ExcludedCount = excluded,
                    RemainingCount = totalFatal - excluded,
                    NormalizedExclusions = normalizedExclusions
                };
            }
            catch (Exception ex)
            {
                return new Rule32VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule32ValidationSummary> RunValidationAsync(Rule32ValidationRequest request, string? userEmail = null, string? userName = null)
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
                return new Rule32ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule32WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 30);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule32WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                TableName = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                ErrorTypeColumn = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
                ErrorColumn = string.IsNullOrWhiteSpace(row.StudColumn) ? "" : row.StudColumn,
                ErrorTypeValue = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "Fatal" : row.DeceasedColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary,
                ExclusionCodes = string.Join(", ", summary != null ? summary.Exclusions : DefaultExclusions)
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

        public async Task<Rule32RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 30);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule32RunReviewViewModel
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

        public async Task<Rule32WorkspaceSaveResult> SaveWorkspaceAsync(Rule32ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule32WorkspaceSaveResult { Success = false, Error = "Run validation before saving the workspace." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule32WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.TableName,
                    DeceasedTable = request.ErrorTypeColumn,
                    StudColumn = request.ErrorColumn,
                    DeceasedColumn = request.ErrorTypeValue
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: true);
                if (workspace != null) workspace.ResultsVisible = true;
                return new Rule32WorkspaceSaveResult
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
                return new Rule32WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule32WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule32WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: true);
                if (workspace != null) workspace.ResultsVisible = true;
                return new Rule32WorkspaceSaveResult
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
                return new Rule32WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 30 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 30 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 30 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public Task<string> GenerateSqlAsync(Rule32ValidationRequest request)
        {
            ValidateSqlRequest(request);

            var exclusions = ParseExclusions(request.ExclusionCodes);
            var normalizedExclusions = exclusions
                .Select(NormalizeErrorCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var exclusionList = string.Join(", ", exclusions.Select(e => $"'{EscapeSqlString(e)}'"));
            var normalizedList = string.Join(", ", normalizedExclusions.Select(e => $"'{EscapeSqlString(e)}'"));
            var errorCodeExpr = $"TRIM(CAST(\"{request.ErrorColumn}\" AS text))";
            var normalizedExpr = PostgresNumericFilterValueHelper.BuildNormalizedSqlExpression(errorCodeExpr);

            var sql = $@"-- ============================================================================
-- HEMIS RULE 30: FATAL ERRORS WITH EXCLUSIONS (PROF)
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- ============================================================================
-- Table: ""{request.TableName}""
-- Filter: ""{request.ErrorTypeColumn}"" = '{EscapeSqlString(request.ErrorTypeValue)}'
-- Exclusions: {string.Join(", ", exclusions)}
-- Normalized exclusions: {string.Join(", ", normalizedExclusions)}
-- PASS if no fatal errors remain after exclusion filtering.
-- ============================================================================

DROP TABLE IF EXISTS rule30_classified;
CREATE TEMP TABLE rule30_classified AS
SELECT
    ROW_NUMBER() OVER () AS validation_number,
    src.*,
    error_code_raw,
    error_code_normalized,
    CASE
        WHEN error_code_raw IN ({exclusionList})
          OR error_code_normalized IN ({normalizedList})
        THEN 'EXCLUDED'
        ELSE 'REMAINING'
    END AS classification
FROM (
    SELECT
        t.*,
        {errorCodeExpr} AS error_code_raw,
        {normalizedExpr} AS error_code_normalized
    FROM ""{{schema}}"".""{request.TableName}"" t
    WHERE UPPER(TRIM(CAST(t.""{request.ErrorTypeColumn}"" AS text))) = UPPER('{EscapeSqlString(request.ErrorTypeValue)}')
) src;

SELECT * FROM rule30_classified;

SELECT
    COUNT(*) AS total_fatal,
    SUM(CASE WHEN classification = 'EXCLUDED' THEN 1 ELSE 0 END) AS excluded_count,
    SUM(CASE WHEN classification = 'REMAINING' THEN 1 ELSE 0 END) AS remaining_count,
    CASE
        WHEN SUM(CASE WHEN classification = 'REMAINING' THEN 1 ELSE 0 END) = 0 THEN 'PASS'
        ELSE 'FAIL'
    END AS validation_result
FROM rule30_classified;

SELECT error_code_raw AS error_code, COUNT(*) AS excluded_count
FROM rule30_classified
WHERE classification = 'EXCLUDED'
GROUP BY error_code_raw
ORDER BY COUNT(*) DESC, error_code_raw ASC;

SELECT error_code_raw AS error_code, COUNT(*) AS remaining_count
FROM rule30_classified
WHERE classification = 'REMAINING'
GROUP BY error_code_raw
ORDER BY COUNT(*) DESC, error_code_raw ASC;";

            return Task.FromResult(sql);
        }

        // ── Analysis ─────────────────────────────────────────────────────────────────────

        private async Task<Rule32ValidationSummary> AnalyseAsync(Rule32ValidationRequest request)
        {
            var exclusions = ParseExclusions(request.ExclusionCodes);
            var normalizedExclusions = exclusions.Select(NormalizeErrorCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
SELECT *
FROM ""{schema}"".""{request.TableName}""
WHERE UPPER(TRIM(CAST(""{request.ErrorTypeColumn}"" AS text))) = UPPER(@filterValue);";
            cmd.Parameters.AddWithValue("filterValue", request.ErrorTypeValue.Trim());

            await using var reader = await cmd.ExecuteReaderAsync();
            var excludedRows = new List<Rule32ValidationRowRecord>();
            var remainingRows = new List<Rule32ValidationRowRecord>();
            var excludedBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var remainingBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var validationNumber = 0;
            var totalFatal = 0;
            var excludedCount = 0;
            var remainingCount = 0;
            var rowsTruncated = false;

            while (await reader.ReadAsync())
            {
                totalFatal++;
                validationNumber++;
                var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    var value = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    displayValues[name] = value;
                }

                var errorTypeValue = displayValues.TryGetValue(request.ErrorTypeColumn, out var errorTypeRaw) ? errorTypeRaw ?? "" : "";
                var errorCode = displayValues.TryGetValue(request.ErrorColumn, out var errorRaw) ? errorRaw ?? "" : "";
                var normalizedErrorCode = NormalizeErrorCode(errorCode);
                var isExcluded = IsExcluded(errorCode, normalizedExclusions);
                var classification = isExcluded ? "EXCLUDED" : "REMAINING";

                if (isExcluded)
                {
                    excludedCount++;
                    IncrementCount(excludedBreakdown, string.IsNullOrWhiteSpace(errorCode) ? "(blank)" : errorCode);
                }
                else
                {
                    remainingCount++;
                    IncrementCount(remainingBreakdown, string.IsNullOrWhiteSpace(errorCode) ? "(blank)" : errorCode);
                }

                if (excludedRows.Count + remainingRows.Count >= RowLimit)
                {
                    rowsTruncated = true;
                    continue;
                }

                var row = new Rule32ValidationRowRecord
                {
                    ValidationNumber = validationNumber,
                    ErrorTypeValue = errorTypeValue,
                    ErrorCode = errorCode,
                    NormalizedErrorCode = normalizedErrorCode,
                    Classification = classification,
                    ErrorMessage = FindFirstValue(displayValues, "Error_Message", "ErrorMessage", "Message"),
                    Description = FindFirstValue(displayValues, "Description", "Error_Description", "ErrorDescription"),
                    ElementInformation = FindFirstValue(displayValues, "Element_Information", "ElementInformation", "Element"),
                    DisplayValues = displayValues
                };

                if (isExcluded) excludedRows.Add(row);
                else remainingRows.Add(row);
            }

            var rate = totalFatal > 0 ? Math.Round((decimal)remainingCount / totalFatal * 100m, 2) : 0m;

            return new Rule32ValidationSummary
            {
                Success = true,
                TotalValidated = totalFatal,
                TotalFatal = totalFatal,
                ExcludedCount = excludedCount,
                RemainingCount = remainingCount,
                PassCount = excludedCount,
                FailCount = remainingCount,
                ExceptionRate = rate,
                Status = remainingCount == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                TableName = request.TableName,
                ErrorTypeColumn = request.ErrorTypeColumn,
                ErrorColumn = request.ErrorColumn,
                ErrorTypeValue = request.ErrorTypeValue,
                ClientId = request.ClientId,
                RowsTruncated = rowsTruncated,
                Exclusions = exclusions,
                NormalizedExclusions = normalizedExclusions,
                ExcludedBreakdown = excludedBreakdown
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new Rule32BreakdownItemViewModel { ErrorCode = x.Key, Count = x.Value })
                    .ToList(),
                RemainingBreakdown = remainingBreakdown
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new Rule32BreakdownItemViewModel { ErrorCode = x.Key, Count = x.Value })
                    .ToList(),
                ExcludedRows = excludedRows,
                RemainingRows = remainingRows,
                Warning = rowsTruncated
                    ? $"Only the first {RowLimit:N0} rows were saved for browser review and export performance. Total fatal rows found: {totalFatal:N0}."
                    : null
            };
        }

        private async Task<int> SaveValidationRunAsync(Rule32ValidationRequest request, Rule32ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 30);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 30,
                RuleName = "Fatal Errors with Exclusions (PROF)",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.TableName,
                DeceasedTable = request.ErrorTypeColumn,
                StudColumn = request.ErrorColumn,
                DeceasedColumn = request.ErrorTypeValue,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.RemainingRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Validation / helpers ─────────────────────────────────────────────────────────

        private static void ValidateRequest(Rule32ValidationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName))
                throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.ErrorTypeColumn))
                throw new InvalidOperationException("Error type column is required.");
            if (string.IsNullOrWhiteSpace(request.ErrorColumn))
                throw new InvalidOperationException("Error column is required.");
            if (string.IsNullOrWhiteSpace(request.ErrorTypeValue))
                throw new InvalidOperationException("Filter value is required.");

            ValidateObjectName(request.TableName);
            ValidateObjectName(request.ErrorTypeColumn);
            ValidateObjectName(request.ErrorColumn);
        }

        private static void ValidateSqlRequest(Rule32ValidationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName))
                throw new InvalidOperationException("Source table is required.");
            if (string.IsNullOrWhiteSpace(request.ErrorTypeColumn))
                throw new InvalidOperationException("Error type column is required.");
            if (string.IsNullOrWhiteSpace(request.ErrorColumn))
                throw new InvalidOperationException("Error column is required.");
            if (string.IsNullOrWhiteSpace(request.ErrorTypeValue))
                throw new InvalidOperationException("Filter value is required.");

            ValidateObjectName(request.TableName);
            ValidateObjectName(request.ErrorTypeColumn);
            ValidateObjectName(request.ErrorColumn);
        }

        private static void ValidateRequest(Rule32VerifyRequest request)
        {
            ValidateRequest(new Rule32ValidationRequest
            {
                ClientId = request.ClientId,
                TableName = request.TableName,
                ErrorTypeColumn = request.ErrorTypeColumn,
                ErrorColumn = request.ErrorColumn,
                ErrorTypeValue = request.ErrorTypeValue,
                ExclusionCodes = request.ExclusionCodes
            });
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

        private static List<string> ParseExclusions(string? exclusionCodes) =>
            NumericFilterValueHelper.ParseValues(exclusionCodes, DefaultExclusions);

        private static string NormalizeErrorCode(string? code) => NumericFilterValueHelper.NormalizeNumericLikeValue(code);

        private static bool IsExcluded(string? errorCode, IEnumerable<string> normalizedExclusions)
        {
            var raw = (errorCode ?? "").Trim();
            var normalized = NormalizeErrorCode(raw);

            foreach (var exclusion in normalizedExclusions)
            {
                if (string.Equals(raw, exclusion, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, exclusion, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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

        private static void IncrementCount(IDictionary<string, int> lookup, string key)
        {
            if (lookup.TryGetValue(key, out var count))
                lookup[key] = count + 1;
            else
                lookup[key] = 1;
        }

        private static string FindFirstValue(Dictionary<string, string?> values, params string[] candidateKeys)
        {
            foreach (var key in candidateKeys)
            {
                if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    return value!;
            }

            return "";
        }

        private static string EscapeSqlString(string value) => value.Replace("'", "''");

        private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static Rule32ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule32ValidationSummary>(decoded);
            }
            catch { return null; }
        }

        private static void ApplyBrowserPreview(Rule32ValidationSummary summary)
        {
            summary.ExcludedRows = summary.ExcludedRows.Take(BrowserPreviewRowLimit).ToList();
            summary.RemainingRows = summary.RemainingRows.Take(BrowserPreviewRowLimit).ToList();
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
