using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 66: NSFAS Students in CREG — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. Population is every distinct
    // STUD student+qualification pair whose funding-source column matches the configured
    // filter values (blank filter = every student, no funding restriction). A student clears
    // this control the moment ANY of their qualifications has a matching CREG record — a
    // match on one qualification is enough to pass the whole student, so a different
    // qualification not individually matching does not raise an exception for them. Only a
    // student with zero matches across every qualification they hold is a genuine FAIL.
    public class Rule66Service : IRule66Service
    {
        private const int BrowserPreviewRowLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule66Service(
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

        public async Task<Rule66TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule66TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule66TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD_VALPAC", "STUD_VALPAC"], ["stud_valpac", "dbo_stud"]),
                    AutoCregTable = FindFirst(tables, ["dbo_CREG", "CREG"], ["creg"])
                };
            }
            catch (Exception ex) { return new Rule66TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule66ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "stud_student_no"     => FindFirst(cols, ["_007"], []),
                    "stud_funding_source" => FindFirst(cols, ["_019"], []),
                    "stud_qual_code"      => FindFirst(cols, ["_001"], []),
                    "creg_student_no"     => FindFirst(cols, ["_007"], []),
                    "creg_qual_code"      => FindFirst(cols, ["_001"], []),
                    _ => null
                };
                return new Rule66ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule66ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        // Backs the funding-source filter's value picker — real distinct values from the
        // uploaded column instead of free text, so a filter can't silently match nothing
        // because of a typo or a formatting mismatch against the actual data.
        public async Task<Rule66DistinctValuesResult> GetDistinctValuesAsync(int clientId, string tableName, string columnName)
        {
            try
            {
                var values = (await _datasets.GetDistinctColumnValuesAsync(clientId, tableName, columnName, take: 200))
                    .Select(v => v.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new Rule66DistinctValuesResult { Success = true, Values = values };
            }
            catch (Exception ex) { return new Rule66DistinctValuesResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule66VerifyResult> VerifyTablesAsync(Rule66ValidationRequest request)
        {
            try
            {
                var studQualColForVerify = Sanitise(string.IsNullOrWhiteSpace(request.StudQualCol) ? "_001" : request.StudQualCol);
                var cregQualColForVerify = Sanitise(string.IsNullOrWhiteSpace(request.CregQualCol) ? "_001" : request.CregQualCol);
                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, [request.StudStudentNoCol, studQualColForVerify]);
                await ValidateColumnsExistAsync(request.ClientId, request.CregTable, [request.CregStudentNoCol, cregQualColForVerify]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var st = Sanitise(request.StudTable);
                var ct = Sanitise(request.CregTable);
                var fundingCol = Sanitise(string.IsNullOrWhiteSpace(request.FundingSourceCol) ? "_019" : request.FundingSourceCol);
                var fundingValues = ParseFilterValues(request.FundingSourceValues);

                var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{st}\";");
                var cregCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{ct}\";");

                int nsfasCount;
                if (fundingValues.Count > 0)
                {
                    var inList = BuildInClauseSql(fundingValues);
                    nsfasCount = await CountAsync(connection, $@"
SELECT COUNT(*) FROM ""{schema}"".""{st}""
WHERE UPPER(TRIM(CAST(""{fundingCol}"" AS text))) IN ({inList});");
                }
                else
                {
                    nsfasCount = studCount;
                }

                return new Rule66VerifyResult { Success = true, StudRecordCount = studCount, StudNsfasCount = nsfasCount, CregRecordCount = cregCount };
            }
            catch (Exception ex) { return new Rule66VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule66ValidationSummary> RunValidationAsync(Rule66ValidationRequest request, string? userEmail = null, string? userName = null)
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
            catch (Exception ex) { return new Rule66ValidationSummary { Success = false, Error = ex.Message }; }
        }

        private async Task<Rule66ValidationSummary> AnalyseAsync(Rule66ValidationRequest request)
        {
            var studQualCol = Sanitise(string.IsNullOrWhiteSpace(request.StudQualCol) ? "_001" : request.StudQualCol);
            var cregQualCol = Sanitise(string.IsNullOrWhiteSpace(request.CregQualCol) ? "_001" : request.CregQualCol);
            await ValidateColumnsExistAsync(request.ClientId, request.StudTable, [request.StudStudentNoCol, request.FundingSourceCol, studQualCol]);
            await ValidateColumnsExistAsync(request.ClientId, request.CregTable, [request.CregStudentNoCol, cregQualCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var studNoCol = Sanitise(request.StudStudentNoCol);
            var cregNoCol = Sanitise(request.CregStudentNoCol);
            var fundingCol = Sanitise(string.IsNullOrWhiteSpace(request.FundingSourceCol) ? "_019" : request.FundingSourceCol);
            var fundingValues = ParseFilterValues(request.FundingSourceValues);
            var fundingValuesText = fundingValues.Count > 0 ? string.Join(", ", fundingValues) : "ALL — no filter applied";

            var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.StudTable)}\";");
            var cregCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CregTable)}\";");

            var ctes = BuildValidationCtes(schema, request, studNoCol, cregNoCol, studQualCol, cregQualCol, fundingCol, fundingValues);

            int nsfasCount = 0, matched = 0, missing = 0;
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = $@"
WITH {ctes}
SELECT
    COUNT(*) AS total,
    COUNT(*) FILTER (WHERE validation_result = 'PASS') AS pass_count,
    COUNT(*) FILTER (WHERE validation_result = 'FAIL') AS fail_count
