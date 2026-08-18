using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 54: CRED vs QUAL vs PQM Validation — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. Joins CRED with QUAL on a record-ID
    // column, then for each combined row checks whether the qualification name (QUAL) matches a
    // PQM row's Authorised_Qualification_Name (case-insensitive, whitespace-collapsed), and
    // whether the CRED Research_1 value matches that SAME PQM row's Research_1 value. PASS only
    // when both match on the same PQM row.
    public class Rule54Service : IRule54Service
    {
        private const int RowLimit = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule54Service(
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

        public async Task<Rule54TableListResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule54TableListResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule54TableListResult
                {
                    Success       = true,
                    Tables        = tables,
                    AutoCredTable = FindFirst(tables, ["dbo_CRED", "CRED"], ["cred"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "QUAL"], ["qual"]),
                    AutoPqmTable  = FindFirst(tables, ["PQM"], ["pqm"])
                };
            }
            catch (Exception ex) { return new Rule54TableListResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule54ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "cred_id"        => FindFirst(cols, ["_001"], []),
                    "cred_course"    => FindFirst(cols, ["_030"], []),
                    "cred_credit"    => FindFirst(cols, ["_036"], []),
                    "cred_research1" => FindFirst(cols, ["_050"], []),
                    "qual_id"        => FindFirst(cols, ["_001"], []),
                    "qual_name"      => FindFirst(cols, ["_003"], []),
                    "pqm_name"       => FindFirst(cols, ["Authorised_Qualification_Name"], ["Qualification_Name", "Authorised"]),
                    "pqm_research1"  => FindFirst(cols, ["Research_1"], ["Research1", "Research"]),
                    _ => null
                };
                return new Rule54ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule54ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule54VerifyResult> VerifyDataAsync(Rule54ValidationRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.CredTable, [request.CredIdCol]);
                await ValidateColumnsExistAsync(request.ClientId, request.QualTable, [request.QualIdCol]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var ct = Sanitise(request.CredTable);
                var qt = Sanitise(request.QualTable);
                var pt = Sanitise(request.PqmTable);
                var ci = Sanitise(request.CredIdCol);
                var qi = Sanitise(request.QualIdCol);

                var credTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{ct}\";");
                var qualTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{qt}\";");
                var pqmTotal  = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{pt}\";");
                var mergedTotal = await CountAsync(connection, $@"
SELECT COUNT(*) FROM ""{schema}"".""{ct}"" c
INNER JOIN ""{schema}"".""{qt}"" q ON UPPER(TRIM(CAST(c.""{ci}"" AS text))) = UPPER(TRIM(CAST(q.""{qi}"" AS text)));");

                return new Rule54VerifyResult
                {
                    Success     = true,
                    CredTotal   = credTotal,
                    QualTotal   = qualTotal,
                    PqmTotal    = pqmTotal,
                    MergedTotal = mergedTotal
                };
            }
            catch (Exception ex) { return new Rule54VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule54ValidationSummary> RunValidationAsync(Rule54ValidationRequest request, string? userEmail = null, string? userName = null)
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
            catch (Exception ex) { return new Rule54ValidationSummary { Success = false, Error = ex.Message }; }
        }

        private async Task<Rule54ValidationSummary> AnalyseAsync(Rule54ValidationRequest req)
        {
            await ValidateColumnsExistAsync(req.ClientId, req.CredTable, [req.CredIdCol, req.CredCourseCol, req.CredCreditCol, req.CredResearch1Col]);
            await ValidateColumnsExistAsync(req.ClientId, req.QualTable, [req.QualIdCol, req.QualNameCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.PqmTable,  [req.PqmNameCol, req.PqmResearch1Col]);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var bodySql = BuildValidationSql(schema, req);

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

            var rows = new List<Rule54ValidationRow>();
            int rowNo = 0;

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"{bodySql}\nLIMIT @limit;";
                cmd.Parameters.AddWithValue("limit", RowLimit);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rowNo++;
                    var qualNameMatch  = !reader.IsDBNull(reader.GetOrdinal("qual_name_match")) && reader.GetBoolean(reader.GetOrdinal("qual_name_match"));
                    var research1Match = !reader.IsDBNull(reader.GetOrdinal("research1_match")) && reader.GetBoolean(reader.GetOrdinal("research1_match"));
                    var hemisQualName  = GetString(reader, "hemis_qual_name") ?? "";
                    var hemisResearch1 = GetString(reader, "hemis_research1") ?? "";
                    var pqmResearch1Values = GetString(reader, "pqm_research1_values") ?? "";
                    var validationResult = GetString(reader, "validation_result") ?? "FAIL";

                    string? exceptionReason = null;
                    if (!qualNameMatch)
                        exceptionReason = $"Qualification name '{hemisQualName}' not found in PQM ({req.PqmNameCol})";
                    else if (!research1Match)
                        exceptionReason = $"Name matched ('{hemisQualName}') but {req.PqmResearch1Col} mismatch - " +
                                           $"{req.CredTable}.{req.CredResearch1Col}: '{hemisResearch1}' | PQM {req.PqmResearch1Col}: '{pqmResearch1Values}'";

                    rows.Add(new Rule54ValidationRow
                    {
                        ValidationNumber = rowNo,
                        RecordId         = GetString(reader, "record_id") ?? "",
                        QualRecordId     = GetString(reader, "qual_record_id") ?? "",
                        CourseCode       = GetString(reader, "course_code") ?? "",
                        CreditValue      = GetString(reader, "credit_value") ?? "",
                        HemisResearch1   = hemisResearch1,
                        HemisQualName    = hemisQualName,
                        PqmQualName      = GetString(reader, "pqm_qual_name"),
                        PqmResearch1     = GetString(reader, "pqm_research1"),
                        QualNameMatch    = qualNameMatch,
                        Research1Match   = research1Match,
                        ValidationResult = validationResult,
                        ExceptionReason  = exceptionReason
                    });
                }
            }

            var total     = exactTotal;
            var passCount = exactPass;
            var failCount = exactFail;
            var rate      = total > 0 ? Math.Round((decimal)failCount / total * 100, 2) : 0;

            var exceptions = rows
                .Where(r => r.ValidationResult == "FAIL")
                .Select(r => new Rule54ExceptionRecord
                {
                    ValidationNumber = r.ValidationNumber,
                    RecordId         = r.RecordId,
                    QualRecordId     = r.QualRecordId,
                    CourseCode       = r.CourseCode,
                    CreditValue      = r.CreditValue,
                    HemisResearch1   = r.HemisResearch1,
                    HemisQualName    = r.HemisQualName,
                    PqmQualName      = r.PqmQualName,
                    PqmResearch1     = r.PqmResearch1,
                    QualNameMatch    = r.QualNameMatch,
                    Research1Match   = r.Research1Match,
                    ValidationResult = r.ValidationResult,
                    ExceptionReason  = r.ExceptionReason ?? ""
                })
                .ToList();

            return new Rule54ValidationSummary
            {
                Success          = true,
                TotalValidated   = total,
                PassCount        = passCount,
                FailCount        = failCount,
                ExceptionRate    = rate,
                Status           = failCount == 0 ? "PASS" : "FAIL",
                Timestamp        = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                CredTable        = req.CredTable,
                QualTable        = req.QualTable,
                PqmTable         = req.PqmTable,
                CredIdCol        = req.CredIdCol,
                CredCourseCol    = req.CredCourseCol,
                CredCreditCol    = req.CredCreditCol,
                CredResearch1Col = req.CredResearch1Col,
                QualIdCol        = req.QualIdCol,
                QualNameCol      = req.QualNameCol,
                PqmNameCol       = req.PqmNameCol,
                PqmResearch1Col  = req.PqmResearch1Col,
                ClientId         = req.ClientId,
                ValidationRows   = rows,
                Exceptions       = exceptions,
                Warning = total > rowNo
                    ? $"{total:N0} rows were found; only the first {rowNo:N0} are stored and shown to keep the app responsive. All totals above are exact."
                    : null
            };
        }

        // Whitespace-collapsing, case-insensitive name normalization matching the original
        // NormName() helper (Regex.Replace(v.Trim().ToUpperInvariant(), @"\s+", " ")).
        private static string NormNameSql(string expr) =>
            $"UPPER(REGEXP_REPLACE(TRIM(CAST({expr} AS text)), '\\s+', ' ', 'g'))";

        private static string BuildValidationSql(string schema, Rule54ValidationRequest req)
        {
            var ct = Sanitise(req.CredTable);
            var qt = Sanitise(req.QualTable);
            var pt = Sanitise(req.PqmTable);
            var qualNameExpr = $@"q.""{req.QualNameCol}""";
            var pqmNameExpr  = $@"p.""{req.PqmNameCol}""";

            return $@"
SELECT
    TRIM(CAST(c.""{req.CredIdCol}"" AS text)) AS record_id,
    TRIM(CAST(q.""{req.QualIdCol}"" AS text)) AS qual_record_id,
    TRIM(CAST(c.""{req.CredCourseCol}"" AS text)) AS course_code,
    TRIM(CAST(c.""{req.CredCreditCol}"" AS text)) AS credit_value,
    TRIM(CAST(c.""{req.CredResearch1Col}"" AS text)) AS hemis_research1,
    TRIM(CAST(q.""{req.QualNameCol}"" AS text)) AS hemis_qual_name,
    (
        SELECT TRIM(CAST(p.""{req.PqmNameCol}"" AS text))
        FROM ""{schema}"".""{pt}"" p
        WHERE {NormNameSql(pqmNameExpr)} = {NormNameSql(qualNameExpr)}
        LIMIT 1
    ) AS pqm_qual_name,
    (
        SELECT TRIM(CAST(p.""{req.PqmResearch1Col}"" AS text))
        FROM ""{schema}"".""{pt}"" p
        WHERE {NormNameSql(pqmNameExpr)} = {NormNameSql(qualNameExpr)}
        LIMIT 1
    ) AS pqm_research1,
    EXISTS (
        SELECT 1 FROM ""{schema}"".""{pt}"" p
        WHERE {NormNameSql(pqmNameExpr)} = {NormNameSql(qualNameExpr)}
    ) AS qual_name_match,
    EXISTS (
        SELECT 1 FROM ""{schema}"".""{pt}"" p
        WHERE {NormNameSql(pqmNameExpr)} = {NormNameSql(qualNameExpr)}
          AND UPPER(TRIM(CAST(p.""{req.PqmResearch1Col}"" AS text))) = UPPER(TRIM(CAST(c.""{req.CredResearch1Col}"" AS text)))
    ) AS research1_match,
    CASE WHEN EXISTS (
        SELECT 1 FROM ""{schema}"".""{pt}"" p
        WHERE {NormNameSql(pqmNameExpr)} = {NormNameSql(qualNameExpr)}
          AND UPPER(TRIM(CAST(p.""{req.PqmResearch1Col}"" AS text))) = UPPER(TRIM(CAST(c.""{req.CredResearch1Col}"" AS text)))
    ) THEN 'PASS' ELSE 'FAIL' END AS validation_result,
    (
        SELECT string_agg(v, ' | ') FROM (
            SELECT DISTINCT TRIM(CAST(p.""{req.PqmResearch1Col}"" AS text)) AS v
            FROM ""{schema}"".""{pt}"" p
            WHERE {NormNameSql(pqmNameExpr)} = {NormNameSql(qualNameExpr)}
              AND p.""{req.PqmResearch1Col}"" IS NOT NULL
            LIMIT 3
        ) t
    ) AS pqm_research1_values
FROM ""{schema}"".""{ct}"" c
INNER JOIN ""{schema}"".""{qt}"" q
    ON UPPER(TRIM(CAST(c.""{req.CredIdCol}"" AS text))) = UPPER(TRIM(CAST(q.""{req.QualIdCol}"" AS text)))
ORDER BY record_id";
        }

        // ── SQL generation ────────────────────────────────────────────────────

        public string GenerateSql(Rule54ValidationRequest request)
        {
            var bodySql = BuildValidationSql("{schema}", request);

            return $@"-- ============================================================
-- HEMIS RULE 54 - CRED vs QUAL vs PQM Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- JOIN      : ""{Sanitise(request.CredTable)}"".""{request.CredIdCol}"" = ""{Sanitise(request.QualTable)}"".""{request.QualIdCol}""
-- MATCH     : (1) {request.QualTable}.{request.QualNameCol} = PQM.{request.PqmNameCol} (case-insensitive, whitespace-collapsed)
--             (2) {request.CredTable}.{request.CredResearch1Col} = PQM.{request.PqmResearch1Col} on the SAME PQM row
-- ============================================================
{bodySql};".Trim();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule54ValidationRequest request, Rule54ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 54);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 54,
                RuleName = "CRED vs QUAL vs PQM Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.CredTable,
                DeceasedTable = request.QualTable,
                StudColumn = request.PqmTable,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.Exceptions)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule54WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 54);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);

            var workspace = new Rule54WorkspaceStateViewModel
            {
                ClientId      = row.ClientId,
                RunId         = row.RunId,
                CredTable     = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_CRED" : row.StudTable,
                QualTable     = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_QUAL" : row.DeceasedTable,
                PqmTable      = string.IsNullOrWhiteSpace(row.StudColumn) ? "PQM" : row.StudColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt  = row.LastEditedAt,
                Summary       = summary
            };

            if (summary != null)
            {
                workspace.CredIdCol        = summary.CredIdCol;
                workspace.CredCourseCol    = summary.CredCourseCol;
                workspace.CredCreditCol    = summary.CredCreditCol;
                workspace.CredResearch1Col = summary.CredResearch1Col;
                workspace.QualIdCol        = summary.QualIdCol;
                workspace.QualNameCol      = summary.QualNameCol;
                workspace.PqmNameCol       = summary.PqmNameCol;
                workspace.PqmResearch1Col  = summary.PqmResearch1Col;
                workspace.CurrentStatus    = summary.Status;
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

        public async Task<Rule54RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 54);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary.SavedRunId = runId;

            var viewModel = new Rule54RunReviewViewModel
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

        public async Task<Rule54WorkspaceSaveResult> SaveWorkspaceAsync(Rule54ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule54WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule54WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.CredTable,
                    DeceasedTable = request.QualTable,
                    StudColumn = request.PqmTable,
                    DeceasedColumn = ""
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule54WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule54WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule54WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule54WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule54WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule54WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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
            var missing = requiredColumns.Where(c => !actual.Contains(c, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
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

        private static Rule54ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule54ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
