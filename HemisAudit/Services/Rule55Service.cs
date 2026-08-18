using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 55: Graduate W-Code Validation — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. Per DHET §1.5, students coded 'W'
    // (certificate withheld, configurable) in STUD must be treated identically to 'F' graduates:
    // their qualification must be found in QUAL AND approved (configurable approval value), and
    // if a PQM register table is configured, the QUAL name+type must also match a PQM row.
    public class Rule55Service : IRule55Service
    {
        private const int RowLimit = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule55Service(
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

        public async Task<Rule55TableListResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule55TableListResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule55TableListResult
                {
                    Success       = true,
                    Tables        = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "STUD"], ["stud"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "QUAL"], ["qual"]),
                    AutoPqmTable  = FindFirst(tables, ["PQM"], ["pqm"])
                };
            }
            catch (Exception ex) { return new Rule55TableListResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule55ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "stud_id"        => FindFirst(cols, ["_007"], []),
                    "stud_qual_code" => FindFirst(cols, ["_001"], []),
                    "stud_fulfilled" => FindFirst(cols, ["_025"], []),
                    "qual_code"      => FindFirst(cols, ["_001"], []),
                    "qual_name"      => FindFirst(cols, ["_003"], []),
                    "qual_type"      => FindFirst(cols, ["_005"], []),
                    "qual_approval"  => FindFirst(cols, ["_004"], []),
                    "pqm_qual_name"  => FindFirst(cols, ["Authorised_Qualification_Name"], ["Qualification_Name", "Authorised"]),
                    "pqm_qual_type"  => FindFirst(cols, ["HEQF_Qual_Type"], ["HEQF", "Qual_Type"]),
                    _ => null
                };
                return new Rule55ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule55ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule55VerifyResult> VerifyDataAsync(Rule55ValidationRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, [request.StudFulfilledCol]);
                await ValidateColumnsExistAsync(request.ClientId, request.QualTable, []);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var st = Sanitise(request.StudTable);
                var qt = Sanitise(request.QualTable);
                var fval = NormalizeFilterValue(request.StudFulfilledFilterValue, "W");

                var studTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{st}\";");
                var qualTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{qt}\";");
                var filteredTotal = await CountAsync(connection, $@"
SELECT COUNT(*) FROM ""{schema}"".""{st}""
WHERE UPPER(TRIM(CAST(""{request.StudFulfilledCol}"" AS text))) = '{fval}';");

                return new Rule55VerifyResult
                {
                    Success       = true,
                    StudTotal     = studTotal,
                    QualTotal     = qualTotal,
                    FilteredTotal = filteredTotal,
                    FilterColumn  = request.StudFulfilledCol,
                    FilterValue   = fval
                };
            }
            catch (Exception ex) { return new Rule55VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule55ValidationSummary> RunValidationAsync(Rule55ValidationRequest request, string? userEmail = null, string? userName = null)
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
                        summary.Error   = $"Analysis completed but could not be saved: {ex.Message}";
                        return summary;
                    }
                }

                return summary;
            }
            catch (Exception ex) { return new Rule55ValidationSummary { Success = false, Error = ex.Message }; }
        }

        private async Task<Rule55ValidationSummary> AnalyseAsync(Rule55ValidationRequest req)
        {
            var requiredStudCols = new List<string> { req.StudIdCol, req.StudQualCodeCol, req.StudFulfilledCol };
            await ValidateColumnsExistAsync(req.ClientId, req.StudTable, requiredStudCols);
            await ValidateColumnsExistAsync(req.ClientId, req.QualTable, [req.QualCodeCol, req.QualNameCol, req.QualTypeCol, req.QualApprovalCol]);

            var hasPqm = !string.IsNullOrWhiteSpace(req.PqmTable) &&
                         !string.IsNullOrWhiteSpace(req.PqmQualNameColumn) &&
                         !string.IsNullOrWhiteSpace(req.PqmQualTypeColumn);
            if (hasPqm) await ValidateColumnsExistAsync(req.ClientId, req.PqmTable, [req.PqmQualNameColumn, req.PqmQualTypeColumn]);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var fval = NormalizeFilterValue(req.StudFulfilledFilterValue, "W");
            var approvalValue = NormalizeFilterValue(req.QualApprovalFilterValue, "A");

            var bodySql = BuildValidationSql(schema, req, fval, approvalValue, hasPqm);

            int exactTotal, exactPass, exactFail;
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = $@"
WITH validation AS ({bodySql})
SELECT
    COUNT(*) AS total,
    COUNT(*) FILTER (WHERE validation_result = 'PASS') AS pass_count,
    COUNT(*) FILTER (WHERE validation_result = 'FAIL') AS fail_count
FROM validation;";
                await using var countReader = await countCmd.ExecuteReaderAsync();
                if (await countReader.ReadAsync())
                {
                    exactTotal = Convert.ToInt32(countReader.GetValue(0));
                    exactPass  = Convert.ToInt32(countReader.GetValue(1));
                    exactFail  = Convert.ToInt32(countReader.GetValue(2));
                }
                else { exactTotal = exactPass = exactFail = 0; }
            }

            var rows = new List<Rule55ValidationRow>();
            int rowNo = 0;

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"{bodySql}\nLIMIT @limit;";
                cmd.Parameters.AddWithValue("limit", RowLimit);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rowNo++;
                    var qualName = GetString(reader, "qual_name");
                    var nameMatch = !reader.IsDBNull(reader.GetOrdinal("name_match")) && reader.GetBoolean(reader.GetOrdinal("name_match"));
                    var typeMatch = !reader.IsDBNull(reader.GetOrdinal("type_match")) && reader.GetBoolean(reader.GetOrdinal("type_match"));

                    rows.Add(new Rule55ValidationRow
                    {
                        ValidationNumber   = rowNo,
                        StudentId          = GetString(reader, "student_id") ?? "",
                        QualCode           = GetString(reader, "qual_code") ?? "",
                        FulfilledStatus    = GetString(reader, "fulfilled_status") ?? "",
                        QualName           = qualName,
                        QualType           = GetString(reader, "qual_type"),
                        QualApprovalStatus = GetString(reader, "approval_status"),
                        PqmQualName        = GetString(reader, "pqm_qual_name"),
                        PqmQualType        = GetString(reader, "pqm_qual_type"),
                        NameMatch          = nameMatch,
                        TypeMatch          = typeMatch,
                        ValidationResult   = GetString(reader, "validation_result") ?? "FAIL",
                        ExceptionReason    = GetString(reader, "exception_reason")
                    });
                }
            }

            var total     = exactTotal;
            var passCount = exactPass;
            var failCount = exactFail;
            var rate      = total > 0 ? Math.Round((decimal)failCount / total * 100, 2) : 0;

            var exceptions = rows
                .Where(r => r.ValidationResult == "FAIL")
                .Select(r => new Rule55ExceptionRecord
                {
                    ValidationNumber   = r.ValidationNumber,
                    StudentId          = r.StudentId,
                    QualCode           = r.QualCode,
                    FulfilledStatus    = r.FulfilledStatus,
                    QualName           = r.QualName,
                    QualType           = r.QualType,
                    QualApprovalStatus = r.QualApprovalStatus,
                    PqmQualName        = r.PqmQualName,
                    PqmQualType        = r.PqmQualType,
                    NameMatch          = r.NameMatch,
                    TypeMatch          = r.TypeMatch,
                    ValidationResult   = r.ValidationResult,
                    ExceptionReason    = r.ExceptionReason ?? ""
                })
                .ToList();

            return new Rule55ValidationSummary
            {
                Success          = true,
                TotalValidated   = total,
                PassCount        = passCount,
                FailCount        = failCount,
                ExceptionRate    = rate,
                Status           = failCount == 0 ? "PASS" : "FAIL",
                Timestamp        = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable        = req.StudTable,
                QualTable        = req.QualTable,
                StudIdCol        = req.StudIdCol,
                StudQualCodeCol  = req.StudQualCodeCol,
                StudFulfilledCol = req.StudFulfilledCol,
                StudFulfilledFilterValue = fval,
                QualApprovalFilterValue  = approvalValue,
                QualCodeCol      = req.QualCodeCol,
                QualNameCol      = req.QualNameCol,
                QualTypeCol      = req.QualTypeCol,
                QualApprovalCol  = req.QualApprovalCol,
                PqmTable          = req.PqmTable,
                PqmQualNameColumn = req.PqmQualNameColumn,
                PqmQualTypeColumn = req.PqmQualTypeColumn,
                ClientId         = req.ClientId,
                ValidationRows   = rows,
                Exceptions       = exceptions,
                Warning = total > rowNo
                    ? $"{total:N0} rows were found; only the first {rowNo:N0} are stored and shown to keep the app responsive. All totals above are exact."
                    : null
            };
        }

        private static string BuildValidationSql(string schema, Rule55ValidationRequest req, string fval, string approvalValue, bool hasPqm)
        {
            var st = Sanitise(req.StudTable);
            var qt = Sanitise(req.QualTable);
            var pt = hasPqm ? Sanitise(req.PqmTable) : "";
            var qualNameExpr = $@"q.""{req.QualNameCol}""";
            var qualTypeExpr = $@"q.""{req.QualTypeCol}""";

            var pqmNameMatchExists = hasPqm ? $@"
    EXISTS (
        SELECT 1 FROM ""{schema}"".""{pt}"" p
        WHERE UPPER(TRIM(CAST(p.""{req.PqmQualNameColumn}"" AS text))) = UPPER(TRIM(CAST({qualNameExpr} AS text)))
    )" : "false";

            var pqmTripleMatchExists = hasPqm ? $@"
    EXISTS (
        SELECT 1 FROM ""{schema}"".""{pt}"" p
        WHERE UPPER(TRIM(CAST(p.""{req.PqmQualNameColumn}"" AS text))) = UPPER(TRIM(CAST({qualNameExpr} AS text)))
          AND UPPER(TRIM(CAST(p.""{req.PqmQualTypeColumn}"" AS text))) = UPPER(TRIM(CAST({qualTypeExpr} AS text)))
    )" : "false";

            var pqmQualNameSub = hasPqm ? $@"
        (SELECT TRIM(CAST(p.""{req.PqmQualNameColumn}"" AS text)) FROM ""{schema}"".""{pt}"" p
         WHERE UPPER(TRIM(CAST(p.""{req.PqmQualNameColumn}"" AS text))) = UPPER(TRIM(CAST({qualNameExpr} AS text))) LIMIT 1)" : "NULL";
            var pqmQualTypeSub = hasPqm ? $@"
        (SELECT TRIM(CAST(p.""{req.PqmQualTypeColumn}"" AS text)) FROM ""{schema}"".""{pt}"" p
         WHERE UPPER(TRIM(CAST(p.""{req.PqmQualNameColumn}"" AS text))) = UPPER(TRIM(CAST({qualNameExpr} AS text))) LIMIT 1)" : "NULL";

            var body = $@"
    SELECT
        TRIM(CAST(s.""{req.StudIdCol}"" AS text)) AS student_id,
        TRIM(CAST(s.""{req.StudQualCodeCol}"" AS text)) AS qual_code,
        TRIM(CAST(s.""{req.StudFulfilledCol}"" AS text)) AS fulfilled_status,
        TRIM(CAST({qualNameExpr} AS text)) AS qual_name,
        TRIM(CAST({qualTypeExpr} AS text)) AS qual_type,
        TRIM(CAST(q.""{req.QualApprovalCol}"" AS text)) AS approval_status,
        {pqmQualNameSub} AS pqm_qual_name,
        {pqmQualTypeSub} AS pqm_qual_type,
        CASE
            WHEN q.""{req.QualCodeCol}"" IS NULL THEN false
            ELSE {(hasPqm ? pqmNameMatchExists : "true")}
        END AS name_match,
        CASE
            WHEN q.""{req.QualCodeCol}"" IS NULL THEN false
            WHEN q.""{req.QualApprovalCol}"" IS NULL OR TRIM(CAST(q.""{req.QualApprovalCol}"" AS text)) = '' THEN false
            WHEN UPPER(TRIM(CAST(q.""{req.QualApprovalCol}"" AS text))) <> '{approvalValue}' THEN false
            WHEN {(hasPqm ? "NOT (" + pqmTripleMatchExists + ")" : "false")} THEN false
            ELSE true
        END AS type_match,
        CASE
            WHEN q.""{req.QualCodeCol}"" IS NULL THEN 'FAIL'
            WHEN q.""{req.QualApprovalCol}"" IS NULL OR TRIM(CAST(q.""{req.QualApprovalCol}"" AS text)) = '' THEN 'FAIL'
            WHEN UPPER(TRIM(CAST(q.""{req.QualApprovalCol}"" AS text))) <> '{approvalValue}' THEN 'FAIL'
            WHEN {(hasPqm ? "NOT (" + pqmTripleMatchExists + ")" : "false")} THEN 'FAIL'
            ELSE 'PASS'
        END AS validation_result,
        CASE
            WHEN q.""{req.QualCodeCol}"" IS NULL THEN 'Qualification not found in ' || '{qt}'
            WHEN q.""{req.QualApprovalCol}"" IS NULL OR TRIM(CAST(q.""{req.QualApprovalCol}"" AS text)) = '' THEN 'Approval status missing ({req.QualApprovalCol} is null)'
            WHEN UPPER(TRIM(CAST(q.""{req.QualApprovalCol}"" AS text))) <> '{approvalValue}' THEN 'Qualification not approved ({req.QualApprovalCol}=''' || TRIM(CAST(q.""{req.QualApprovalCol}"" AS text)) || ''', expected ''{approvalValue}'')'
            {(hasPqm ? $@"WHEN NOT ({pqmTripleMatchExists}) THEN
                CASE WHEN {pqmNameMatchExists}
                    THEN 'Name matched PQM but HEQF type mismatch - {req.QualTypeCol}: ''' || COALESCE(TRIM(CAST({qualTypeExpr} AS text)), '') || ''''
                    ELSE 'Qualification name not found in PQM register - {req.QualNameCol}: ''' || COALESCE(TRIM(CAST({qualNameExpr} AS text)), '') || ''''
                END" : "")}
            ELSE NULL
        END AS exception_reason
    FROM ""{schema}"".""{st}"" s
    LEFT JOIN ""{schema}"".""{qt}"" q
        ON UPPER(TRIM(CAST(s.""{req.StudQualCodeCol}"" AS text))) = UPPER(TRIM(CAST(q.""{req.QualCodeCol}"" AS text)))
    WHERE UPPER(TRIM(CAST(s.""{req.StudFulfilledCol}"" AS text))) = '{fval}'";

            return body;
        }

        // ── SQL generation ────────────────────────────────────────────────────

        public string GenerateSql(Rule55ValidationRequest req)
        {
            var fval = NormalizeFilterValue(req.StudFulfilledFilterValue, "W");
            var approvalValue = NormalizeFilterValue(req.QualApprovalFilterValue, "A");
            var hasPqm = !string.IsNullOrWhiteSpace(req.PqmTable) &&
                         !string.IsNullOrWhiteSpace(req.PqmQualNameColumn) &&
                         !string.IsNullOrWhiteSpace(req.PqmQualTypeColumn);
            var bodySql = BuildValidationSql("{schema}", req, fval, approvalValue, hasPqm);

            return $@"-- ============================================================
-- HEMIS RULE 55 - Graduate W-Code Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- DHET §1.5 : Students coded '{fval}' are treated identically to 'F' graduates.
-- Filter    : {Sanitise(req.StudTable)}.[{req.StudFulfilledCol}] = '{fval}'
-- JOIN      : {Sanitise(req.StudTable)}.[{req.StudQualCodeCol}] = {Sanitise(req.QualTable)}.[{req.QualCodeCol}] (LEFT JOIN)
-- PASS      : QUAL row found AND [{req.QualApprovalCol}] = '{approvalValue}'{(hasPqm ? $" AND QUAL name+type match a row in {Sanitise(req.PqmTable)}" : "")}
-- ============================================================
WITH validation AS ({bodySql})
SELECT * FROM validation
ORDER BY student_id;".Trim();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule55ValidationRequest request, Rule55ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 55);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 55,
                RuleName = "Graduate W-Code Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.QualTable,
                StudColumn = request.StudFulfilledCol,
                DeceasedColumn = summary.StudFulfilledFilterValue,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.Exceptions)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule55WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 55);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            var restoredFilterValue = summary?.StudFulfilledFilterValue is { Length: > 0 } v ? v : (string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "W" : row.DeceasedColumn);

            var workspace = new Rule55WorkspaceStateViewModel
            {
                ClientId      = row.ClientId,
                RunId         = row.RunId,
                StudTable     = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable,
                QualTable     = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_QUAL" : row.DeceasedTable,
                StudFulfilledCol = string.IsNullOrWhiteSpace(row.StudColumn) ? "_025" : row.StudColumn,
                StudFulfilledFilterValue = restoredFilterValue,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt  = row.LastEditedAt,
                Summary       = summary
            };

            if (summary != null)
            {
                workspace.StudIdCol              = summary.StudIdCol;
                workspace.StudQualCodeCol        = summary.StudQualCodeCol;
                workspace.QualCodeCol            = summary.QualCodeCol;
                workspace.QualNameCol            = summary.QualNameCol;
                workspace.QualTypeCol            = summary.QualTypeCol;
                workspace.QualApprovalCol        = summary.QualApprovalCol;
                workspace.QualApprovalFilterValue = summary.QualApprovalFilterValue;
                workspace.PqmTable                = summary.PqmTable;
                workspace.PqmQualNameColumn       = summary.PqmQualNameColumn;
                workspace.PqmQualTypeColumn       = summary.PqmQualTypeColumn;
                workspace.CurrentStatus           = summary.Status;
            }

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

        public async Task<Rule55RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 55);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary.SavedRunId = runId;

            var viewModel = new Rule55RunReviewViewModel
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

            return viewModel;
        }

        public async Task<Rule55WorkspaceSaveResult> SaveWorkspaceAsync(Rule55ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule55WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule55WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.QualTable,
                    StudColumn = request.StudFulfilledCol,
                    DeceasedColumn = NormalizeFilterValue(request.StudFulfilledFilterValue, "W")
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule55WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule55WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule55WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule55WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule55WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule55WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        // ── Utilities ─────────────────────────────────────────────────────────

        private static string Sanitise(string name) => name.Replace("\"", "").Replace("'", "").Replace(";", "").Trim();

        private static string NormalizeFilterValue(string? value, string fallback)
        {
            var v = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            return v.Replace("'", "''").ToUpperInvariant();
        }

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

        private static Rule55ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule55ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