FROM results;";
                await using var reader = await countCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    nsfasCount = Convert.ToInt32(reader.GetValue(0));
                    matched    = Convert.ToInt32(reader.GetValue(1));
                    missing    = Convert.ToInt32(reader.GetValue(2));
                }
            }

            var reviewRows = new List<Rule66ValidationRowRecord>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
WITH {ctes}
SELECT stud_no, qual_code, funding_source, creg_stud_no, validation_result, direct_match FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, stud_no;";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new Rule66ValidationRowRecord
                    {
                        ValidationNumber = reviewRows.Count + 1,
                        ControlType = "Control_1",
                        ControlLabel = "Control 1",
                        ValidationResult = GetString(reader, 4) ?? "FAIL",
                        DisplayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["STUD_NO"] = GetString(reader, 0),
                            ["QUAL_CODE"] = GetString(reader, 1),
                            ["FUNDING_SOURCE"] = GetString(reader, 2),
                            ["CREG_STUD_NO"] = GetString(reader, 3),
                            ["DIRECT_MATCH"] = reader.IsDBNull(5) ? "FALSE" : reader.GetBoolean(5) ? "TRUE" : "FALSE"
                        }
                    };
                    EnrichDisplayValues(row);
                    reviewRows.Add(row);
                }
            }

            var controlSummaries = new List<Rule66ControlSummaryItemViewModel>
            {
                new()
                {
                    ControlType  = "Control_1",
                    ControlLabel = "Control 1",
                    CriteriaText = $"Every NSFAS student in {request.StudTable} (where [{fundingCol}] IN ({fundingValuesText})) must have at least one [{studNoCol}]+[{studQualCol}] pair matching a [{cregNoCol}]+[{cregQualCol}] pair in {request.CregTable} — a match on any one of the student's qualifications clears the student",
                    TotalCount   = nsfasCount,
                    PassCount    = matched,
                    FailCount    = missing,
                    Status       = missing == 0 ? "PASS" : "FAIL"
                }
            };

            return new Rule66ValidationSummary
            {
                Success          = true,
                StudRecordCount  = studCount,
                StudNsfasCount   = nsfasCount,
                CregRecordCount  = cregCount,
                TotalValidated   = nsfasCount,
                DisplayedCount   = reviewRows.Count,
                PassCount        = matched,
                FailCount        = missing,
                ExceptionRate    = nsfasCount == 0 ? 0m : Math.Round(missing * 100m / nsfasCount, 2),
                Status           = missing == 0 ? "PASS" : "FAIL",
                Timestamp        = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable        = request.StudTable,
                CregTable        = request.CregTable,
                StudStudentNoCol = studNoCol,
                CregStudentNoCol = cregNoCol,
                StudQualCol      = studQualCol,
                CregQualCol      = cregQualCol,
                FundingSourceCol    = fundingCol,
                FundingSourceValues = fundingValues.Count > 0 ? string.Join(",", fundingValues) : "",
                TableLinkageText = $"{request.StudTable}.[{studNoCol}]+[{studQualCol}] <> {request.CregTable}.[{cregNoCol}]+[{cregQualCol}] (filtered to [{fundingCol}] IN {fundingValuesText})",
                RuleModeText     = $"NSFAS students where [{fundingCol}] IN ({fundingValuesText}) in {request.StudTable} must have at least one qualification matched as a [{cregNoCol}]+[{cregQualCol}] pair in {request.CregTable}",
                ProcedureSteps   = new List<string>
                {
                    $"Filter {request.StudTable} to rows where [{fundingCol}] IN ({fundingValuesText}) — these are the NSFAS student population, one row per student+qualification pair.",
                    $"For each NSFAS student number [{studNoCol}] + qualification code [{studQualCol}] pair, check if a matching pair exists in {request.CregTable} on columns [{cregNoCol}]+[{cregQualCol}].",
                    "A student PASSes as soon as any one of their qualification pairs matches in CREG — that match clears every row for that student, even a different qualification of theirs with no direct match.",
                    "FAIL is reserved for a student with zero matches in CREG across every qualification they hold in STUD."
                },
                ClientId         = request.ClientId,
                ControlSummaries = controlSummaries,
                ReviewRows       = reviewRows,
                Warning = null
            };
        }

        private static void EnrichDisplayValues(Rule66ValidationRowRecord row)
        {
            var v = row.DisplayValues;
            var isPass = string.Equals(row.ValidationResult, "PASS", StringComparison.OrdinalIgnoreCase);
            var isDirectMatch = string.Equals(v.TryGetValue("DIRECT_MATCH", out var dm) ? dm : null, "TRUE", StringComparison.OrdinalIgnoreCase);
            var studNo = v.TryGetValue("STUD_NO", out var sn) ? sn ?? "" : "";
            var qualCode = v.TryGetValue("QUAL_CODE", out var qc) ? qc ?? "" : "";
            var fundingSrc = v.TryGetValue("FUNDING_SOURCE", out var fs) ? fs ?? "" : "";

            v["FINAL_RESULT_MESSAGE"] = (isPass, isDirectMatch) switch
            {
                (true, true)  => $"PASS: NSFAS student '{studNo}' qualification '{qualCode}' (funding: {fundingSrc}) found in CREG.",
                (true, false) => $"PASS: NSFAS student '{studNo}' qualification '{qualCode}' (funding: {fundingSrc}) has no direct CREG match, but the student passed on another qualification — no exception raised for this student.",
                _             => $"FAIL: NSFAS student '{studNo}' qualification '{qualCode}' (funding: {fundingSrc}) not found in CREG — the student has no matching course registration record for any qualification."
            };
            row.ValidationExplanation = v["FINAL_RESULT_MESSAGE"] ?? "";
        }

        // ── SQL builders ──────────────────────────────────────────────────────

        // Returns only the CTE definitions (no trailing SELECT) so callers can each append
        // their own final SELECT against "results" without illegal nested-WITH syntax.
        private static string BuildValidationCtes(string schema, Rule66ValidationRequest request, string studNoCol, string cregNoCol, string studQualCol, string cregQualCol, string fundingCol, IReadOnlyList<string> fundingValues)
        {
            var st = Sanitise(request.StudTable);
            var ct = Sanitise(request.CregTable);
            var fundingFilter = fundingValues.Count > 0
                ? $"AND UPPER(TRIM(CAST(s.\"{fundingCol}\" AS text))) IN ({BuildInClauseSql(fundingValues)})"
                : "";

            return $@"
nsfas_students AS (
    SELECT DISTINCT
        TRIM(CAST(s.""{studNoCol}"" AS text)) AS stud_no,
        UPPER(TRIM(CAST(s.""{studQualCol}"" AS text))) AS qual_code,
        TRIM(CAST(s.""{fundingCol}"" AS text)) AS funding_source
    FROM ""{schema}"".""{st}"" s
    WHERE TRIM(CAST(s.""{studNoCol}"" AS text)) <> ''
    {fundingFilter}
),
creg_pairs AS (
    SELECT DISTINCT
        TRIM(CAST(c.""{cregNoCol}"" AS text)) AS creg_stud_no,
        UPPER(TRIM(CAST(c.""{cregQualCol}"" AS text))) AS creg_qual_code
    FROM ""{schema}"".""{ct}"" c
    WHERE TRIM(CAST(c.""{cregNoCol}"" AS text)) <> ''
),
matched_students AS (
    -- A student clears this control the moment ANY of their qualifications has a matching
    -- CREG record. Auditors test the sample the student is selected into, not every
    -- individual qualification row in isolation — one match is enough to clear the whole
    -- student, so no exception is raised just because a different qualification didn't match.
    SELECT DISTINCT ns.stud_no
    FROM nsfas_students ns
    JOIN creg_pairs cp ON cp.creg_stud_no = ns.stud_no AND cp.creg_qual_code = ns.qual_code
),
results AS (
    SELECT
        ns.stud_no, ns.qual_code, ns.funding_source, cp.creg_stud_no,
        CASE WHEN ms.stud_no IS NOT NULL THEN 'PASS' ELSE 'FAIL' END AS validation_result,
        (cp.creg_stud_no IS NOT NULL) AS direct_match
    FROM nsfas_students ns
    LEFT JOIN creg_pairs cp ON cp.creg_stud_no = ns.stud_no AND cp.creg_qual_code = ns.qual_code
    LEFT JOIN matched_students ms ON ms.stud_no = ns.stud_no
)";
        }

        public string GenerateSql(Rule66ValidationRequest request)
        {
            var studNoCol = Sanitise(request.StudStudentNoCol);
            var cregNoCol = Sanitise(request.CregStudentNoCol);
            var studQualCol = Sanitise(string.IsNullOrWhiteSpace(request.StudQualCol) ? "_001" : request.StudQualCol);
            var cregQualCol = Sanitise(string.IsNullOrWhiteSpace(request.CregQualCol) ? "_001" : request.CregQualCol);
            var fundingCol = Sanitise(string.IsNullOrWhiteSpace(request.FundingSourceCol) ? "_019" : request.FundingSourceCol);
            var fundingValues = ParseFilterValues(request.FundingSourceValues);
            var fundingValuesText = fundingValues.Count > 0 ? string.Join(", ", fundingValues.Select(v => $"'{v}'")) : "ALL — no filter applied";
            var ctes = BuildValidationCtes("{schema}", request, studNoCol, cregNoCol, studQualCol, cregQualCol, fundingCol, fundingValues);

            return $@"-- ============================================================
-- HEMIS RULE 66 - NSFAS Students in CREG
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Check     : Every {Sanitise(request.StudTable)}.[{fundingCol}] IN ({fundingValuesText}) student must have at least one [{studNoCol}]+[{studQualCol}] pair matching a [{cregNoCol}]+[{cregQualCol}] pair in {Sanitise(request.CregTable)}
-- PASS when the student matches on any one qualification (clears every row for that student); FAIL only when none of their qualifications match
-- ============================================================
WITH {ctes}
SELECT * FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, stud_no;".Trim();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule66ValidationRequest request, Rule66ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 66);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 66,
                RuleName = "NSFAS Students in CREG",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.CregTable,
                StudColumn = request.StudStudentNoCol,
                DeceasedColumn = request.CregStudentNoCol,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(
                    summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList())),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule66WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 66);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);

            var workspace = new Rule66WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD_VALPAC" : row.StudTable,
                CregTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_CREG" : row.DeceasedTable,
                StudQualCol = "_001",
                CregQualCol = "_001",
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (summary != null)
            {
                workspace.CurrentStatus = summary.Status;
                workspace.StudTable = summary.StudTable;
                workspace.CregTable = summary.CregTable;
                workspace.StudStudentNoCol = summary.StudStudentNoCol;
                workspace.CregStudentNoCol = summary.CregStudentNoCol;
                workspace.StudQualCol = string.IsNullOrWhiteSpace(summary.StudQualCol) ? "_001" : summary.StudQualCol;
                workspace.CregQualCol = string.IsNullOrWhiteSpace(summary.CregQualCol) ? "_001" : summary.CregQualCol;
                workspace.FundingSourceCol = summary.FundingSourceCol;
                workspace.FundingSourceValues = summary.FundingSourceValues;
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

        public async Task<Rule66RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 66);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary.SavedRunId = runId;

            var viewModel = new Rule66RunReviewViewModel
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
            viewModel.GeneratedSql = GenerateSql(new Rule66ValidationRequest
            {
                ClientId = viewModel.ClientId,
                StudTable = summary.StudTable,
                CregTable = summary.CregTable,
                StudStudentNoCol = summary.StudStudentNoCol,
                CregStudentNoCol = summary.CregStudentNoCol,
                StudQualCol = string.IsNullOrWhiteSpace(summary.StudQualCol) ? "_001" : summary.StudQualCol,
                CregQualCol = string.IsNullOrWhiteSpace(summary.CregQualCol) ? "_001" : summary.CregQualCol,
                FundingSourceCol = summary.FundingSourceCol,
                FundingSourceValues = summary.FundingSourceValues
            });

            ApplyBrowserPreview(summary);
            return viewModel;
        }

        public async Task<Rule66WorkspaceSaveResult> SaveWorkspaceAsync(Rule66ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule66WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule66WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.CregTable,
                    StudColumn = request.StudStudentNoCol,
                    DeceasedColumn = request.CregStudentNoCol
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule66WorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new Rule66WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule66WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule66WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule66WorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new Rule66WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        public async Task<Rule66ValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 66);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static void ApplyBrowserPreview(Rule66ValidationSummary summary)
        {
            var rows = summary.ReviewRows;
            if (rows.Count <= BrowserPreviewRowLimit)
            {
                summary.DisplayedCount = rows.Count;
                summary.IsPreviewOnly = false;
                summary.PreviewLimit = 0;
                return;
            }

            var failRows = rows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.ValidationNumber).ToList();
            var passRows = rows.Where(r => !string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.ValidationNumber).ToList();

            var halfLimit = BrowserPreviewRowLimit / 2;
            var failTake = Math.Min(failRows.Count, passRows.Count > 0 ? halfLimit : BrowserPreviewRowLimit);
            var passTake = Math.Min(passRows.Count, BrowserPreviewRowLimit - failTake);

            var preview = failRows.Take(failTake).Concat(passRows.Take(passTake)).ToList();
            summary.ReviewRows = preview;
            summary.DisplayedCount = preview.Count;
            summary.IsPreviewOnly = summary.TotalValidated > preview.Count;
            summary.PreviewLimit = preview.Count;
        }

        private static List<string> ParseFilterValues(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(SanitiseFilterValue)
                .Where(v => v.Length > 0)
                .Select(v => v.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string SanitiseFilterValue(string value) =>
            value.Replace("'", "").Replace(";", "").Replace("--", "").Trim();

        private static string BuildInClauseSql(IReadOnlyList<string> values) =>
            string.Join(",", values.Select(v => $"'{v.Replace("'", "''")}'"));

        private static string Sanitise(string name) => name.Replace("\"", "").Replace("'", "").Replace(";", "").Trim();

        private static string? GetString(System.Data.Common.DbDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return null;
            var value = Convert.ToString(reader.GetValue(ordinal));
            return string.IsNullOrEmpty(value) ? null : value;
        }

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

        private static Rule66ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule66ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
