using System.Globalization;
using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 65: Cancellation Census Date Validation — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. Every distinct cancellation
    // record (with a non-blank CANCEL date) is checked: FAIL when CANCEL equals the row's own
    // CENSUS date, FAIL when CANCEL matches any date in CENSUS_LIST_CLIENT's CURRENT_CENSUS column,
    // otherwise PASS. Date parsing happens in C# (not raw SQL casts) so an unparseable date is
    // reported as its own exception category instead of aborting the whole query.
    public class Rule65Service : IRule65Service
    {
        private const int BrowserPreviewRowLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule65Service(
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

        // ── Discovery ─────────────────────────────────────────────────────────

        public async Task<Rule65TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule65TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule65TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoCancellationTable = FindFirst(tables,
                        ["canceliation list", "cancellation list", "CANCELLATION_LIST", "CANCELIATION_LIST"],
                        ["canceliation", "cancellation", "cancel"]),
                    AutoClientTable = FindFirst(tables,
                        ["CENSUS_LIST_CLIENT", "dbo_CENSUS_LIST_CLIENT"],
                        ["census_list_client", "current_census"])
                };
            }
            catch (Exception ex) { return new Rule65TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule65ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "student_no"     => FindFirst(cols, ["STD_NO"], ["std_no", "student_no", "studentno"]),
                    "qualification"  => FindFirst(cols, ["QUAL"], ["qual", "qualification"]),
                    "subject"        => FindFirst(cols, ["SUBJ"], ["subj", "subject"]),
                    "cancel_date"    => FindFirst(cols, ["CANCEL"], ["cancel", "cancel_date"]),
                    "census_date"    => FindFirst(cols, ["CENSUS"], ["census", "census_date"]),
                    "current_census" => FindFirst(cols, ["CURRENT_CENSUS"], ["current_census", "currentcensus"]),
                    _ => null
                };
                return new Rule65ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule65ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule65VerifyResult> VerifyTablesAsync(Rule65ValidationRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.CancellationTable, [request.ColumnMapping.CancelDateCol]);
                if (request.UseClientCensusTable)
                    await ValidateColumnsExistAsync(request.ClientId, request.ClientTable, [request.ColumnMapping.CurrentCensusCol]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var clientCount = request.UseClientCensusTable
                    ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.ClientTable)}\";")
                    : 0;

                return new Rule65VerifyResult
                {
                    Success = true,
                    CancellationCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CancellationTable)}\";"),
                    ClientCount = clientCount
                };
            }
            catch (Exception ex) { return new Rule65VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule65ValidationSummary> RunValidationAsync(Rule65ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                var summary = await AnalyseAsync(request);

                if (summary.Success && request.ClientId > 0)
                {
                    try { summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName); }
                    catch (Exception ex)
                    {
                        summary.Success = false;
                        summary.Error = $"Analysis completed, but the run could not be saved: {ex.Message}";
                        return summary;
                    }
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex) { return new Rule65ValidationSummary { Success = false, Error = ex.Message }; }
        }

        private async Task<Rule65ValidationSummary> AnalyseAsync(Rule65ValidationRequest request)
        {
            var mapping = request.ColumnMapping;
            await ValidateColumnsExistAsync(request.ClientId, request.CancellationTable,
                [mapping.StudentNoCol, mapping.QualificationCol, mapping.SubjectCol, mapping.CancelDateCol, mapping.CensusDateCol]);
            if (request.UseClientCensusTable)
                await ValidateColumnsExistAsync(request.ClientId, request.ClientTable, [mapping.CurrentCensusCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var ct = Sanitise(request.CancellationTable);

            var currentCensusDates = new HashSet<DateOnly>();
            if (request.UseClientCensusTable)
            {
                var lt = Sanitise(request.ClientTable);
                await using var censusCmd = connection.CreateCommand();
                censusCmd.CommandText = $@"
SELECT DISTINCT TRIM(CAST(""{mapping.CurrentCensusCol}"" AS text)) AS current_census
FROM ""{schema}"".""{lt}""
WHERE TRIM(CAST(""{mapping.CurrentCensusCol}"" AS text)) <> '';";
                await using var censusReader = await censusCmd.ExecuteReaderAsync();
                while (await censusReader.ReadAsync())
                {
                    var parsed = ParseDateOrNull(GetString(censusReader, 0));
                    if (parsed.HasValue) currentCensusDates.Add(parsed.Value);
                }
            }

            var passRows = new List<Rule65ReviewRow>();
            var failRows = new List<Rule65ReviewRow>();

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT DISTINCT
    TRIM(CAST(""{mapping.StudentNoCol}"" AS text)) AS student_no,
    TRIM(CAST(""{mapping.QualificationCol}"" AS text)) AS qualification,
    TRIM(CAST(""{mapping.SubjectCol}"" AS text)) AS subject,
    TRIM(CAST(""{mapping.CancelDateCol}"" AS text)) AS cancel_date,
    TRIM(CAST(""{mapping.CensusDateCol}"" AS text)) AS census_date
FROM ""{schema}"".""{ct}""
WHERE TRIM(CAST(""{mapping.CancelDateCol}"" AS text)) <> '';";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var studentNo = GetString(reader, 0) ?? "";
                    var qualification = GetString(reader, 1) ?? "";
                    var subject = GetString(reader, 2) ?? "";
                    var cancelDateText = GetString(reader, 3) ?? "";
                    var censusDateText = GetString(reader, 4) ?? "";

                    var cancelDate = ParseDateOrNull(cancelDateText);
                    var censusDate = ParseDateOrNull(censusDateText);

                    var row = ClassifyRow(studentNo, qualification, subject, cancelDateText, censusDateText, cancelDate, censusDate, currentCensusDates, request.UseClientCensusTable);
                    (row.ValidationResult == "PASS" ? passRows : failRows).Add(row);
                }
            }

            passRows = DeduplicateRows(passRows);
            failRows = DeduplicateRows(failRows);
            AssignRowNumbers(passRows);
            AssignRowNumbers(failRows);

            var totalCount = passRows.Count + failRows.Count;
            var failCount = failRows.Count;
            var passCount = passRows.Count;
            var exceptionRate = totalCount == 0 ? 0m : Math.Round((decimal)failCount / totalCount * 100m, 2);

            var summary = new Rule65ValidationSummary
            {
                Success = true,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                CancellationTable = request.CancellationTable,
                ClientTable = request.ClientTable,
                UseClientCensusTable = request.UseClientCensusTable,
                ColumnMapping = mapping,
                ClientId = request.ClientId,
                TotalCount = totalCount,
                PassCount = passCount,
                FailCount = failCount,
                ExceptionDetailCount = failCount,
                ExceptionRate = exceptionRate,
                Status = failCount == 0 ? "PASS" : "FAIL",
                PassRows = passRows,
                FailRows = failRows,
                ExceptionCategories = BuildExceptionCategories(passRows, failRows)
            };

            return summary;
        }

        private static Rule65ReviewRow ClassifyRow(
            string studentNo, string qualification, string subject, string cancelDateText, string censusDateText,
            DateOnly? cancelDate, DateOnly? censusDate, HashSet<DateOnly> currentCensusDates, bool useClientCensusTable)
        {
            var currentCensusText = cancelDate.HasValue && currentCensusDates.Contains(cancelDate.Value)
                ? cancelDate.Value.ToString("yyyy-MM-dd")
                : "";

            string exceptionCategory;
            string errorCode;
            string validationResult;
            string validationExplanation;

            var equalsRowCensus = cancelDate.HasValue && censusDate.HasValue && cancelDate.Value == censusDate.Value;
            var matchesCurrentCensus = useClientCensusTable && cancelDate.HasValue && currentCensusDates.Contains(cancelDate.Value);

            if (!cancelDate.HasValue)
            {
                exceptionCategory = "INVALID_CANCEL_DATE";
                errorCode = "INVALID_CANCEL_DATE";
                validationResult = "FAIL";
                validationExplanation = $"FAIL: CANCEL value '{cancelDateText}' could not be converted to a valid date.";
            }
            else if (equalsRowCensus && matchesCurrentCensus)
            {
                exceptionCategory = "CANCEL_EQUALS_CENSUS_AND_CURRENT_CENSUS";
                errorCode = "BOTH";
                validationResult = "FAIL";
                validationExplanation = $"FAIL: CANCEL date '{cancelDate:yyyy-MM-dd}' equals the row CENSUS date and also matches CURRENT_CENSUS '{cancelDate:yyyy-MM-dd}'.";
            }
            else if (equalsRowCensus)
            {
                exceptionCategory = "CANCEL_EQUALS_CENSUS";
                errorCode = "ROW_CENSUS";
                validationResult = "FAIL";
                validationExplanation = $"FAIL: CANCEL date '{cancelDate:yyyy-MM-dd}' equals the row CENSUS date '{censusDate:yyyy-MM-dd}'.";
            }
            else if (matchesCurrentCensus)
            {
                exceptionCategory = "CURRENT_CENSUS_MATCH";
                errorCode = "";
                validationResult = "FAIL";
                validationExplanation = $"FAIL: CANCEL date '{cancelDate:yyyy-MM-dd}' matches CURRENT_CENSUS date '{cancelDate:yyyy-MM-dd}' from CENSUS_LIST_CLIENT.";
            }
            else
            {
                exceptionCategory = "PASS_NOT_ON_CENSUS";
                errorCode = "";
                validationResult = "PASS";
                validationExplanation = $"PASS: CANCEL date '{cancelDate:yyyy-MM-dd}' does not equal the row CENSUS date and does not appear in CURRENT_CENSUS.";
            }

            return new Rule65ReviewRow
            {
                SourceTable = "CANCELLATION LIST",
                StudentNo = studentNo,
                Qualification = qualification,
                Subject = subject,
                CancelDate = cancelDateText,
                CensusDate = censusDateText,
                CurrentCensus = currentCensusText,
                ExceptionCategory = exceptionCategory,
                ErrorCode = errorCode,
                ValidationResult = validationResult,
                ValidationExplanation = validationExplanation
            };
        }

        // ── SQL generation (reference script for download - assumes CANCEL/CENSUS/CURRENT_CENSUS
        //    values are valid dates; the in-app analysis above is more tolerant of mixed formats
        //    and classifies unparseable values as INVALID_CANCEL_DATE instead of erroring) ──────

        public string GenerateSql(Rule65ValidationRequest request)
        {
            var mapping = request.ColumnMapping;
            var ct = Sanitise(request.CancellationTable);
            var lt = Sanitise(request.ClientTable);

            var currentCensusCte = request.UseClientCensusTable
                ? $@"SELECT DISTINCT TRIM(CAST(""{mapping.CurrentCensusCol}"" AS text))::date AS current_census_date
    FROM ""{{schema}}"".""{lt}""
    WHERE TRIM(CAST(""{mapping.CurrentCensusCol}"" AS text)) <> ''"
                : "SELECT NULL::date AS current_census_date WHERE FALSE";

            return $@"-- ============================================================
-- HEMIS RULE 65 - Cancellation Census Date Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Tables    : ""{ct}"" Cancellation List | ""{lt}"" CENSUS_LIST_CLIENT
-- Rule      : FAIL when CANCEL equals CENSUS on the cancellation row
--           : FAIL when CANCEL matches CURRENT_CENSUS in CENSUS_LIST_CLIENT
--           : PASS when neither comparison matches
-- NOTE      : this reference script assumes the date columns are valid dates; the app's own
--             analysis instead classifies unparseable values as INVALID_CANCEL_DATE.
-- ============================================================
WITH current_census AS (
    {currentCensusCte}
),
population AS (
    SELECT DISTINCT
        TRIM(CAST(""{mapping.StudentNoCol}"" AS text)) AS student_no,
        TRIM(CAST(""{mapping.QualificationCol}"" AS text)) AS qualification,
        TRIM(CAST(""{mapping.SubjectCol}"" AS text)) AS subject,
        TRIM(CAST(""{mapping.CancelDateCol}"" AS text)) AS cancel_date,
        TRIM(CAST(""{mapping.CensusDateCol}"" AS text)) AS census_date,
        TRIM(CAST(""{mapping.CancelDateCol}"" AS text))::date AS cancel_date_parsed,
        TRIM(CAST(""{mapping.CensusDateCol}"" AS text))::date AS census_date_parsed
    FROM ""{{schema}}"".""{ct}""
    WHERE TRIM(CAST(""{mapping.CancelDateCol}"" AS text)) <> ''
),
results AS (
    SELECT
        'CANCELLATION LIST' AS source_table,
        p.student_no, p.qualification, p.subject, p.cancel_date, p.census_date,
        COALESCE(TO_CHAR(cc.current_census_date, 'YYYY-MM-DD'), '') AS current_census,
        CASE
            WHEN p.census_date_parsed IS NOT NULL AND p.cancel_date_parsed = p.census_date_parsed AND cc.current_census_date IS NOT NULL THEN 'CANCEL_EQUALS_CENSUS_AND_CURRENT_CENSUS'
            WHEN p.census_date_parsed IS NOT NULL AND p.cancel_date_parsed = p.census_date_parsed THEN 'CANCEL_EQUALS_CENSUS'
            WHEN cc.current_census_date IS NOT NULL THEN 'CURRENT_CENSUS_MATCH'
            ELSE 'PASS_NOT_ON_CENSUS'
        END AS exception_category,
        CASE
            WHEN p.census_date_parsed IS NOT NULL AND p.cancel_date_parsed = p.census_date_parsed AND cc.current_census_date IS NOT NULL THEN 'FAIL'
            WHEN p.census_date_parsed IS NOT NULL AND p.cancel_date_parsed = p.census_date_parsed THEN 'FAIL'
            WHEN cc.current_census_date IS NOT NULL THEN 'FAIL'
            ELSE 'PASS'
        END AS validation_result
    FROM population p
    LEFT JOIN current_census cc ON cc.current_census_date = p.cancel_date_parsed
)
SELECT * FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, student_no, qualification, subject, cancel_date;".Trim();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule65ValidationRequest request, Rule65ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 65);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 65,
                RuleName = "Cancellation Census Date Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalCount,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.CancellationTable,
                DeceasedTable = request.ClientTable,
                StudColumn = request.ColumnMapping.CancelDateCol,
                DeceasedColumn = request.ColumnMapping.CurrentCensusCol,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.FailRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule65WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 65);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);

            var workspace = new Rule65WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                CancellationTable = string.IsNullOrWhiteSpace(row.StudTable) ? "canceliation list" : row.StudTable,
                ClientTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "CENSUS_LIST_CLIENT" : row.DeceasedTable,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (summary != null)
            {
                workspace.CurrentStatus = summary.Status;
                workspace.CancellationTable = summary.CancellationTable;
                workspace.ClientTable = summary.ClientTable;
                workspace.UseClientCensusTable = summary.UseClientCensusTable;
                workspace.ColumnMapping = summary.ColumnMapping;
            }

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            workspace.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(clientId, currentUser.Id) ?? ""
                : "";

            var signoffs = await _systemDb.GetRuleRunSignoffsAsync(workspace.RunId!.Value, currentUser?.Id);
            workspace.HasDataAnalystSignoff = signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            var mySignoff = signoffs.FirstOrDefault(s => ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, workspace.CurrentUserEngagementRole));
            workspace.CurrentUserHasSignedOff = mySignoff != null;
            workspace.CurrentUserSignoffComment = mySignoff?.Comment ?? "";
            workspace.IsWorkspaceSaved = await _systemDb.IsWorkspaceSavedAsync(workspace.RunId!.Value);

            if (workspace.Summary != null)
            {
                workspace.Summary.SavedRunId = workspace.RunId;
                ApplyBrowserPreview(workspace.Summary);
            }
            return workspace;
        }

        public async Task<Rule65RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 65);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary.SavedRunId = runId;

            var viewModel = new Rule65RunReviewViewModel
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
            viewModel.HasDataAnalystSignoff = viewModel.Signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            viewModel.GeneratedSql = GenerateSql(new Rule65ValidationRequest
            {
                ClientId = viewModel.ClientId,
                CancellationTable = summary.CancellationTable,
                ClientTable = summary.ClientTable,
                UseClientCensusTable = summary.UseClientCensusTable,
                ColumnMapping = summary.ColumnMapping
            });

            ApplyBrowserPreview(summary);
            return viewModel;
        }

        public async Task<Rule65WorkspaceSaveResult> SaveWorkspaceAsync(Rule65ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule65WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule65WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.CancellationTable,
                    DeceasedTable = request.ClientTable,
                    StudColumn = request.ColumnMapping.CancelDateCol,
                    DeceasedColumn = request.ColumnMapping.CurrentCensusCol
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule65WorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new Rule65WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule65WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule65WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule65WorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new Rule65WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Your account could not be resolved in the system database.");
            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Validation run not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var role = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(role))
                throw new InvalidOperationException("Only assigned data analysts, managers, and directors can sign off.");

            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) &&
                !await _systemDb.HasRuleSignoffRoleAsync(runId, "DataAnalyst"))
                throw new InvalidOperationException("The assigned data analyst must sign off first.");

            await _systemDb.AddOrUpdateRuleSignoffAsync(runId, clientId, reviewer.Id, role!, comment);
        }

        public async Task RemoveSignoffAsync(int runId, string reviewerEmail)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Your account could not be resolved.");
            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Validation run not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);
            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<Rule65ValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 65);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static void ApplyBrowserPreview(Rule65ValidationSummary? summary)
        {
            if (summary == null) return;

            var failRows = summary.FailRows ?? new List<Rule65ReviewRow>();
            var passRows = summary.PassRows ?? new List<Rule65ReviewRow>();

            summary.IsPreviewOnly = failRows.Count > BrowserPreviewRowLimit || passRows.Count > BrowserPreviewRowLimit;
            summary.FailRows = failRows.Take(BrowserPreviewRowLimit).ToList();
            summary.PassRows = passRows.Take(BrowserPreviewRowLimit).ToList();
            summary.PreviewLimit = summary.IsPreviewOnly ? BrowserPreviewRowLimit : 0;
        }

        private static List<Rule65ExceptionCategoryViewModel> BuildExceptionCategories(
            IReadOnlyCollection<Rule65ReviewRow> passRows,
            IReadOnlyCollection<Rule65ReviewRow> failRows)
        {
            return passRows
                .Concat(failRows)
                .GroupBy(row => ResolveExceptionCategory(row), StringComparer.OrdinalIgnoreCase)
                .Select(group => new Rule65ExceptionCategoryViewModel
                {
                    Category = group.Key,
                    Description = GetExceptionCategoryDescription(group.Key),
                    Count = group.Count()
                })
                .OrderByDescending(category => category.Count)
                .ThenBy(category => category.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<Rule65ReviewRow> DeduplicateRows(IEnumerable<Rule65ReviewRow> rows)
        {
            var deduplicated = new List<Rule65ReviewRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows ?? Enumerable.Empty<Rule65ReviewRow>())
            {
                var key = string.Join("|", new[]
                {
                    row.SourceTable?.Trim() ?? "",
                    row.StudentNo?.Trim() ?? "",
                    row.Qualification?.Trim() ?? "",
                    row.Subject?.Trim() ?? "",
                    row.CancelDate?.Trim() ?? "",
                    row.CensusDate?.Trim() ?? "",
                    row.CurrentCensus?.Trim() ?? "",
                    row.ExceptionCategory?.Trim() ?? "",
                    row.ValidationResult?.Trim() ?? "",
                    row.ErrorCode?.Trim() ?? ""
                });

                if (!seen.Add(key))
                    continue;

                deduplicated.Add(row);
            }

            return deduplicated;
        }

        private static void AssignRowNumbers(List<Rule65ReviewRow> rows)
        {
            for (var index = 0; index < rows.Count; index++)
                rows[index].RowNumber = index + 1;
        }

        private static string ResolveExceptionCategory(Rule65ReviewRow row)
        {
            if (!string.IsNullOrWhiteSpace(row.ExceptionCategory))
                return row.ExceptionCategory.Trim().ToUpperInvariant();

            if (string.Equals(row.ValidationResult, "PASS", StringComparison.OrdinalIgnoreCase))
                return "PASS_NOT_ON_CENSUS";

            return string.IsNullOrWhiteSpace(row.ErrorCode)
                ? "FAIL_OTHER"
                : row.ErrorCode.Trim().ToUpperInvariant();
        }

        private static string GetExceptionCategoryDescription(string category) =>
            category.ToUpperInvariant() switch
            {
                "PASS_NOT_ON_CENSUS" => "Cancel date does not equal the row census date and does not match CURRENT_CENSUS",
                "CANCEL_EQUALS_CENSUS" => "Cancel date equals the row census date",
                "CURRENT_CENSUS_MATCH" => "Cancel date matches CURRENT_CENSUS in CENSUS_LIST_CLIENT",
                "CANCEL_EQUALS_CENSUS_AND_CURRENT_CENSUS" => "Cancel date equals the row census date and matches CURRENT_CENSUS",
                "INVALID_CANCEL_DATE" => "Cancel value could not be converted to a valid date",
                "FAIL_OTHER" => "Other Rule 65 failure",
                _ => category
            };

        private static readonly string[] DateFormats =
        {
            "yyyy-MM-dd", "yyyy/MM/dd",
            "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "M/d/yyyy",
            "dd-MM-yyyy", "d-M-yyyy",
            "dd MMM yyyy", "d MMM yyyy", "dd MMMM yyyy", "d MMMM yyyy",
            "dd-MMM-yyyy", "d-MMM-yyyy"
        };

        private static DateOnly? ParseDateOrNull(string? value)
        {
            var raw = (value ?? "").Trim();
            if (raw.Length == 0) return null;

            foreach (var format in DateFormats)
            {
                if (DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                    return DateOnly.FromDateTime(exact);
            }

            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return DateOnly.FromDateTime(parsed);

            return null;
        }

        private static string? GetString(System.Data.Common.DbDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return null;
            var value = Convert.ToString(reader.GetValue(ordinal));
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static string Sanitise(string name) => name.Replace("\"", "").Replace("'", "").Replace(";", "").Trim();

        private static async Task<int> CountAsync(NpgsqlConnection conn, string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        // Column names come from defaults or a previously-saved workspace and may not match this
        // engagement's actual uploaded table - check before querying so a mismatch surfaces as a
        // clear message instead of a raw Postgres "column does not exist" error.
        private async Task ValidateColumnsExistAsync(int clientId, string tableName, IEnumerable<string> requiredColumns)
        {
            var actual = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
            var missing = requiredColumns.Where(c => !string.IsNullOrWhiteSpace(c) && !actual.Contains(c, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Column(s) {string.Join(", ", missing.Select(m => $"\"{m}\""))} were not found in table \"{tableName}\". " +
                    "Update the column mapping to match your uploaded data, then run again.");
        }

        private static string? FindFirst(IEnumerable<string> values, string[] exactMatches, string[] containsMatches)
        {
            foreach (var exact in exactMatches)
            {
                var m = values.FirstOrDefault(v => string.Equals(v, exact, StringComparison.OrdinalIgnoreCase));
                if (m != null) return m;
            }
            foreach (var fragment in containsMatches)
            {
                var m = values.FirstOrDefault(v => v.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
                if (m != null) return m;
            }
            return null;
        }

        private static Rule65ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule65ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
