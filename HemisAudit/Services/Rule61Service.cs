using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 61: Masters/Doctoral Research Time Validation — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection.
    //   Flow    : STUD.<status> filter -> STUD.<qual code> -> QUAL.<qual code> -> filter QUAL.<type> IN (PG types)
    //             -> QUAL.<name> -> PQM.<name>
    //   Compare : STUD.<research time> (actual) vs PQM.<research time> (expected)
    public class Rule61Service : IRule61Service
    {
        private const int RowLimit = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule61Service(
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

        public async Task<Rule61TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule61TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule61TableDiscoveryResult
                {
                    Success       = true,
                    Tables        = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "STUD"], ["stud"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "QUAL"], ["qual"]),
                    AutoPqmTable  = FindFirst(tables, ["PQM"], ["pqm"])
                };
            }
            catch (Exception ex) { return new Rule61TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule61ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "stud_student_no"     => FindFirst(cols, ["_007"], []),
                    "stud_status"         => FindFirst(cols, ["_010"], []),
                    "stud_qual_code"      => FindFirst(cols, ["_001"], []),
                    "stud_id"             => FindFirst(cols, ["_008"], []),
                    "stud_research_time"  => FindFirst(cols, ["_073"], []),
                    "qual_qual_code"      => FindFirst(cols, ["_001"], []),
                    "qual_name"           => FindFirst(cols, ["_003"], []),
                    "qual_type"           => FindFirst(cols, ["_005"], []),
                    "pqm_name"            => FindFirst(cols, ["Authorised_Qualification_Name"], ["qualification_name", "qual_name"]),
                    "pqm_research_time"   => FindFirst(cols, ["Research_1"], ["research"]),
                    _ => null
                };
                return new Rule61ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule61ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule61VerifyResult> VerifyDataAsync(Rule61ValidationRequest request)
        {
            try
            {
                var m = NormalizeMapping(request.ColumnMapping);
                var studStatusValue = NormalizeFilterValue(request.StudStatusValue, "N");
                var pgList = ParsePgTypes(request.PgTypesText);
                var studStatusValues = ParseFilterValues(studStatusValue, "N");

                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, [m.StudStudentNoCol, m.StudStatusCol, m.StudQualCodeCol]);
                await ValidateColumnsExistAsync(request.ClientId, request.QualTable, [m.QualQualCodeCol, m.QualTypeCol]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var st = Sanitise(request.StudTable);
                var qt = Sanitise(request.QualTable);
                var pt = Sanitise(request.PqmTable);
                var pgSql = BuildInList(pgList);
                var studStatusSql = BuildInList(studStatusValues);

                var countSql = $@"
SELECT COUNT(*) FROM ""{schema}"".""{st}"" S
INNER JOIN ""{schema}"".""{qt}"" Q ON TRIM(CAST(S.""{m.StudQualCodeCol}"" AS text)) = TRIM(CAST(Q.""{m.QualQualCodeCol}"" AS text))
WHERE UPPER(TRIM(CAST(S.""{m.StudStatusCol}"" AS text))) IN ({studStatusSql})
  AND {(string.IsNullOrWhiteSpace(pgSql) ? "1=0" : $"TRIM(CAST(Q.\"{m.QualTypeCol}\" AS text)) IN ({pgSql})")};";

                var mastersDoctCount = await CountAsync(connection, countSql);
                var pqmCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{pt}\";");

                return new Rule61VerifyResult { Success = true, MastersDoctCount = mastersDoctCount, PqmCount = pqmCount };
            }
            catch (Exception ex) { return new Rule61VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule61ValidationSummary> RunValidationAsync(Rule61ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                request.ColumnMapping = NormalizeMapping(request.ColumnMapping);
                request.StudStatusValue = NormalizeFilterValue(request.StudStatusValue, "N");

                var summary = await AnalyseAsync(request, RowLimit);

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
            catch (Exception ex) { return new Rule61ValidationSummary { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule61ValidationSummary> GetExportSummaryAsync(Rule61ValidationRequest request)
            => await AnalyseAsync(request, rowLimit: null);

        public async Task<int> GetPopulationCountAsync(Rule61ValidationRequest req)
        {
            var m = NormalizeMapping(req.ColumnMapping);
            var pgList = ParsePgTypes(req.PgTypesText);
            if (pgList.Count == 0) return 0;

            await ValidateColumnsExistAsync(req.ClientId, req.StudTable, [m.StudStudentNoCol, m.StudStatusCol, m.StudQualCodeCol, m.StudIdCol, m.StudResearchTimeCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.QualTable, [m.QualQualCodeCol, m.QualNameCol, m.QualTypeCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.PqmTable, [m.PqmNameCol, m.PqmResearchTimeCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var pgSql = BuildInList(pgList);
            var (bodySql, _) = BuildValidationSqlParts(schema, req.StudTable, req.QualTable, req.PqmTable, m, pgSql, req.StudStatusValue);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"WITH validation AS ({bodySql}) SELECT COUNT(*) FROM validation;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private async Task<Rule61ValidationSummary> AnalyseAsync(Rule61ValidationRequest req, int? rowLimit)
        {
            var m = NormalizeMapping(req.ColumnMapping);
            var pgList = ParsePgTypes(req.PgTypesText);
            if (pgList.Count == 0)
                return new Rule61ValidationSummary { Success = false, Error = "No valid PG type codes were specified." };

            await ValidateColumnsExistAsync(req.ClientId, req.StudTable, [m.StudStudentNoCol, m.StudStatusCol, m.StudQualCodeCol, m.StudIdCol, m.StudResearchTimeCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.QualTable, [m.QualQualCodeCol, m.QualNameCol, m.QualTypeCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.PqmTable, [m.PqmNameCol, m.PqmResearchTimeCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var pgSql = BuildInList(pgList);
            var (bodySql, orderSql) = BuildValidationSqlParts(schema, req.StudTable, req.QualTable, req.PqmTable, m, pgSql, req.StudStatusValue);

            var countSql = $@"
WITH validation AS ({bodySql})
SELECT
    COUNT(*) AS total,
    COUNT(*) FILTER (WHERE validation_result = 'PASS') AS pass_count,
    COUNT(*) FILTER (WHERE validation_result = 'MISSING_PQM') AS missing_count
FROM validation;";

            int total, passed, missing;
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = countSql;
                await using var reader = await countCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    total   = Convert.ToInt32(reader.GetValue(0));
                    passed  = Convert.ToInt32(reader.GetValue(1));
                    missing = Convert.ToInt32(reader.GetValue(2));
                }
                else { total = passed = missing = 0; }
            }

            var rowsSql = $@"
WITH validation AS ({bodySql})
SELECT * FROM validation
{orderSql}
{(rowLimit.HasValue ? "LIMIT @limit" : "")};";

            var passRows = new List<Rule61ReviewRow>();
            var failRows = new List<Rule61ReviewRow>();
            int rowNo = 0;

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = rowsSql;
                if (rowLimit.HasValue) cmd.Parameters.AddWithValue("limit", rowLimit.Value);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rowNo++;
                    var result = (GetString(reader, "validation_result") ?? "").Trim().ToUpperInvariant();
                    var row = new Rule61ReviewRow
                    {
                        RowNumber             = rowNo,
                        StudentNo             = GetString(reader, "student_no") ?? "",
                        QualCode              = GetString(reader, "qual_code") ?? "",
                        StudentId             = GetString(reader, "student_id") ?? "",
                        StudResearchTime      = GetString(reader, "stud_research_time") ?? "",
                        StudStatus            = GetString(reader, "stud_status") ?? "",
                        QualJoinCode          = GetString(reader, "qual_join_code") ?? "",
                        QualType              = GetString(reader, "qual_type") ?? "",
                        QualName              = GetString(reader, "qual_name") ?? "",
                        PqmName               = GetString(reader, "pqm_name") ?? "",
                        PqmResearchTime       = GetString(reader, "pqm_research_time") ?? "",
                        ValidationResult      = result,
                        ValidationExplanation = GetString(reader, "validation_explanation") ?? ""
                    };

                    if (result == "PASS") passRows.Add(row); else failRows.Add(row);
                }
            }

            var failCount = total - passed - missing;
            var excRate = total == 0 ? 0m : Math.Round((decimal)(total - passed) / total * 100m, 2);

            return new Rule61ValidationSummary
            {
                Success          = true,
                Timestamp        = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable        = req.StudTable,
                QualTable        = req.QualTable,
                PqmTable         = req.PqmTable,
                StudStatusValue  = req.StudStatusValue,
                PgTypesText      = req.PgTypesText,
                ColumnMapping    = m,
                ClientId         = req.ClientId,
                TotalCount       = total,
                PassCount        = passed,
                FailCount        = Math.Max(failCount, 0),
                MissingPqmCount  = missing,
                ExceptionRate    = excRate,
                Status           = (total - passed == 0) ? "PASS" : "FAIL",
                PassRows         = passRows,
                FailRows         = failRows,
                Warning = total > rowNo
                    ? $"{total:N0} rows were found; only the first {rowNo:N0} are stored and shown to keep the app responsive. All totals above are exact."
                    : null
            };
        }

        // ─── SQL Builders ────────────────────────────────────────────────────────

        private static (string BodySql, string OrderSql) BuildValidationSqlParts(
            string schema, string studTable, string qualTable, string pqmTable, Rule61ColumnMapping m, string pgSql, string studStatusValue)
        {
            var st = Sanitise(studTable);
            var qt = Sanitise(qualTable);
            var pt = Sanitise(pqmTable);
            var studStatusList = ParseFilterValues(studStatusValue, "N");
            var studStatusSql  = BuildInList(studStatusList);

            var body = $@"
    SELECT
        F.student_no, F.qual_code, F.student_id, F.stud_research_time, F.stud_status,
        F.qual_join_code, F.qual_type, F.qual_name,
        COALESCE(TRIM(CAST(P.""{m.PqmNameCol}"" AS text)), '') AS pqm_name,
        COALESCE(TRIM(CAST(P.""{m.PqmResearchTimeCol}"" AS text)), '') AS pqm_research_time,
        CASE
            WHEN P.""{m.PqmNameCol}"" IS NULL
                THEN 'No PQM record found for QUAL.{m.QualNameCol}: ' || COALESCE(F.qual_name, '')
            WHEN F.stud_research_time = TRIM(CAST(P.""{m.PqmResearchTimeCol}"" AS text))
                THEN 'PASS: QUAL.{m.QualNameCol} (' || F.qual_name || ') matches PQM.{m.PqmNameCol} (' || COALESCE(TRIM(CAST(P.""{m.PqmNameCol}"" AS text)), '') || ') and STUD.{m.StudResearchTimeCol} (' || F.stud_research_time || ') agrees with PQM.{m.PqmResearchTimeCol} (' || COALESCE(TRIM(CAST(P.""{m.PqmResearchTimeCol}"" AS text)), '') || ')'
            ELSE 'FAIL: QUAL.{m.QualNameCol} (' || F.qual_name || ') matches PQM.{m.PqmNameCol} (' || COALESCE(TRIM(CAST(P.""{m.PqmNameCol}"" AS text)), '') || ') but STUD.{m.StudResearchTimeCol} (' || F.stud_research_time || ') disagrees with PQM.{m.PqmResearchTimeCol} (' || COALESCE(TRIM(CAST(P.""{m.PqmResearchTimeCol}"" AS text)), '') || ')'
        END AS validation_explanation,
        CASE
            WHEN P.""{m.PqmNameCol}"" IS NULL THEN 'MISSING_PQM'
            WHEN F.stud_research_time = TRIM(CAST(P.""{m.PqmResearchTimeCol}"" AS text)) THEN 'PASS'
            ELSE 'FAIL'
        END AS validation_result
    FROM (
        SELECT
            SQ.student_no, SQ.qual_code, SQ.student_id, SQ.stud_research_time, SQ.stud_status,
            TRIM(CAST(Q.""{m.QualQualCodeCol}"" AS text)) AS qual_join_code,
            TRIM(CAST(Q.""{m.QualNameCol}"" AS text)) AS qual_name,
            TRIM(CAST(Q.""{m.QualTypeCol}"" AS text)) AS qual_type
        FROM (
            SELECT
                TRIM(CAST(S.""{m.StudStudentNoCol}"" AS text)) AS student_no,
                TRIM(CAST(S.""{m.StudQualCodeCol}"" AS text)) AS qual_code,
                TRIM(CAST(S.""{m.StudIdCol}"" AS text)) AS student_id,
                TRIM(CAST(S.""{m.StudResearchTimeCol}"" AS text)) AS stud_research_time,
                TRIM(CAST(S.""{m.StudStatusCol}"" AS text)) AS stud_status
            FROM ""{schema}"".""{st}"" S
            WHERE UPPER(TRIM(CAST(S.""{m.StudStatusCol}"" AS text))) IN ({studStatusSql})
        ) SQ
        INNER JOIN ""{schema}"".""{qt}"" Q
            ON SQ.qual_code = TRIM(CAST(Q.""{m.QualQualCodeCol}"" AS text))
    ) F
    LEFT JOIN ""{schema}"".""{pt}"" P
        ON UPPER(F.qual_name) = UPPER(TRIM(CAST(P.""{m.PqmNameCol}"" AS text)))
    WHERE {(string.IsNullOrWhiteSpace(pgSql) ? "1=0" : $"F.qual_type IN ({pgSql})")}";

            // student_no is the CTE's own output column name — this ORDER BY is always applied
            // against "SELECT * FROM validation", outside the CTE, where the F alias used to
            // build it is no longer in scope. Qualifying it as F.student_no here previously
            // produced Postgres error 42P01 ("missing FROM-clause entry for table 'f'").
            var order = $@"
ORDER BY
    CASE WHEN validation_result = 'MISSING_PQM' THEN 0
         WHEN validation_result = 'FAIL' THEN 1
         ELSE 2 END,
    student_no";

            return (body, order);
        }

        // ─── SQL generation ────────────────────────────────────────────────────

        public string GenerateSql(Rule61ValidationRequest request)
        {
            var m = NormalizeMapping(request.ColumnMapping);
            var pgList = ParsePgTypes(request.PgTypesText);
            var pgSql  = BuildInList(pgList);
            var studStatusList = ParseFilterValues(request.StudStatusValue, "N");
            var studStatusSql  = BuildInList(studStatusList);
            var (bodySql, orderSql) = BuildValidationSqlParts("{schema}", request.StudTable, request.QualTable, request.PqmTable, m, pgSql, request.StudStatusValue);

            return $@"-- ============================================================
-- HEMIS RULE 61 – Masters / Doctoral Research Time Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Tables    : {Sanitise(request.StudTable)} (STUD)  |  {Sanitise(request.QualTable)} (QUAL)  |  {Sanitise(request.PqmTable)} (PQM)
-- Flow      : STUD.{m.StudStatusCol} IN ({studStatusSql})
--           : STUD.{m.StudQualCodeCol} -> QUAL.{m.QualQualCodeCol} -> filter QUAL.{m.QualTypeCol} IN ({string.Join(", ", pgList)})
--           : QUAL.{m.QualNameCol} -> PQM.{m.PqmNameCol}
-- Compare   : QUAL.{m.QualNameCol} vs PQM.{m.PqmNameCol}
--           : STUD.{m.StudResearchTimeCol} vs PQM.{m.PqmResearchTimeCol}
-- ============================================================
WITH validation AS ({bodySql})
SELECT * FROM validation
{orderSql};".Trim();
        }

        // ─── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule61ValidationRequest request, Rule61ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 61);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 61,
                RuleName = "Masters/Doctoral Research Time Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalCount,
                PassCount = summary.PassCount,
                FailCount = summary.TotalCount - summary.PassCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.QualTable,
                StudColumn = request.PqmTable,
                DeceasedColumn = request.PgTypesText,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.FailRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule61ValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 61);
            if (row == null) return null;
            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        public async Task<Rule61WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 61);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);

            var workspace = new Rule61WorkspaceStateViewModel
            {
                ClientId      = row.ClientId,
                RunId         = row.RunId,
                StudTable     = summary?.StudTable ?? (string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable),
                QualTable     = summary?.QualTable ?? (string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_QUAL" : row.DeceasedTable),
                PqmTable      = summary?.PqmTable ?? (string.IsNullOrWhiteSpace(row.StudColumn) ? "PQM" : row.StudColumn),
                PgTypesText   = summary?.PgTypesText ?? (string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "07, 27, 28, 49, 72, 73, 08, 30, 50, 74, 75" : row.DeceasedColumn),
                ColumnMapping = summary?.ColumnMapping ?? new Rule61ColumnMapping(),
                StudStatusValue = summary?.StudStatusValue ?? "N",
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

        public async Task<Rule61RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 61);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary.SavedRunId = runId;

            var viewModel = new Rule61RunReviewViewModel
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
            viewModel.GeneratedSql = GenerateSql(new Rule61ValidationRequest
            {
                ClientId = viewModel.ClientId,
                StudTable = summary.StudTable,
                QualTable = summary.QualTable,
                PqmTable = summary.PqmTable,
                StudStatusValue = summary.StudStatusValue,
                PgTypesText = summary.PgTypesText,
                ColumnMapping = summary.ColumnMapping
            });

            return viewModel;
        }

        public async Task<Rule61WorkspaceSaveResult> SaveWorkspaceAsync(Rule61ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule61WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule61WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                request.ColumnMapping = NormalizeMapping(request.ColumnMapping);
                request.StudStatusValue = NormalizeFilterValue(request.StudStatusValue, "N");

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.QualTable,
                    StudColumn = request.PqmTable,
                    DeceasedColumn = request.PgTypesText
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule61WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule61WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule61WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule61WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule61WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule61WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        private static Rule61ColumnMapping NormalizeMapping(Rule61ColumnMapping? m)
        {
            m ??= new Rule61ColumnMapping();
            return new Rule61ColumnMapping
            {
                StudStudentNoCol    = ColOrDef(m.StudStudentNoCol,    "_007"),
                StudStatusCol       = ColOrDef(m.StudStatusCol,       "_010"),
                StudQualCodeCol     = ColOrDef(m.StudQualCodeCol,     "_001"),
                StudIdCol           = ColOrDef(m.StudIdCol,           "_008"),
                StudResearchTimeCol = ColOrDef(m.StudResearchTimeCol, "_073"),
                QualQualCodeCol     = ColOrDef(m.QualQualCodeCol,     "_001"),
                QualNameCol         = ColOrDef(m.QualNameCol,         "_003"),
                QualTypeCol         = ColOrDef(m.QualTypeCol,         "_005"),
                PqmNameCol          = ColOrDef(m.PqmNameCol,          "Authorised_Qualification_Name"),
                PqmResearchTimeCol  = ColOrDef(m.PqmResearchTimeCol,  "Research_1")
            };
        }

        private static string ColOrDef(string? v, string def) => string.IsNullOrWhiteSpace(v) ? def : v.Trim();

        private static string NormalizeFilterValue(string? value, string defaultValue) =>
            string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();

        private static List<string> ParseFilterValues(string? text, string defaultValue)
        {
            var normalized = NormalizeFilterValue(text, defaultValue);
            return normalized.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries)
                             .Select(v => v.Trim().ToUpperInvariant())
                             .Where(v => v.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToList();
        }

        private static List<string> ParsePgTypes(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                       .Select(t => t.Trim())
                       .Where(t => t.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        private static string BuildInList(IEnumerable<string> values)
            => string.Join(", ", values.Select(v => $"'{v.Replace("'", "''")}'"));

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

        private static Rule61ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule61ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
