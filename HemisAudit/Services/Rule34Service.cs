using System.Globalization;
using System.Text.RegularExpressions;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 34: Census Date Validation — validates against the engagement's own uploaded Supabase
    // data instead of a live SQL Server connection. All the census-date/holiday/tolerance arithmetic
    // runs in C# (unchanged from the original design); only the source-row retrieval, the optional
    // client-census-table comparison, and the block-code exclusion filter were translated to
    // Postgres. The original SQL-Server design loaded every validated row into memory with no cap —
    // RowLimit is introduced here from the start, matching the house style established for every
    // rule this session.
    public class Rule34Service : IRule34Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int RowLimit = 5000;

        private static readonly (string FirstDay, string LastDay, string CensusDate)[] AutoColumnPrioritySets =
        {
            ("First_Day_Class", "Last_Day_Class", "Midpoint_CENSUS_DATE"),
            ("FirstDayClass", "LastDayClass", "CENSUS_DATE"),
            ("First_Class_Date", "Last_Class_Date", "CensusDate")
        };

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHttpClientFactory _httpClientFactory;

        public Rule34Service(
            IConfiguration configuration,
            IEngagementDatasetService datasets,
            ISystemDatabaseService systemDb,
            UserManager<ApplicationUser> userManager,
            IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _datasets = datasets;
            _systemDb = systemDb;
            _userManager = userManager;
            _httpClientFactory = httpClientFactory;
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

                var autoTable = tables.FirstOrDefault(t => t.Equals("dbo_CENSUS_DATES", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Equals("CENSUS_DATES", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.EndsWith("CENSUS_DATES", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Contains("CENSUS_DATE", StringComparison.OrdinalIgnoreCase));

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

        public async Task<Rule34ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);

                var autoFirst = FindFirst(columns,
                    AutoColumnPrioritySets.Select(s => s.FirstDay).ToArray(),
                    new[] { "first_day", "firstday", "first_class", "firstdayclass" });
                var autoLast = FindFirst(columns,
                    AutoColumnPrioritySets.Select(s => s.LastDay).ToArray(),
                    new[] { "last_day", "lastday", "last_class", "lastdayclass" });
                var autoCensus = FindFirst(columns,
                    AutoColumnPrioritySets.Select(s => s.CensusDate).ToArray(),
                    new[] { "current_census", "midpoint_census", "census_date", "censusdate", "midpoint" });

                var autoBlock = columns.FirstOrDefault(c =>
                    c.Equals("Block", StringComparison.OrdinalIgnoreCase) ||
                    c.Equals("BLOCK_CODE", StringComparison.OrdinalIgnoreCase) ||
                    c.Equals("BLOCK_CODE_2", StringComparison.OrdinalIgnoreCase) ||
                    c.Equals("BlockCode", StringComparison.OrdinalIgnoreCase) ||
                    c.Contains("block", StringComparison.OrdinalIgnoreCase));

                return new Rule34ColumnSelectionResult
                {
                    Success = true,
                    Columns = columns,
                    AutoFirstDayColumn = autoFirst,
                    AutoLastDayColumn = autoLast,
                    AutoCensusDateColumn = autoCensus,
                    AutoBlockColumn = autoBlock
                };
            }
            catch (Exception ex)
            {
                return new Rule34ColumnSelectionResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule34VerifyResult> VerifyTableAsync(Rule34VerifyRequest request)
        {
            try
            {
                ValidateObjectName(request.TableName);
                ValidateObjectName(request.FirstDayColumn);
                ValidateObjectName(request.LastDayColumn);
                ValidateObjectName(request.CensusDateColumn);
                var useClientComparison = UsesClientComparison(request.ClientTableName, request.ClientJoinColumn, request.BlockColumn);
                if (useClientComparison)
                {
                    ValidateObjectName(request.ClientTableName);
                    ValidateObjectName(request.ClientJoinColumn);
                    ValidateObjectName(request.BlockColumn);
                }
                else if (!string.IsNullOrWhiteSpace(request.BlockColumn))
                {
                    ValidateObjectName(request.BlockColumn);
                }

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var totalRecords = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.TableName}\";");

                var cteSql = useClientComparison
                    ? $"WITH {BuildClientComparisonCte(schema, request.ClientTableName, request.ClientJoinColumn, request.CensusDateColumn)}\n"
                    : "";
                var joinSql = useClientComparison
                    ? $"\n{BuildClientComparisonJoin(request.BlockColumn, request.ClientJoinColumn)}"
                    : "";
                var censusSourceSql = useClientComparison
                    ? $"cmp.\"{request.CensusDateColumn}\" AS census_date_value"
                    : $"src.\"{request.CensusDateColumn}\" AS census_date_value";
                var blockCol = request.BlockColumn ?? "";
                var blockSourceSql = !string.IsNullOrWhiteSpace(blockCol)
                    ? $",\n    src.\"{blockCol}\" AS block_value"
                    : "";

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"{cteSql}SELECT
    src.""{request.FirstDayColumn}"" AS first_day_value,
    src.""{request.LastDayColumn}"" AS last_day_value,
    {censusSourceSql}{blockSourceSql}
FROM ""{schema}"".""{request.TableName}"" src{joinSql}
LIMIT 5;";

                await using var reader = await cmd.ExecuteReaderAsync();
                var rows = new List<Rule34SampleRowViewModel>();
                while (await reader.ReadAsync())
                {
                    var row = new Rule34SampleRowViewModel();
                    row.Values[request.FirstDayColumn] = reader.IsDBNull(0) ? null : FormatValue(reader.GetValue(0));
                    row.Values[request.LastDayColumn] = reader.IsDBNull(1) ? null : FormatValue(reader.GetValue(1));
                    row.Values[request.CensusDateColumn] = reader.IsDBNull(2) ? null : FormatValue(reader.GetValue(2));
                    if (!string.IsNullOrWhiteSpace(blockCol))
                        row.Values[blockCol] = reader.IsDBNull(3) ? null : FormatValue(reader.GetValue(3));
                    rows.Add(row);
                }

                return new Rule34VerifyResult
                {
                    Success = true,
                    TotalRecords = totalRecords,
                    SampleRows = rows
                };
            }
            catch (Exception ex)
            {
                return new Rule34VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule34HolidayLoadResult> LoadHolidaysAsync(int startYear, int endYear)
        {
            if (startYear <= 0 || endYear <= 0 || startYear > endYear)
            {
                return new Rule34HolidayLoadResult
                {
                    Success = false,
                    Error = "Start year must be less than or equal to end year."
                };
            }

            try
            {
                var holidays = await FetchHolidaysAsync(startYear, endYear);
                return new Rule34HolidayLoadResult
                {
                    Success = true,
                    StartYear = startYear,
                    EndYear = endYear,
                    TotalCount = holidays.Count,
                    Holidays = holidays.OrderBy(h => h.Date, StringComparer.OrdinalIgnoreCase).ToList()
                };
            }
            catch (Exception ex)
            {
                return new Rule34HolidayLoadResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule34ValidationSummary> RunValidationAsync(Rule34ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request);
                var useClientComparison = UsesClientComparison(request);

                var holidays = await FetchHolidaysAsync(request.StartYear, request.EndYear);
                var holidayLookup = holidays
                    .ToDictionary(h => DateOnly.ParseExact(h.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), h => h.Name);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var sourceColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.TableName);
                var optionalColumns = GetOptionalCurrentDayColumns(sourceColumns);

                Dictionary<string, DateTime?>? clientCensusLookup = null;
                if (useClientComparison)
                {
                    clientCensusLookup = await LoadClientComparisonLookupAsync(
                        connection, schema, request.ClientTableName, request.ClientJoinColumn, request.CensusDateColumn);
                }

                var excludedRowCount = await GetExcludedRowCountAsync(connection, schema, request.TableName, request.BlockColumn, request.BlockExcludeValues);

                var selectedColumns = new List<string>
                {
                    $"src.\"{request.FirstDayColumn}\" AS first_day_value",
                    $"src.\"{request.LastDayColumn}\" AS last_day_value"
                };

                if (!useClientComparison)
                    selectedColumns.Add($"src.\"{request.CensusDateColumn}\" AS census_date_value");

                if (!string.IsNullOrWhiteSpace(optionalColumns.CurrentDaysColumn))
                    selectedColumns.Add($"src.\"{optionalColumns.CurrentDaysColumn}\" AS current_days_value");

                if (!string.IsNullOrWhiteSpace(optionalColumns.CurrentDaysHalfColumn))
                    selectedColumns.Add($"src.\"{optionalColumns.CurrentDaysHalfColumn}\" AS current_days_half_value");

                if (!string.IsNullOrWhiteSpace(request.BlockColumn))
                    selectedColumns.Add($"src.\"{request.BlockColumn}\" AS block_value");

                await using var cmd = connection.CreateCommand();
                var whereClause = BuildBlockExcludeWhere(cmd, request.BlockColumn, request.BlockExcludeValues, "src");
                cmd.CommandText = $@"SELECT
    {string.Join(",\n    ", selectedColumns)}
FROM ""{schema}"".""{request.TableName}"" src{(string.IsNullOrWhiteSpace(whereClause) ? "" : $"\n{whereClause}")};";

                var rows = new List<Rule34ValidationRowRecord>();
                var total = 0;
                var passCount = 0;
                var failCount = 0;
                var holidayCount = 0;
                var weekendCount = 0;
                var tolerancePassCount = 0;
                var rowsTruncated = false;

                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < reader.FieldCount; i++)
                        colIndex[reader.GetName(i)] = i;

                    while (await reader.ReadAsync())
                    {
                        total++;
                        var firstDay = ParseNullableDate(GetOrdinalValue(reader, colIndex, "first_day_value"));
                        var lastDay = ParseNullableDate(GetOrdinalValue(reader, colIndex, "last_day_value"));

                        string blockValue = "";
                        if (colIndex.TryGetValue("block_value", out var blockIdx))
                            blockValue = reader.IsDBNull(blockIdx) ? "" : Convert.ToString(reader.GetValue(blockIdx), CultureInfo.InvariantCulture) ?? "";

                        DateTime? censusDate;
                        if (useClientComparison)
                        {
                            var normalizedBlock = NormalizeJoinKey(blockValue);
                            censusDate = !string.IsNullOrEmpty(normalizedBlock) && clientCensusLookup!.TryGetValue(normalizedBlock, out var cd)
                                ? cd
                                : null;
                        }
                        else
                        {
                            censusDate = ParseNullableDate(GetOrdinalValue(reader, colIndex, "census_date_value"));
                        }

                        var storedCurrentDays = ParseNullableInt(GetOrdinalValue(reader, colIndex, "current_days_value"));
                        var storedCurrentDaysHalf = ParseNullableDecimal(GetOrdinalValue(reader, colIndex, "current_days_half_value"));
                        var wholeDays = storedCurrentDays ?? ComputeNotebookDaySpan(firstDay, lastDay);
                        var halfDays = storedCurrentDaysHalf ?? ComputeNotebookHalfDaySpan(wholeDays);
                        var useSqlDayValues = storedCurrentDays.HasValue && storedCurrentDaysHalf.HasValue;
                        var computedDate = useSqlDayValues
                            ? ComputePreparedCensusDateFromSqlDayValues(firstDay, wholeDays, halfDays)
                            : ComputePreparedCensusDate(firstDay, halfDays);
                        var actualCensusDate = ComputeActualCensusDate(computedDate, holidayLookup);
                        var dayStatus = GetDayStatus(computedDate, actualCensusDate, holidayLookup);
                        var comparisonResult = !actualCensusDate.HasValue || !censusDate.HasValue ||
                                               actualCensusDate.Value.Date != censusDate.Value.Date;
                        var workingDayDiff = comparisonResult
                            ? CountWorkingDaysApart(censusDate, actualCensusDate, holidayLookup)
                            : 0;
                        var withinTolerance = comparisonResult && workingDayDiff <= 2;
                        var toleranceNote = withinTolerance
                            ? BuildToleranceNote(censusDate, actualCensusDate, workingDayDiff, holidayLookup)
                            : "";
                        var dateMatch = !comparisonResult || withinTolerance;

                        if (dateMatch) passCount++; else failCount++;
                        if (withinTolerance) tolerancePassCount++;
                        if (dayStatus.StartsWith("SA Public Holiday", StringComparison.OrdinalIgnoreCase)) holidayCount++;
                        if (dayStatus.Contains("Saturday", StringComparison.OrdinalIgnoreCase) ||
                            dayStatus.Contains("Sunday", StringComparison.OrdinalIgnoreCase)) weekendCount++;

                        if (rows.Count < RowLimit)
                        {
                            rows.Add(new Rule34ValidationRowRecord
                            {
                                ValidationNumber = total,
                                FirstDayValue = FormatDate(firstDay),
                                LastDayValue = FormatDate(lastDay),
                                CurrentDays = wholeDays,
                                CurrentDaysHalf = halfDays,
                                ComputedCensusDate = FormatDate(computedDate),
                                ActualCensusDate = FormatDate(actualCensusDate),
                                CensusDateValue = FormatDate(censusDate),
                                DayStatus = dayStatus,
                                ComparisonResult = comparisonResult,
                                DateMatch = dateMatch,
                                ValidationStatus = !comparisonResult
                                    ? "PASS (FALSE - MATCH)"
                                    : withinTolerance
                                        ? "PASS (TOLERANCE)"
                                        : "FAIL (TRUE - MISMATCH)",
                                WorkingDayDiff = workingDayDiff,
                                WithinTolerance = withinTolerance,
                                ToleranceNote = toleranceNote,
                                BlockValue = blockValue
                            });
                        }
                        else
                        {
                            rowsTruncated = true;
                        }
                    }
                }

                var summary = new Rule34ValidationSummary
                {
                    Success = true,
                    TotalValidated = total,
                    PassCount = passCount,
                    FailCount = failCount,
                    ExceptionRate = total > 0 ? Math.Round((decimal)failCount / total * 100m, 2) : 0,
                    Status = failCount == 0 ? "PASS" : "FAIL",
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    TableName = request.TableName,
                    ClientTableName = useClientComparison ? request.ClientTableName : "",
                    FirstDayColumn = request.FirstDayColumn,
                    LastDayColumn = request.LastDayColumn,
                    CensusDateColumn = request.CensusDateColumn,
                    ClientJoinColumn = useClientComparison ? request.ClientJoinColumn : "",
                    StartYear = request.StartYear,
                    EndYear = request.EndYear,
                    HolidayYearRange = $"{request.StartYear}-{request.EndYear}",
                    HolidayCount = holidayCount,
                    WeekendCount = weekendCount,
                    TolerancePassCount = tolerancePassCount,
                    ClientId = request.ClientId,
                    BlockColumn = request.BlockColumn ?? "",
                    BlockExcludeValues = request.BlockExcludeValues ?? "",
                    ExcludedRowCount = excludedRowCount,
                    RowsTruncated = rowsTruncated,
                    Holidays = holidays,
                    ValidationRows = rows,
                    Exceptions = rows.Where(r => !r.DateMatch).ToList(),
                    ToleranceExceptions = rows.Where(r => r.WithinTolerance).ToList(),
                    Warning = rowsTruncated
                        ? $"Only the first {RowLimit:N0} rows were retained for browser review, export, and exception listing. Total records validated: {total:N0}."
                        : null
                };

                if (request.ClientId > 0)
                    summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName);

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule34ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule34WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 34);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule34WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                TableName = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                ClientTableName = summary?.ClientTableName ?? "",
                FirstDayColumn = string.IsNullOrWhiteSpace(row.StudColumn) ? "" : row.StudColumn,
                LastDayColumn = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "" : row.DeceasedColumn,
                CensusDateColumn = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
                ClientJoinColumn = summary?.ClientJoinColumn ?? "",
                StartYear = summary?.StartYear ?? DateTime.Now.Year,
                EndYear = summary?.EndYear ?? DateTime.Now.Year,
                BlockColumn = summary?.BlockColumn ?? "",
                BlockExcludeValues = !string.IsNullOrWhiteSpace(summary?.BlockExcludeValues) ? summary!.BlockExcludeValues : "5, 5F, 8F, 8G, R0, R1, R2",
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
            workspace.HasDataAnalystSignoff = signoffs.Any(s =>
                string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));

            var currentRoleSignoff = signoffs.FirstOrDefault(s =>
                ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, workspace.CurrentUserEngagementRole));
            workspace.CurrentUserHasSignedOff = currentRoleSignoff != null;
            workspace.CurrentUserSignoffComment = currentRoleSignoff?.Comment ?? "";
            workspace.IsWorkspaceSaved = await _systemDb.IsWorkspaceSavedAsync(workspace.RunId!.Value);

            return workspace;
        }

        public async Task<Rule34RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 34);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule34RunReviewViewModel
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

        public async Task<Rule34WorkspaceSaveResult> SaveWorkspaceAsync(Rule34ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule34WorkspaceSaveResult { Success = false, Error = "Run validation before saving the workspace." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule34WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.TableName,
                    DeceasedTable = request.CensusDateColumn,
                    StudColumn = request.FirstDayColumn,
                    DeceasedColumn = request.LastDayColumn
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule34WorkspaceSaveResult
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
                return new Rule34WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule34WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule34WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule34WorkspaceSaveResult
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
                return new Rule34WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 34 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 34 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 34 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule34ValidationRequest request)
        {
            ValidateRequest(request);

            var holidays = await FetchHolidaysAsync(request.StartYear, request.EndYear);
            var holidayValues = holidays.Any()
                ? string.Join(",\n", holidays.Select(h => $"    ('{h.Date}'::date, '{EscapeSqlString(h.Name)}')"))
                : "    ('1900-01-01'::date, 'No Holiday Data')";

            var useClientComparison = UsesClientComparison(request);
            var schema = $"engagement_{request.ClientId}";

            var censusSourceExpr = useClientComparison
                ? $"cmp.\"{request.CensusDateColumn}\"::date"
                : $"src.\"{request.CensusDateColumn}\"::date";

            var cteSql = useClientComparison
                ? $@"WITH client_comparison AS
(
    SELECT
        cmpbase.*,
        ROW_NUMBER() OVER
        (
            PARTITION BY {BuildNormalizedTextSql(request.ClientJoinColumn, "cmpbase")}
            ORDER BY
                CASE WHEN {BuildNormalizedTextSql(request.ClientJoinColumn, "cmpbase")} IS NULL OR {BuildNormalizedTextSql(request.ClientJoinColumn, "cmpbase")} = '' THEN 1 ELSE 0 END,
                CASE WHEN cmpbase.""{request.CensusDateColumn}"" IS NULL THEN 1 ELSE 0 END,
                cmpbase.""{request.CensusDateColumn}""::date DESC
        ) AS match_rank
    FROM ""{schema}"".""{request.ClientTableName}"" cmpbase
),
"
                : "WITH ";

            var joinSql = useClientComparison
                ? $"\nLEFT JOIN client_comparison cmp\n    ON cmp.match_rank = 1\n   AND {BuildNormalizedTextSql(request.BlockColumn, "src")} = {BuildNormalizedTextSql(request.ClientJoinColumn, "cmp")}"
                : "";

            var blockWhere = BuildBlockExcludeWhereText(request.BlockColumn, request.BlockExcludeValues, "src");
            var blockNote = string.IsNullOrWhiteSpace(blockWhere) ? "" :
                $"\n-- Block filter: rows where \"{request.BlockColumn}\" IN ({request.BlockExcludeValues}) are excluded";
            var comparisonNote = useClientComparison
                ? $"\n-- Comparison table: \"{request.ClientTableName}\"\n-- Join: source \"{request.BlockColumn}\" -> client \"{request.ClientJoinColumn}\"\n-- Client census date column: \"{request.CensusDateColumn}\""
                : "";
            var comparisonPurpose = useClientComparison ? "client census date" : "stored census date";
            var comparisonResultLabel = useClientComparison ? "client_census_date" : "stored_census_date";
            var blockColumnSelect = !string.IsNullOrWhiteSpace(request.BlockColumn)
                ? $",\n        src.\"{request.BlockColumn}\" AS block_value"
                : "";

            return $@"-- ============================================================================
-- HEMIS 2025 - RULE 34: CENSUS DATE VALIDATION
-- Source: this engagement's own uploaded tables (schema ""{schema}""), not a live SQL Server.
-- ============================================================================
-- Purpose: Compare the adjusted actual census date against the {comparisonPurpose}.
-- NOTEBOOK FORMULA:
--   c_Days = (Last_Day_Class - First_Day_Class) in days
--   c_Days_2 = c_Days / 2
--   c_Census_Date_Prep = First_Day_Class + c_Days_2 days
--   c_ACTUAL_CENSUS_DATE = next working day when the prepared date falls on
--                          a weekend or South African public holiday
--   Comparison_Result = c_ACTUAL_CENSUS_DATE <> {comparisonResultLabel}
-- PASS: FALSE (dates match)
-- FAIL: TRUE (dates mismatch)
-- Dynamic holiday year range: {request.StartYear} - {request.EndYear}{blockNote}{comparisonNote}
-- ============================================================================

DROP TABLE IF EXISTS rule34_holidays;
DROP TABLE IF EXISTS rule34_validation;

CREATE TEMP TABLE rule34_holidays
(
    holiday_date date NOT NULL,
    holiday_name text NOT NULL
);

INSERT INTO rule34_holidays (holiday_date, holiday_name)
VALUES
{holidayValues};

{cteSql}base_data AS
(
    SELECT
        ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS validation_number,
        src.""{request.FirstDayColumn}""::date AS first_day_class,
        src.""{request.LastDayColumn}""::date AS last_day_class,
        {censusSourceExpr} AS midpoint_census_date{blockColumnSelect}
    FROM ""{schema}"".""{request.TableName}"" src{joinSql}{(string.IsNullOrWhiteSpace(blockWhere) ? "" : $"\n{blockWhere}")}
),
prepared AS
(
    SELECT
        validation_number,
        first_day_class,
        last_day_class,
        midpoint_census_date,
        CASE WHEN first_day_class IS NULL OR last_day_class IS NULL THEN NULL
             ELSE (last_day_class - first_day_class) END AS c_days
    FROM base_data
),
calculated AS
(
    SELECT
        validation_number,
        first_day_class,
        last_day_class,
        midpoint_census_date,
        c_days,
        CASE WHEN c_days IS NULL THEN NULL ELSE c_days / 2.0 END AS c_days_2,
        CASE WHEN first_day_class IS NULL OR c_days IS NULL THEN NULL
             ELSE first_day_class + make_interval(secs => FLOOR((c_days / 2.0) * 86400)) END AS c_census_date_prep
    FROM prepared
),
actual_dates AS
(
    SELECT
        c.*,
        (
            SELECT (c.c_census_date_prep::date + o.offset_value)
            FROM generate_series(0, 14) AS o(offset_value)
            WHERE c.c_census_date_prep IS NOT NULL
              AND EXTRACT(ISODOW FROM (c.c_census_date_prep::date + o.offset_value)) NOT IN (6, 7)
              AND NOT EXISTS (
                  SELECT 1 FROM rule34_holidays h
                  WHERE h.holiday_date = (c.c_census_date_prep::date + o.offset_value)
              )
            ORDER BY o.offset_value
            LIMIT 1
        ) AS c_actual_census_date
    FROM calculated c
)
SELECT
    validation_number,
    first_day_class,
    last_day_class,
    c_days AS current_days,
    c_days_2 AS current_days_2,
    c_census_date_prep,
    c_actual_census_date,
    midpoint_census_date AS {comparisonResultLabel},
    CASE
        WHEN c_census_date_prep IS NULL THEN 'NULL Date'
        WHEN EXISTS (SELECT 1 FROM rule34_holidays h WHERE h.holiday_date = c_census_date_prep::date)
            THEN 'SA Public Holiday: ' || (SELECT h.holiday_name FROM rule34_holidays h WHERE h.holiday_date = c_census_date_prep::date LIMIT 1)
        WHEN EXTRACT(ISODOW FROM c_census_date_prep) = 6 THEN 'Falls on Saturday'
        WHEN EXTRACT(ISODOW FROM c_census_date_prep) = 7 THEN 'Falls on Sunday'
        ELSE 'Weekday'
    END AS step4_weekend_note,
    CASE
        WHEN c_actual_census_date IS NULL OR midpoint_census_date IS NULL THEN true
        WHEN c_actual_census_date::date <> midpoint_census_date::date THEN true
        ELSE false
    END AS comparison_result,
    CASE
        WHEN c_actual_census_date IS NULL OR midpoint_census_date IS NULL THEN 'FAIL (TRUE - MISMATCH)'
        WHEN c_actual_census_date::date <> midpoint_census_date::date THEN 'FAIL (TRUE - MISMATCH)'
        ELSE 'PASS (FALSE - MATCH)'
    END AS validation_status
INTO TEMP TABLE rule34_validation
FROM actual_dates;

SELECT
    COUNT(*) AS total_validated,
    SUM(CASE WHEN validation_status = 'PASS (FALSE - MATCH)' THEN 1 ELSE 0 END) AS pass_count,
    SUM(CASE WHEN validation_status = 'FAIL (TRUE - MISMATCH)' THEN 1 ELSE 0 END) AS fail_count,
    ROUND(SUM(CASE WHEN validation_status = 'FAIL (TRUE - MISMATCH)' THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0), 2) AS exception_rate_percent,
    SUM(CASE WHEN step4_weekend_note LIKE 'SA Public Holiday:%' THEN 1 ELSE 0 END) AS holiday_count,
    SUM(CASE WHEN step4_weekend_note IN ('Falls on Saturday', 'Falls on Sunday') THEN 1 ELSE 0 END) AS weekend_count
FROM rule34_validation;

SELECT * FROM rule34_validation ORDER BY validation_number;

SELECT * FROM rule34_validation WHERE validation_status = 'FAIL (TRUE - MISMATCH)' ORDER BY validation_number;

DROP TABLE rule34_validation;
DROP TABLE rule34_holidays;
-- ============================================================================
-- END OF RULE 34 CENSUS DATE VALIDATION
-- ============================================================================
";
        }

        // ── Holiday lookup (external API, unchanged) ────────────────────────────────────

        private async Task<List<Rule34HolidayItemViewModel>> FetchHolidaysAsync(int startYear, int endYear)
        {
            var all = new List<Rule34HolidayItemViewModel>();
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            for (var year = startYear; year <= endYear; year++)
            {
                var yearItems = new List<Rule34HolidayItemViewModel>();

                try
                {
                    var url = $"https://date.nager.at/api/v3/PublicHolidays/{year}/ZA";
                    using var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var payload = await response.Content.ReadAsStringAsync();
                        var apiRows = JsonConvert.DeserializeObject<List<NagerHolidayDto>>(payload) ?? new List<NagerHolidayDto>();
                        yearItems.AddRange(apiRows
                            .Where(r => !string.IsNullOrWhiteSpace(r.Date))
                            .Select(r => new Rule34HolidayItemViewModel
                            {
                                Date = r.Date!,
                                Name = !string.IsNullOrWhiteSpace(r.LocalName) ? r.LocalName! : (r.Name ?? ""),
                                Source = "Nager.Date API"
                            }));
                    }
                }
                catch
                {
                    // fall back to fixed holidays below
                }

                if (!yearItems.Any())
                {
                    foreach (var (month, day, name) in GetFallbackHolidayDefinitions())
                    {
                        yearItems.Add(new Rule34HolidayItemViewModel
                        {
                            Date = new DateOnly(year, month, day).ToString("yyyy-MM-dd"),
                            Name = name,
                            Source = "Fallback Fixed Dates"
                        });
                    }
                }

                all.AddRange(yearItems);
            }

            return all
                .GroupBy(h => h.Date, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(h => h.Date, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<(int Month, int Day, string Name)> GetFallbackHolidayDefinitions()
        {
            yield return (1, 1, "New Year's Day");
            yield return (3, 21, "Human Rights Day");
            yield return (4, 27, "Freedom Day");
            yield return (5, 1, "Workers' Day");
            yield return (6, 16, "Youth Day");
            yield return (8, 9, "National Women's Day");
            yield return (9, 24, "Heritage Day");
            yield return (12, 16, "Day of Reconciliation");
            yield return (12, 25, "Christmas Day");
            yield return (12, 26, "Day of Goodwill");
        }

        // ── Validation / helpers ─────────────────────────────────────────────────────────

        private static void ValidateRequest(Rule34ValidationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TableName))
                throw new InvalidOperationException("Table is required.");
            if (string.IsNullOrWhiteSpace(request.FirstDayColumn))
                throw new InvalidOperationException("First day column is required.");
            if (string.IsNullOrWhiteSpace(request.LastDayColumn))
                throw new InvalidOperationException("Last day column is required.");
            if (string.IsNullOrWhiteSpace(request.CensusDateColumn))
                throw new InvalidOperationException("Census date column is required.");
            if (!string.IsNullOrWhiteSpace(request.ClientTableName) ||
                !string.IsNullOrWhiteSpace(request.ClientJoinColumn))
            {
                if (string.IsNullOrWhiteSpace(request.ClientTableName))
                    throw new InvalidOperationException("Client census table is required.");
                if (string.IsNullOrWhiteSpace(request.ClientJoinColumn))
                    throw new InvalidOperationException("Client join column is required.");
                if (string.IsNullOrWhiteSpace(request.BlockColumn))
                    throw new InvalidOperationException("Source block/join column is required for the client census comparison.");
            }
            if (request.StartYear <= 0 || request.EndYear <= 0 || request.StartYear > request.EndYear)
                throw new InvalidOperationException("Select a valid holiday year range.");

            ValidateObjectName(request.TableName);
            ValidateObjectName(request.FirstDayColumn);
            ValidateObjectName(request.LastDayColumn);
            ValidateObjectName(request.CensusDateColumn);
            if (!string.IsNullOrWhiteSpace(request.ClientTableName)) ValidateObjectName(request.ClientTableName);
            if (!string.IsNullOrWhiteSpace(request.ClientJoinColumn)) ValidateObjectName(request.ClientJoinColumn);
            if (!string.IsNullOrWhiteSpace(request.BlockColumn)) ValidateObjectName(request.BlockColumn);
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

        private static bool UsesClientComparison(Rule34ValidationRequest request) =>
            UsesClientComparison(request.ClientTableName, request.ClientJoinColumn, request.BlockColumn);

        private static bool UsesClientComparison(string? clientTableName, string? clientJoinColumn, string? sourceBlockColumn) =>
            !string.IsNullOrWhiteSpace(clientTableName) &&
            !string.IsNullOrWhiteSpace(clientJoinColumn) &&
            !string.IsNullOrWhiteSpace(sourceBlockColumn);

        private static string NormalizeJoinKey(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();

        private async Task<Dictionary<string, DateTime?>> LoadClientComparisonLookupAsync(
            NpgsqlConnection connection, string schema, string clientTableName, string clientJoinColumn, string censusDateColumn)
        {
            var lookup = new Dictionary<string, DateTime?>(StringComparer.Ordinal);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"SELECT ""{clientJoinColumn}"", ""{censusDateColumn}"" FROM ""{schema}"".""{clientTableName}"";";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var joinRaw = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                var key = NormalizeJoinKey(joinRaw);
                if (string.IsNullOrEmpty(key)) continue;

                var censusDate = ParseNullableDate(reader.IsDBNull(1) ? DBNull.Value : reader.GetValue(1));

                if (!lookup.TryGetValue(key, out var existing) ||
                    (censusDate.HasValue && (!existing.HasValue || censusDate.Value > existing.Value)))
                {
                    lookup[key] = censusDate;
                }
            }

            return lookup;
        }

        private static List<string> ParseExclusionValues(string? blockExcludeValues)
        {
            if (string.IsNullOrWhiteSpace(blockExcludeValues)) return new List<string>();
            return blockExcludeValues
                .Split(',')
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<int> GetExcludedRowCountAsync(NpgsqlConnection connection, string schema, string tableName, string? blockColumn, string? blockExcludeValues)
        {
            if (string.IsNullOrWhiteSpace(blockColumn) || string.IsNullOrWhiteSpace(blockExcludeValues))
                return 0;

            var values = ParseExclusionValues(blockExcludeValues);
            if (values.Count == 0) return 0;

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"SELECT COUNT(*) FROM ""{schema}"".""{tableName}"" WHERE {BuildNormalizedTextSql(blockColumn)} = ANY(@values);";
            cmd.Parameters.AddWithValue("values", values.Select(v => v.ToUpperInvariant()).ToArray());
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private static string BuildBlockExcludeWhere(NpgsqlCommand command, string? blockColumn, string? blockExcludeValues, string alias)
        {
            if (string.IsNullOrWhiteSpace(blockColumn) || string.IsNullOrWhiteSpace(blockExcludeValues))
                return "";

            var values = ParseExclusionValues(blockExcludeValues);
            if (values.Count == 0) return "";

            command.Parameters.AddWithValue("blockexclude", values.Select(v => v.ToUpperInvariant()).ToArray());
            return $"WHERE NOT ({BuildNormalizedTextSql(blockColumn, alias)} = ANY(@blockexclude))";
        }

        private static string BuildBlockExcludeWhereText(string? blockColumn, string? blockExcludeValues, string alias)
        {
            if (string.IsNullOrWhiteSpace(blockColumn) || string.IsNullOrWhiteSpace(blockExcludeValues))
                return "";

            var values = ParseExclusionValues(blockExcludeValues);
            if (values.Count == 0) return "";

            var inList = string.Join(", ", values.Select(v => $"'{EscapeSqlString(v.ToUpperInvariant())}'"));
            return $"WHERE {BuildNormalizedTextSql(blockColumn, alias)} NOT IN ({inList})";
        }

        private static string BuildNormalizedTextSql(string columnName, string? alias = null)
        {
            var prefix = string.IsNullOrEmpty(alias) ? "" : $"{alias}.";
            return $"UPPER(TRIM(CAST({prefix}\"{columnName}\" AS text)))";
        }

        private static string BuildClientComparisonCte(string schema, string clientTableName, string clientJoinColumn, string censusDateColumn)
        {
            var joinKeySql = BuildNormalizedTextSql(clientJoinColumn, "cmpbase");

            return $@"clientcomparison AS
(
    SELECT
        cmpbase.*,
        ROW_NUMBER() OVER
        (
            PARTITION BY {joinKeySql}
            ORDER BY
                CASE WHEN {joinKeySql} IS NULL OR {joinKeySql} = '' THEN 1 ELSE 0 END,
                CASE WHEN cmpbase.""{censusDateColumn}"" IS NULL THEN 1 ELSE 0 END,
                cmpbase.""{censusDateColumn}"" DESC
        ) AS match_rank
    FROM ""{schema}"".""{clientTableName}"" cmpbase
)";
        }

        private static string BuildClientComparisonJoin(string sourceBlockColumn, string clientJoinColumn)
        {
            var sourceJoinSql = BuildNormalizedTextSql(sourceBlockColumn, "src");
            var clientJoinSql = BuildNormalizedTextSql(clientJoinColumn, "cmp");

            return $@"LEFT JOIN clientcomparison cmp
    ON cmp.match_rank = 1
   AND {sourceJoinSql} IS NOT NULL
   AND {sourceJoinSql} <> ''
   AND {sourceJoinSql} = {clientJoinSql}";
        }

        private static (string? CurrentDaysColumn, string? CurrentDaysHalfColumn) GetOptionalCurrentDayColumns(IEnumerable<string> columns)
        {
            var list = columns.ToList();
            return
            (
                FindOptionalColumn(list,
                    new[] { "Current_days", "CurrentDays", "c_Days", "C_DAYS" },
                    new[] { "current_days", "currentdays", "c_days" }),
                FindOptionalColumn(list,
                    new[] { "Current_days_2", "CurrentDays_2", "CurrentDays2", "c_Days_2", "C_DAYS_2" },
                    new[] { "current_days_2", "currentdays_2", "currentdays2", "c_days_2" })
            );
        }

        private static string? FindOptionalColumn(IEnumerable<string> columns, string[] exactMatches, string[] containsMatches)
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

            return null;
        }

        private static string? FindFirst(List<string> columns, string[] exactMatches, string[] containsMatches)
        {
            foreach (var exact in exactMatches)
            {
                var value = columns.FirstOrDefault(c => c.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            foreach (var fragment in containsMatches)
            {
                var value = columns.FirstOrDefault(c => c.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return columns.FirstOrDefault();
        }

        private static object GetOrdinalValue(System.Data.Common.DbDataReader reader, Dictionary<string, int> colIndex, string name) =>
            colIndex.TryGetValue(name, out var idx) ? (reader.IsDBNull(idx) ? DBNull.Value : reader.GetValue(idx)) : DBNull.Value;

        private static DateTime? ParseNullableDate(object value)
        {
            if (value == null || value == DBNull.Value)
                return null;

            if (value is DateTime dt)
                return dt;

            var raw = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var normalized = NormalizeNotebookDateText(raw);

            foreach (var format in NotebookDateFormats)
            {
                if (DateTime.TryParseExact(normalized, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                    return exact;

                if (DateTime.TryParseExact(normalized, format, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out exact))
                    return exact;
            }

            if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;

            if (DateTime.TryParse(normalized, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out parsed))
                return parsed;

            return null;
        }

        private static readonly string[] NotebookDateFormats =
        {
            "dd MMM yy",
            "d MMM yy",
            "dd MMM yyyy",
            "d MMM yyyy",
            "dd MMMM yy",
            "d MMMM yy",
            "dd MMMM yyyy",
            "d MMMM yyyy",
            "dd-MMM-yy",
            "d-MMM-yy",
            "dd-MMM-yyyy",
            "d-MMM-yyyy",
            "dd-MMMM-yy",
            "d-MMMM-yy",
            "dd-MMMM-yyyy",
            "d-MMMM-yyyy",
            "yyyy-MM-dd",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy/MM/dd",
            "MM/dd/yyyy",
            "dd/MM/yyyy"
        };

        private static string NormalizeNotebookDateText(string value)
        {
            var normalized = value.Trim();
            if (normalized.Length == 0)
                return normalized;

            normalized = normalized.Replace(' ', ' ');
            normalized = Regex.Replace(normalized, @"\s+", " ");
            normalized = Regex.Replace(normalized, @"\bSept\b", "Sep", RegexOptions.IgnoreCase);
            return normalized;
        }

        private static int? ComputeNotebookDaySpan(DateTime? firstDay, DateTime? lastDay)
        {
            if (!firstDay.HasValue || !lastDay.HasValue)
                return null;

            return (int)Math.Floor((lastDay.Value - firstDay.Value).TotalDays);
        }

        private static decimal? ComputeNotebookHalfDaySpan(int? wholeDays) =>
            wholeDays.HasValue ? wholeDays.Value / 2m : null;

        private static DateTime? ComputePreparedCensusDate(DateTime? firstDay, decimal? halfDays)
        {
            if (!firstDay.HasValue || !halfDays.HasValue)
                return null;

            return firstDay.Value.AddDays((double)halfDays.Value);
        }

        private static DateTime? ComputePreparedCensusDateFromSqlDayValues(DateTime? firstDay, int? wholeDays, decimal? halfDays)
        {
            if (!firstDay.HasValue || !wholeDays.HasValue || !halfDays.HasValue)
                return null;

            var midpointOffset = wholeDays.Value % 2 == 0
                ? halfDays.Value - 1m
                : halfDays.Value;

            return firstDay.Value.AddDays((double)midpointOffset);
        }

        private static DateTime? ComputeActualCensusDate(DateTime? preparedDate, IReadOnlyDictionary<DateOnly, string> holidays)
        {
            if (!preparedDate.HasValue)
                return null;

            var candidate = preparedDate.Value.Date;
            for (var i = 0; i < 31; i++)
            {
                var day = DateOnly.FromDateTime(candidate);
                var isWeekend = candidate.DayOfWeek == DayOfWeek.Saturday || candidate.DayOfWeek == DayOfWeek.Sunday;
                if (!isWeekend && !holidays.ContainsKey(day))
                    return candidate;

                candidate = candidate.AddDays(1);
            }

            return candidate;
        }

        private static int CountWorkingDaysApart(DateTime? a, DateTime? b, IReadOnlyDictionary<DateOnly, string> holidays)
        {
            if (!a.HasValue || !b.HasValue || a.Value.Date == b.Value.Date) return 0;
            var start = a.Value.Date < b.Value.Date ? a.Value.Date : b.Value.Date;
            var end = a.Value.Date > b.Value.Date ? a.Value.Date : b.Value.Date;
            var count = 0;
            for (var d = start.AddDays(1); d <= end; d = d.AddDays(1))
            {
                if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday
                    && !holidays.ContainsKey(DateOnly.FromDateTime(d)))
                    count++;
            }
            return count;
        }

        private static string BuildToleranceNote(DateTime? censusDate, DateTime? actualCensusDate, int workingDayDiff, IReadOnlyDictionary<DateOnly, string> holidays)
        {
            if (!censusDate.HasValue || !actualCensusDate.HasValue) return "";
            var start = censusDate.Value.Date < actualCensusDate.Value.Date ? censusDate.Value.Date : actualCensusDate.Value.Date;
            var end = censusDate.Value.Date > actualCensusDate.Value.Date ? censusDate.Value.Date : actualCensusDate.Value.Date;
            var calDiff = (end - start).Days;
            var skipped = new List<string>();
            for (var d = start.AddDays(1); d < end; d = d.AddDays(1))
            {
                if (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday)
                    skipped.Add($"{d:dd MMM} [{d.DayOfWeek}]");
                else if (holidays.TryGetValue(DateOnly.FromDateTime(d), out var name))
                    skipped.Add($"{d:dd MMM} [Holiday: {name}]");
            }
            var skippedText = skipped.Count > 0 ? $" ({string.Join(", ", skipped)} not counted)" : "";
            return $"{calDiff} calendar day(s) apart; {workingDayDiff} working day(s){skippedText} — within 2-day tolerance";
        }

        private static string GetDayStatus(DateTime? preparedDate, DateTime? actualDate, IReadOnlyDictionary<DateOnly, string> holidays)
        {
            if (!preparedDate.HasValue)
                return "NULL Date";

            var day = DateOnly.FromDateTime(preparedDate.Value);
            var shiftSuffix = actualDate.HasValue && actualDate.Value.Date != preparedDate.Value.Date
                ? $" -> shifted to {actualDate.Value:yyyy-MM-dd}"
                : "";

            if (holidays.TryGetValue(day, out var holidayName))
                return $"SA Public Holiday: {holidayName}{shiftSuffix}";

            return preparedDate.Value.DayOfWeek switch
            {
                DayOfWeek.Saturday => $"Falls on Saturday{shiftSuffix}",
                DayOfWeek.Sunday => $"Falls on Sunday{shiftSuffix}",
                _ => "Weekday"
            };
        }

        private static string FormatDate(DateTime? value) =>
            value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm:ss") : "NULL";

        private static int? ParseNullableInt(object value)
        {
            if (value == null || value == DBNull.Value)
                return null;

            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                return parsedInt;

            if (decimal.TryParse(text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDecimal))
                return (int)Math.Round(parsedDecimal, MidpointRounding.AwayFromZero);

            return null;
        }

        private static decimal? ParseNullableDecimal(object value)
        {
            if (value == null || value == DBNull.Value)
                return null;

            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            if (decimal.TryParse(text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                return parsed;

            return null;
        }

        private static string FormatValue(object value) =>
            value switch
            {
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            };

        private static string EscapeSqlString(string value) => value.Replace("'", "''");

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

        private static Rule34ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule34ValidationSummary>(decoded);
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyBrowserPreview(Rule34ValidationSummary summary)
        {
            summary.ValidationRows = summary.ValidationRows.Take(BrowserPreviewRowLimit).ToList();
            summary.Exceptions = summary.Exceptions.Take(BrowserPreviewRowLimit).ToList();
        }

        private async Task<int> SaveValidationRunAsync(Rule34ValidationRequest request, Rule34ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 34);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 34,
                RuleName = "Census Date Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.TableName,
                DeceasedTable = request.CensusDateColumn,
                StudColumn = request.FirstDayColumn,
                DeceasedColumn = request.LastDayColumn,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.Exceptions)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
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

        private sealed class NagerHolidayDto
        {
            [JsonProperty("date")]
            public string? Date { get; set; }

            [JsonProperty("localName")]
            public string? LocalName { get; set; }

            [JsonProperty("name")]
            public string? Name { get; set; }
        }
    }
}
