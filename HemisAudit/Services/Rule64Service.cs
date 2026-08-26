using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 64: STUD to CREG Student Number Validation — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. A STUD student number
    // PASSes if it exists in CREG AND at least one of that student's compare values (e.g. STUD._001
    // qualification code) matches one of CREG's compare values for the same student - a student
    // with multiple qualification rows only needs one match to fully pass. FAILs are further
    // confirmed against the STUD PRODUCTION table for context in the exception explanation.
    public class Rule64Service : IRule64Service
    {
        private const int RowLimit = 5000;
        private const int BrowserPreviewRowLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule64Service(
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

        public async Task<Rule64TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule64TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule64TableDiscoveryResult
                {
                    Success       = true,
                    Tables        = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "STUD"], ["stud"]),
                    AutoCregTable = FindFirst(tables, ["dbo_CREG", "CREG"], ["creg"]),
                    AutoProdTable = FindFirst(tables, ["dbo_STUD_PRODUCTION", "STUD_PRODUCTION", "MT-audit-prod-std", "MT_AUDIT_PROD_STD", "mt_audit_prod_std"], [])
                                    ?? FindFirstContainsAll(tables, "prod", "std")
                };
            }
            catch (Exception ex) { return new Rule64TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule64ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "stud_student_no"    => FindFirst(cols, ["_007"], []),
                    "stud_compare_value" => FindFirst(cols, ["_001"], []),
                    "creg_student_no"    => FindFirst(cols, ["_007"], []),
                    "creg_compare_value" => FindFirst(cols, ["_001"], []),
                    "prod_student_no"    => FindFirst(cols, ["IAGSTNO"], ["stno"]),
                    _ => null
                };
                return new Rule64ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule64ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule64VerifyResult> VerifyTablesAsync(Rule64ValidationRequest request)
        {
            try
            {
                var m = NormalizeMapping(request.ColumnMapping);
                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, [m.StudStudentNoCol]);
                await ValidateColumnsExistAsync(request.ClientId, request.CregTable, [m.CregStudentNoCol]);
                await ValidateColumnsExistAsync(request.ClientId, request.ProdTable, [m.ProdStudentNoCol]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                return new Rule64VerifyResult
                {
                    Success   = true,
                    StudCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.StudTable)}\";"),
                    CregCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CregTable)}\";"),
                    ProdCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.ProdTable)}\";")
                };
            }
            catch (Exception ex) { return new Rule64VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule64ValidationSummary> RunValidationAsync(Rule64ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                request.ColumnMapping = NormalizeMapping(request.ColumnMapping);

                var summary = await AnalyseAsync(request, RowLimit);

                if (summary.Success && request.ClientId > 0)
                {
                    try { summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName); }
                    catch (Exception ex)
                    {
                        summary.Success = false;
                        summary.Error   = $"Analysis completed, but the run could not be saved: {ex.Message}";
                        return summary;
                    }
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex) { return new Rule64ValidationSummary { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule64ValidationSummary> GetExportSummaryAsync(Rule64ValidationRequest request)
            => await AnalyseAsync(request, rowLimit: null);

        public async Task<int> GetPopulationCountAsync(Rule64ValidationRequest req)
        {
            var m = NormalizeMapping(req.ColumnMapping);
            await ValidateColumnsExistAsync(req.ClientId, req.StudTable, [m.StudStudentNoCol, m.StudCompareValueCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.CregTable, [m.CregStudentNoCol, m.CregCompareValueCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.ProdTable, [m.ProdStudentNoCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var cteSql = BuildValidationCtes(schema, req.StudTable, req.CregTable, req.ProdTable, m);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"WITH {cteSql} SELECT COUNT(*) FROM results;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private async Task<Rule64ValidationSummary> AnalyseAsync(Rule64ValidationRequest req, int? rowLimit)
        {
            var m = NormalizeMapping(req.ColumnMapping);

            await ValidateColumnsExistAsync(req.ClientId, req.StudTable, [m.StudStudentNoCol, m.StudCompareValueCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.CregTable, [m.CregStudentNoCol, m.CregCompareValueCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.ProdTable, [m.ProdStudentNoCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var cteSql = BuildValidationCtes(schema, req.StudTable, req.CregTable, req.ProdTable, m);

            var countSql = $@"
WITH {cteSql}
SELECT
    COUNT(*) AS total,
    COUNT(*) FILTER (WHERE validation_result = 'PASS') AS pass_count
FROM results;";

            int total, passed;
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = countSql;
                await using var reader = await countCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    total  = Convert.ToInt32(reader.GetValue(0));
                    passed = Convert.ToInt32(reader.GetValue(1));
                }
                else { total = passed = 0; }
            }

            var rowsSql = $@"
WITH {cteSql}
SELECT * FROM results
ORDER BY
    CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END,
    student_no
{(rowLimit.HasValue ? "LIMIT @limit" : "")};";

            var passRows = new List<Rule64ReviewRow>();
            var failRows = new List<Rule64ReviewRow>();

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = rowsSql;
                if (rowLimit.HasValue) cmd.Parameters.AddWithValue("limit", rowLimit.Value);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new Rule64ReviewRow
                    {
                        SourceTable           = GetString(reader, "source_table") ?? "",
                        StudentNo             = GetString(reader, "student_no") ?? "",
                        CregStudentNo         = GetString(reader, "creg_student_no") ?? "",
                        ProdStudentNo         = GetString(reader, "prod_student_no") ?? "",
                        StudCompareValue      = GetString(reader, "stud_compare_value") ?? "",
                        CregCompareValue      = GetString(reader, "creg_compare_value") ?? "",
                        ErrorCode             = GetString(reader, "error_code") ?? "",
                        ValidationResult      = (GetString(reader, "validation_result") ?? "").Trim().ToUpperInvariant(),
                        ValidationExplanation = GetString(reader, "validation_explanation") ?? ""
                    };
                    row.ExceptionCategory = ResolveExceptionCategory(row);

                    if (row.ValidationResult == "PASS") passRows.Add(row); else failRows.Add(row);
                }
            }

            failRows = DeduplicateRows(failRows);
            passRows = DeduplicateRows(passRows);
            AssignRowNumbers(failRows);
            AssignRowNumbers(passRows);

            var failCount = total - passed;
            var exceptionRate = total == 0 ? 0m : Math.Round((decimal)failCount / total * 100m, 2);

            var summary = new Rule64ValidationSummary
            {
                Success              = true,
                Timestamp            = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable            = req.StudTable,
                CregTable            = req.CregTable,
                ProdTable            = req.ProdTable,
                ColumnMapping        = m,
                ClientId             = req.ClientId,
                TotalCount           = total,
                PassCount            = passed,
                FailCount            = failCount,
                ExceptionDetailCount = failCount,
                ExceptionRate        = exceptionRate,
                Status               = failCount == 0 ? "PASS" : "FAIL",
                PassRows             = passRows,
                FailRows             = failRows,
                ExceptionCategories  = BuildExceptionCategories(passRows, failRows),
                Warning = total > (passRows.Count + failRows.Count)
                    ? $"{total:N0} rows were found; only the first {(passRows.Count + failRows.Count):N0} are stored and shown to keep the app responsive. All totals above are exact."
                    : null
            };

            return summary;
        }

        private static string BuildValidationCtes(string schema, string studTable, string cregTable, string prodTable, Rule64ColumnMapping m)
        {
            var stud = Sanitise(studTable);
            var creg = Sanitise(cregTable);
            var prod = Sanitise(prodTable);

            return $@"
    creg_ref AS (
        SELECT DISTINCT
            UPPER(TRIM(CAST(CREG.""{m.CregStudentNoCol}"" AS text))) AS creg_student_no,
            UPPER(TRIM(COALESCE(CAST(CREG.""{m.CregCompareValueCol}"" AS text), ''))) AS creg_compare_value
        FROM ""{schema}"".""{creg}"" CREG
        WHERE CREG.""{m.CregStudentNoCol}"" IS NOT NULL
          AND TRIM(CAST(CREG.""{m.CregStudentNoCol}"" AS text)) <> ''
    ),
    creg_summary AS (
        SELECT
            creg_student_no,
            STRING_AGG(CASE WHEN creg_compare_value = '' THEN '[BLANK]' ELSE creg_compare_value END, ', ' ORDER BY creg_compare_value) AS creg_compare_value
        FROM creg_ref
        GROUP BY creg_student_no
    ),
    prod_ref AS (
        SELECT DISTINCT UPPER(TRIM(CAST(PROD.""{m.ProdStudentNoCol}"" AS text))) AS prod_student_no
        FROM ""{schema}"".""{prod}"" PROD
        WHERE PROD.""{m.ProdStudentNoCol}"" IS NOT NULL
          AND TRIM(CAST(PROD.""{m.ProdStudentNoCol}"" AS text)) <> ''
    ),
    stud_pop AS (
        SELECT DISTINCT
            UPPER(TRIM(CAST(STUD.""{m.StudStudentNoCol}"" AS text))) AS student_no,
            UPPER(TRIM(COALESCE(CAST(STUD.""{m.StudCompareValueCol}"" AS text), ''))) AS stud_compare_value
        FROM ""{schema}"".""{stud}"" STUD
        WHERE STUD.""{m.StudStudentNoCol}"" IS NOT NULL
          AND TRIM(CAST(STUD.""{m.StudStudentNoCol}"" AS text)) <> ''
    ),
    creg_student_match AS (
        SELECT DISTINCT P.student_no
        FROM stud_pop P
        INNER JOIN creg_ref CR ON CR.creg_student_no = P.student_no AND CR.creg_compare_value = P.stud_compare_value
    ),
    results AS (
        SELECT
            'STUD' AS source_table,
            COALESCE(P.student_no, '') AS student_no,
            COALESCE(CS.creg_student_no, '') AS creg_student_no,
            COALESCE(PR.prod_student_no, '') AS prod_student_no,
            COALESCE(P.stud_compare_value, '') AS stud_compare_value,
            COALESCE(CS.creg_compare_value, '') AS creg_compare_value,
            CASE
                WHEN CS.creg_student_no IS NULL THEN 'NOTE'
                WHEN MM.student_no IS NULL THEN 'MISMATCH'
                ELSE ''
            END AS error_code,
            CASE
                WHEN CS.creg_student_no IS NULL THEN 'FAIL'
                WHEN MM.student_no IS NULL THEN 'FAIL'
                ELSE 'PASS'
            END AS validation_result,
            CASE
                WHEN CS.creg_student_no IS NULL AND PR.prod_student_no IS NULL
                    THEN 'FAIL: STUD.{m.StudStudentNoCol} student number ''' || P.student_no || ''' was not found in CREG.{m.CregStudentNoCol}. Note: this student should not appear in production. Confirmation: it was not found in STUD PRODUCTION.{m.ProdStudentNoCol}.'
                WHEN CS.creg_student_no IS NULL
                    THEN 'FAIL: STUD.{m.StudStudentNoCol} student number ''' || P.student_no || ''' was not found in CREG.{m.CregStudentNoCol}. Note: this student should not appear in production. Confirmation: it exists in STUD PRODUCTION.{m.ProdStudentNoCol} as ''' || PR.prod_student_no || '''.'
                WHEN MM.student_no IS NULL
                    THEN 'FAIL: STUD.{m.StudStudentNoCol} student number ''' || P.student_no || ''' exists in CREG.{m.CregStudentNoCol}, but none of the student''s {m.StudCompareValueCol} values match any CREG.{m.CregCompareValueCol} value(s). CREG has: ''' || COALESCE(CS.creg_compare_value, '') || '''.'
                ELSE 'PASS: STUD.{m.StudStudentNoCol} student number ''' || P.student_no || ''' exists in CREG.{m.CregStudentNoCol} with at least one matching {m.CregCompareValueCol} value. CREG {m.CregCompareValueCol} value(s): ''' || COALESCE(CS.creg_compare_value, '') || '''.'
            END AS validation_explanation
        FROM stud_pop P
        LEFT JOIN creg_summary       CS ON CS.creg_student_no = P.student_no
        LEFT JOIN creg_student_match MM ON MM.student_no = P.student_no
        LEFT JOIN prod_ref           PR ON PR.prod_student_no = P.student_no
    )";
        }

        // ── SQL generation ────────────────────────────────────────────────────

        public string GenerateSql(Rule64ValidationRequest request)
        {
            var m = NormalizeMapping(request.ColumnMapping);
            var cteSql = BuildValidationCtes("{schema}", request.StudTable, request.CregTable, request.ProdTable, m);

            return $@"-- ============================================================
-- HEMIS RULE 64 - STUD to CREG Student Number Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Tables    : ""{Sanitise(request.StudTable)}"" STUD | ""{Sanitise(request.CregTable)}"" CREG | ""{Sanitise(request.ProdTable)}"" STUD PRODUCTION
-- Join      : STUD.{m.StudStudentNoCol} -> CREG.{m.CregStudentNoCol}
-- Compare   : STUD.{m.StudCompareValueCol} -> CREG.{m.CregCompareValueCol}
-- Confirm   : STUD.{m.StudStudentNoCol} -> STUD PRODUCTION.{m.ProdStudentNoCol}
-- Rule      : PASS when the STUD student number exists in CREG and AT LEAST ONE of the student's compare values matches
--           : PASS even if a specific row's compare value doesn't match, as long as another row for the same student matches
--           : FAIL when the student number is missing from CREG entirely
--           : FAIL when the student number exists in CREG but NONE of the student's compare values match any CREG compare values
-- Fail Note : Student should not appear in production; confirmation uses STUD PRODUCTION
-- ============================================================
WITH {cteSql}
SELECT * FROM results
ORDER BY
    CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END,
    student_no;".Trim();
        }

        // ─── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule64ValidationRequest request, Rule64ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 64);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 64,
                RuleName = "STUD to CREG Student Number Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalCount,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.CregTable,
                StudColumn = request.ProdTable,
                DeceasedColumn = request.ColumnMapping.StudStudentNoCol,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.FailRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule64ValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 64);
            if (row == null) return null;
            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        public async Task<Rule64WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 64);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) ApplyBrowserPreview(summary);

            var workspace = new Rule64WorkspaceStateViewModel
            {
                ClientId      = row.ClientId,
                RunId         = row.RunId,
                StudTable     = summary?.StudTable ?? (string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable),
                CregTable     = summary?.CregTable ?? (string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_CREG" : row.DeceasedTable),
                ProdTable     = summary?.ProdTable ?? (string.IsNullOrWhiteSpace(row.StudColumn) ? "dbo_STUD_PRODUCTION" : row.StudColumn),
                ColumnMapping = summary?.ColumnMapping ?? new Rule64ColumnMapping(),
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt  = row.LastEditedAt,
                Summary       = summary
            };

            if (summary != null) workspace.CurrentStatus = summary.Status;

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            workspace.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(clientId, currentUser.Id) ?? ""
                : "";

            var signoffs = await _systemDb.GetRuleRunSignoffsAsync(workspace.RunId!.Value, currentUser?.Id);
            workspace.HasDataAnalystSignoff = signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            var mySignoff = signoffs.FirstOrDefault(s => ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, workspace.CurrentUserEngagementRole));
            workspace.CurrentUserHasSignedOff   = mySignoff != null;
            workspace.CurrentUserSignoffComment = mySignoff?.Comment ?? "";
            workspace.IsWorkspaceSaved          = await _systemDb.IsWorkspaceSavedAsync(workspace.RunId!.Value);

            if (workspace.Summary != null) workspace.Summary.SavedRunId = workspace.RunId;
            return workspace;
        }

        public async Task<Rule64RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 64);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary.SavedRunId = runId;
            ApplyBrowserPreview(summary);

            var viewModel = new Rule64RunReviewViewModel
            {
                RunId          = row.RunId,
                ClientId       = row.ClientId,
                IsCurrentRun   = row.IsCurrent,
                EngagementName = row.EngagementName,
                MaconomyNumber = row.MaconomyNumber,
                Summary        = summary
            };

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            viewModel.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(viewModel.ClientId, currentUser.Id) ?? ""
                : "";
            viewModel.Signoffs              = await _systemDb.GetRuleRunSignoffsAsync(runId, currentUser?.Id);
            viewModel.HasDataAnalystSignoff = viewModel.Signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            viewModel.GeneratedSql = GenerateSql(new Rule64ValidationRequest
            {
                ClientId = viewModel.ClientId,
                StudTable = summary.StudTable,
                CregTable = summary.CregTable,
                ProdTable = summary.ProdTable,
                ColumnMapping = summary.ColumnMapping
            });

            return viewModel;
        }

        public async Task<Rule64WorkspaceSaveResult> SaveWorkspaceAsync(Rule64ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule64WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule64WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                request.ColumnMapping = NormalizeMapping(request.ColumnMapping);

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.CregTable,
                    StudColumn = request.ProdTable,
                    DeceasedColumn = request.ColumnMapping.StudStudentNoCol
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule64WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule64WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule64WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule64WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule64WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule64WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        // ─── Utilities ─────────────────────────────────────────────────────────

        private static void ApplyBrowserPreview(Rule64ValidationSummary? summary)
        {
            if (summary == null) return;

            var failRows = summary.FailRows ?? new List<Rule64ReviewRow>();
            var passRows = summary.PassRows ?? new List<Rule64ReviewRow>();

            summary.IsPreviewOnly = failRows.Count > BrowserPreviewRowLimit || passRows.Count > BrowserPreviewRowLimit;
            summary.FailRows = failRows.Take(BrowserPreviewRowLimit).ToList();
            summary.PassRows = passRows.Take(BrowserPreviewRowLimit).ToList();
            summary.PreviewLimit = summary.IsPreviewOnly ? BrowserPreviewRowLimit : 0;
        }

        private static List<Rule64ExceptionCategoryViewModel> BuildExceptionCategories(
            IReadOnlyCollection<Rule64ReviewRow> passRows,
            IReadOnlyCollection<Rule64ReviewRow> failRows)
        {
            return passRows
                .Concat(failRows)
                .GroupBy(row => ResolveExceptionCategory(row), StringComparer.OrdinalIgnoreCase)
                .Select(group => new Rule64ExceptionCategoryViewModel
                {
                    Category = group.Key,
                    Description = GetExceptionCategoryDescription(group.Key),
                    Count = group.Count()
                })
                .OrderByDescending(category => category.Count)
                .ThenBy(category => category.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<Rule64ReviewRow> DeduplicateRows(IEnumerable<Rule64ReviewRow> rows)
        {
            var deduplicated = new List<Rule64ReviewRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows ?? Enumerable.Empty<Rule64ReviewRow>())
            {
                row.ExceptionCategory = ResolveExceptionCategory(row);
                var key = string.Join("|", new[]
                {
                    row.SourceTable?.Trim() ?? "",
                    row.StudentNo?.Trim() ?? "",
                    row.CregStudentNo?.Trim() ?? "",
                    row.ProdStudentNo?.Trim() ?? "",
                    row.StudCompareValue?.Trim() ?? "",
                    row.CregCompareValue?.Trim() ?? "",
                    row.ValidationResult?.Trim() ?? "",
                    row.ErrorCode?.Trim() ?? ""
                });

                if (!seen.Add(key))
                    continue;

                deduplicated.Add(row);
            }

            return deduplicated;
        }

        private static void AssignRowNumbers(List<Rule64ReviewRow> rows)
        {
            for (var index = 0; index < rows.Count; index++)
                rows[index].RowNumber = index + 1;
        }

        private static string ResolveExceptionCategory(Rule64ReviewRow row)
        {
            if (!string.IsNullOrWhiteSpace(row.ExceptionCategory))
            {
                var existing = row.ExceptionCategory.Trim().ToUpperInvariant();
                if (existing is "PASS_FOUND_IN_CREG"
                    or "PASS_FOUND_IN_CREG__VALUE_MATCH"
                    or "VALUE_MISMATCH__FOUND_IN_CREG"
                    or "NOT_FOUND_IN_CREG__NOT_IN_PRODUCTION"
                    or "NOT_FOUND_IN_CREG__FOUND_IN_PRODUCTION")
                    return existing;
            }

            if (string.Equals(row.ValidationResult, "PASS", StringComparison.OrdinalIgnoreCase))
                return "PASS_FOUND_IN_CREG__VALUE_MATCH";

            if (!string.IsNullOrWhiteSpace(row.CregStudentNo))
                return "VALUE_MISMATCH__FOUND_IN_CREG";

            return string.IsNullOrWhiteSpace(row.ProdStudentNo)
                ? "NOT_FOUND_IN_CREG__NOT_IN_PRODUCTION"
                : "NOT_FOUND_IN_CREG__FOUND_IN_PRODUCTION";
        }

        private static string GetExceptionCategoryDescription(string category) =>
            category.ToUpperInvariant() switch
            {
                "PASS_FOUND_IN_CREG" => "Student No found in CREG",
                "PASS_FOUND_IN_CREG__VALUE_MATCH" => "Student No found in CREG and the compare values match",
                "VALUE_MISMATCH__FOUND_IN_CREG" => "Student No found in CREG but the STUD and CREG compare values differ",
                "NOT_FOUND_IN_CREG__NOT_IN_PRODUCTION" => "Student No not found in CREG and not found in STUD PRODUCTION",
                "NOT_FOUND_IN_CREG__FOUND_IN_PRODUCTION" => "Student No not found in CREG but found in STUD PRODUCTION",
                _ => category
            };

        private static Rule64ColumnMapping NormalizeMapping(Rule64ColumnMapping? m)
        {
            m ??= new Rule64ColumnMapping();
            return new Rule64ColumnMapping
            {
                StudStudentNoCol    = ColOrDef(m.StudStudentNoCol, "_007"),
                CregStudentNoCol    = ColOrDef(m.CregStudentNoCol, "_007"),
                StudCompareValueCol = ColOrDef(m.StudCompareValueCol, "_001"),
                CregCompareValueCol = ColOrDef(m.CregCompareValueCol, "_001"),
                ProdStudentNoCol    = ColOrDef(m.ProdStudentNoCol, "IAGSTNO")
            };
        }

        private static string ColOrDef(string? v, string def) => string.IsNullOrWhiteSpace(v) ? def : v.Trim();

        private static string Sanitise(string name) => name.Replace("\"", "").Replace("'", "").Replace(";", "").Trim();

        private static string? GetString(System.Data.Common.DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
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
            var actual  = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
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

        private static string? FindFirstContainsAll(IEnumerable<string> values, params string[] fragments) =>
            values.FirstOrDefault(v => fragments.All(f => v.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0));

        private static Rule64ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule64ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
            catch { return null; }
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager",     StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director",    StringComparison.OrdinalIgnoreCase);

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
