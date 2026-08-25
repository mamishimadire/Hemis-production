using Newtonsoft.Json;
using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 46: Foundation Student Chain Validation — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. Selects foundation students
    // via a configurable STUD filter column/value, bridges STUD -> QUAL -> PQM, and confirms the
    // qualification name resolves in PQM. The original SQL-Server design had no row cap; RowLimit
    // is introduced here from the start, matching this session's house style.
    public class Rule46Service : IRule46Service
    {
        // Storage/Excel population cap — effectively "full population" (well above any realistic
        // institution's record count) while still guarding against a pathological runaway query.
        private const int RowLimit = 200000;
        // The UI (Analysis/Results tabs) only ever renders a small sample so the app stays fast;
        // the full population above is always stored and always available via Excel/CSV download.
        private const int UiSampleLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule46Service(
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

        public async Task<Rule46TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule46TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule46TableDiscoveryResult
                {
                    Success       = true,
                    Tables        = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "STUD"], ["stud"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "QUAL"], ["qual"]),
                    AutoPqmTable  = FindFirst(tables, ["PQM"], ["pqm"])
                };
            }
            catch (Exception ex) { return new Rule46TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule46ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "stud_key"    => FindFirst(cols, ["_001"], []),
                    "stud_id"     => FindFirst(cols, ["_008"], []),
                    "stud_007"    => FindFirst(cols, ["_007"], []),
                    "stud_010"    => FindFirst(cols, ["_010"], []),
                    "stud_012"    => FindFirst(cols, ["_012"], []),
                    "stud_026"    => FindFirst(cols, ["_026"], []),
                    "stud_filter" => FindFirst(cols, ["_106"], []),
                    "qual_key"    => FindFirst(cols, ["_001"], []),
                    "qual_name"   => FindFirst(cols, ["_003"], []),
                    "pqm_name"    => FindFirst(cols, ["Authorised_Qualification_Name"], ["Qualification_Name", "Authorised"]),
                    _ => null
                };
                return new Rule46ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule46ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule46VerifyResult> VerifyTablesAsync(Rule46ValidationRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, [request.StudKey, request.StudFilterCol]);
                await ValidateColumnsExistAsync(request.ClientId, request.QualTable, [request.QualKey]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                return new Rule46VerifyResult
                {
                    Success   = true,
                    StudCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.StudTable)}\";"),
                    QualCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.QualTable)}\";"),
                    PqmCount  = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.PqmTable)}\";")
                };
            }
            catch (Exception ex) { return new Rule46VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule46ValidationSummary> RunValidationAsync(Rule46ValidationRequest request, string? userEmail = null, string? userName = null)
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

                return ApplyUiSample(summary);
            }
            catch (Exception ex) { return new Rule46ValidationSummary { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule46ValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 46);
            if (row == null) return null;
            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        // Re-runs the same analysis a fresh "Run Validation" would, without the save-run side
        // effect - used by the Excel export path.
        public async Task<Rule46ValidationSummary> GetExportSummaryAsync(Rule46ValidationRequest request) =>
            await AnalyseAsync(request);

        // Cheap population size check - stops at a COUNT(*), no result rows loaded.
        public async Task<int> GetPopulationCountAsync(Rule46ValidationRequest request)
        {
            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;
            var (bodySql, _) = BuildValidationSqlParts(schema, request);

            await using var command = connection.CreateCommand();
            command.CommandText = $@"WITH validation AS ({bodySql}) SELECT COUNT(*) FROM validation;";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        // Bypasses AnalyseAsync entirely - that buffers every row into a list capped at RowLimit
        // (200,000) regardless of the real row count. Reads and writes one row at a time. Mirrors
        // Rule12Service.StreamCsvExportAsync.
        public async Task StreamCsvExportAsync(Rule46ValidationRequest request, Stream outputStream)
        {
            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;
            var (bodySql, orderSql) = BuildValidationSqlParts(schema, request);

            await using var writer = new StreamWriter(outputStream, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = false };

            await using var command = connection.CreateCommand();
            command.CommandText = $@"WITH validation AS ({bodySql}) SELECT * FROM validation {orderSql};";
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

        private async Task<Rule46ValidationSummary> AnalyseAsync(Rule46ValidationRequest req)
        {
            await ValidateColumnsExistAsync(req.ClientId, req.StudTable, [req.StudKey, req.StudIdCol, req.Stud007Col, req.Stud010Col, req.Stud012Col, req.Stud026Col, req.StudFilterCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.QualTable, [req.QualKey, req.QualNameCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.PqmTable,  [req.PqmNameCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var (bodySql, orderSql) = BuildValidationSqlParts(schema, req);

            var countSql = $@"
WITH validation AS ({bodySql})
SELECT
    COUNT(*) AS total,
    COUNT(*) FILTER (WHERE validation_result = 'PASS') AS pass_count,
    COUNT(*) FILTER (WHERE validation_result = 'FAIL') AS fail_count
FROM validation;";

            int total, passed, failed;
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = countSql;
                await using var reader = await countCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    total  = Convert.ToInt32(reader.GetValue(0));
                    passed = Convert.ToInt32(reader.GetValue(1));
                    failed = Convert.ToInt32(reader.GetValue(2));
                }
                else { total = passed = failed = 0; }
            }

            var rowsSql = $@"
WITH validation AS ({bodySql})
SELECT * FROM validation
{orderSql}
LIMIT @limit;";

            var rows = new List<Rule46ValidationRow>();
            int rowNo = 0;

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = rowsSql;
                cmd.Parameters.AddWithValue("limit", RowLimit);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rowNo++;
                    rows.Add(new Rule46ValidationRow
                    {
                        RowNumber        = rowNo,
                        ControlType      = "Combined",
                        StudId           = GetString(reader, "stud_id") ?? "",
                        StudentId        = GetString(reader, "student_id") ?? "",
                        Stud007          = GetString(reader, "stud_007") ?? "",
                        Stud010          = GetString(reader, "stud_010") ?? "",
                        Stud012          = GetString(reader, "stud_012") ?? "",
                        Stud026          = GetString(reader, "stud_026") ?? "",
                        StudFilterValue  = GetString(reader, "stud_filter_value") ?? "",
                        QualId           = GetString(reader, "qual_id") ?? "",
                        QualName         = GetString(reader, "qual_name") ?? "",
                        PqmName          = GetString(reader, "pqm_name") ?? "",
                        ValidationResult = GetString(reader, "validation_result") ?? "",
                        ResultDetail     = GetString(reader, "result_detail") ?? ""
                    });
                }
            }

            var rate = total == 0 ? 0m : Math.Round((decimal)failed / total * 100m, 2);
            var overallStatus = failed == 0 ? "PASS" : "FAIL";

            return new Rule46ValidationSummary
            {
                Success       = true,
                Status        = overallStatus,
                Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ClientId      = req.ClientId,
                StudTable     = req.StudTable,    StudKey       = req.StudKey,    StudIdCol = req.StudIdCol, Stud007Col = req.Stud007Col, Stud010Col = req.Stud010Col, Stud012Col = req.Stud012Col, Stud026Col = req.Stud026Col,
                StudFilterCol = req.StudFilterCol, StudFilterValue = req.StudFilterValue,
                QualTable     = req.QualTable,    QualKey       = req.QualKey,    QualNameCol = req.QualNameCol,
                PqmTable      = req.PqmTable,     PqmNameCol    = req.PqmNameCol,
                TotalValidated = total,
                PassCount     = passed,
                FailCount     = failed,
                ExceptionRate = rate,
                ControlSummaries = new List<Rule46ControlSummary>
                {
                    new()
                    {
                        ControlType   = "Combined",
                        ControlLabel  = $"Combined Control: {req.StudTable}._001 -> {req.QualTable}._001 -> {req.PqmTable}",
                        CriteriaText  = $"Foundation students where {req.StudTable}.{req.StudFilterCol} = '{req.StudFilterValue}' must have a matching QUAL record and the qualification name ({req.QualTable}.{req.QualNameCol}) must exist in {req.PqmTable}.{req.PqmNameCol}.",
                        TotalCount    = total,
                        PassCount     = passed,
                        FailCount     = failed,
                        ExceptionRate = rate,
                        Status        = overallStatus
                    }
                },
                ValidationRows = rows,
                Warning = total > rowNo
                    ? $"{total:N0} rows were found; only the first {rowNo:N0} are stored to keep the app responsive. All totals above are exact."
                    : null
            };
        }

        // The service always computes and stores the full tested population (up to RowLimit).
        // The UI only ever needs a small sample to stay fast — this returns a shallow copy with
        // ValidationRows truncated to UiSampleLimit; the full set remains in ResultsJSON for
        // Excel/CSV export via GetStoredSummaryAsync.
        private static Rule46ValidationSummary ApplyUiSample(Rule46ValidationSummary full)
        {
            if (full.ValidationRows.Count <= UiSampleLimit) return full;

            return new Rule46ValidationSummary
            {
                Success = full.Success, Error = full.Error, SavedRunId = full.SavedRunId, ClientId = full.ClientId,
                Status = full.Status, Timestamp = full.Timestamp,
                StudTable = full.StudTable, StudKey = full.StudKey, StudIdCol = full.StudIdCol, Stud007Col = full.Stud007Col,
                Stud010Col = full.Stud010Col, Stud012Col = full.Stud012Col, Stud026Col = full.Stud026Col,
                StudFilterCol = full.StudFilterCol, StudFilterValue = full.StudFilterValue,
                QualTable = full.QualTable, QualKey = full.QualKey, QualNameCol = full.QualNameCol,
                PqmTable = full.PqmTable, PqmNameCol = full.PqmNameCol,
                TotalValidated = full.TotalValidated, PassCount = full.PassCount, FailCount = full.FailCount,
                ExceptionRate = full.ExceptionRate, ControlSummaries = full.ControlSummaries,
                ValidationRows = full.ValidationRows.Take(UiSampleLimit).ToList(),
                Warning = "UI results show only the first 10 sample rows. Download the results to access the full tested population."
            };
        }

        private static (string BodySql, string OrderSql) BuildValidationSqlParts(string schema, Rule46ValidationRequest req)
        {
            var st = Sanitise(req.StudTable);
            var qt = Sanitise(req.QualTable);
            var pt = Sanitise(req.PqmTable);
            var sfv = (req.StudFilterValue ?? "Y").Replace("'", "''");

            var body = $@"
    SELECT
        TRIM(CAST(s.""{req.StudKey}""    AS text)) AS stud_id,
        TRIM(CAST(s.""{req.StudIdCol}""  AS text)) AS student_id,
        TRIM(CAST(s.""{req.Stud007Col}"" AS text)) AS stud_007,
        TRIM(CAST(s.""{req.Stud010Col}"" AS text)) AS stud_010,
        TRIM(CAST(s.""{req.Stud012Col}"" AS text)) AS stud_012,
        TRIM(CAST(s.""{req.Stud026Col}"" AS text)) AS stud_026,
        TRIM(CAST(s.""{req.StudFilterCol}"" AS text)) AS stud_filter_value,
        TRIM(CAST(q.""{req.QualKey}""     AS text)) AS qual_id,
        TRIM(CAST(q.""{req.QualNameCol}"" AS text)) AS qual_name,
        TRIM(CAST(p.""{req.PqmNameCol}""  AS text)) AS pqm_name,
        CASE
            WHEN q.""{req.QualKey}"" IS NULL THEN 'FAIL'
            WHEN p.""{req.PqmNameCol}"" IS NULL THEN 'FAIL'
            ELSE 'PASS'
        END AS validation_result,
        CASE
            WHEN q.""{req.QualKey}"" IS NULL
                THEN 'FAIL: No QUAL record found for STUD._001 = ' || COALESCE(TRIM(CAST(s.""{req.StudKey}"" AS text)), '')
            WHEN p.""{req.PqmNameCol}"" IS NULL
                THEN 'FAIL: QUAL.{req.QualNameCol} (' || COALESCE(TRIM(CAST(q.""{req.QualNameCol}"" AS text)), '') || ') was not found in PQM.{req.PqmNameCol}'
            ELSE 'PASS: Foundation student has a valid qualification in QUAL and PQM'
        END AS result_detail
    FROM ""{schema}"".""{st}"" s
    LEFT JOIN ""{schema}"".""{qt}"" q
        ON UPPER(TRIM(CAST(q.""{req.QualKey}"" AS text))) = UPPER(TRIM(CAST(s.""{req.StudKey}"" AS text)))
    LEFT JOIN ""{schema}"".""{pt}"" p
        ON UPPER(TRIM(CAST(p.""{req.PqmNameCol}"" AS text))) = UPPER(TRIM(CAST(q.""{req.QualNameCol}"" AS text)))
    WHERE UPPER(TRIM(CAST(s.""{req.StudFilterCol}"" AS text))) = UPPER('{sfv}')";

            var order = @"
ORDER BY
    CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END,
    stud_id";

            return (body, order);
        }

        // ── SQL generation ────────────────────────────────────────────────────

        public string GenerateValidationSql(Rule46ValidationRequest req)
        {
            var st = Sanitise(req.StudTable);
            var qt = Sanitise(req.QualTable);
            var pt = Sanitise(req.PqmTable);
            var sfv = (req.StudFilterValue ?? "Y").Replace("'", "''");
            var (bodySql, orderSql) = BuildValidationSqlParts("{schema}", req);

            return $@"-- ============================================================
-- HEMIS RULE 46 - Foundation Student Chain Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Chain     : ""{st}"".""{req.StudKey}"" -> ""{qt}"".""{req.QualKey}"" -> ""{pt}"".""{req.PqmNameCol}""
-- Foundation filter : ""{st}"".""{req.StudFilterCol}"" = '{sfv}'
-- ============================================================
WITH validation AS ({bodySql})
SELECT * FROM validation
{orderSql};".Trim();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule46ValidationRequest request, Rule46ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 46);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 46,
                RuleName = "Foundation Student Chain Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.QualTable,
                StudColumn = request.PqmTable,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.ValidationRows.Where(r => r.ValidationResult == "FAIL"))),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule46WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 46);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary = ApplyUiSample(summary);

            var workspace = new Rule46WorkspaceStateViewModel
            {
                ClientId      = row.ClientId,
                RunId         = row.RunId,
                StudTable     = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable,
                QualTable     = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_QUAL" : row.DeceasedTable,
                PqmTable      = string.IsNullOrWhiteSpace(row.StudColumn) ? "PQM" : row.StudColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt  = row.LastEditedAt,
                Summary       = summary
            };

            if (summary != null)
            {
                workspace.StudKey         = summary.StudKey;
                workspace.StudIdCol       = summary.StudIdCol;
                workspace.Stud007Col      = summary.Stud007Col;
                workspace.Stud010Col      = summary.Stud010Col;
                workspace.Stud012Col      = summary.Stud012Col;
                workspace.Stud026Col      = summary.Stud026Col;
                workspace.StudFilterCol   = summary.StudFilterCol;
                workspace.StudFilterValue = summary.StudFilterValue;
                workspace.QualKey         = summary.QualKey;
                workspace.QualNameCol     = summary.QualNameCol;
                workspace.PqmNameCol      = summary.PqmNameCol;
                workspace.CurrentStatus   = summary.Status;
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

        public async Task<Rule46RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 46);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary = ApplyUiSample(summary);
            summary.SavedRunId = runId;

            var viewModel = new Rule46RunReviewViewModel
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
            viewModel.CurrentUserHasSignedOff = viewModel.Signoffs.Any(s => ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, viewModel.CurrentUserEngagementRole));
            viewModel.CanCurrentUserSignOff = ValidationRunAccessPolicy.CanCompleteReviewSignoff(viewModel.CurrentUserEngagementRole, viewModel.CurrentUserEngagementRole, viewModel.HasDataAnalystSignoff);

            return viewModel;
        }

        public async Task<Rule46WorkspaceSaveResult> SaveWorkspaceAsync(Rule46ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule46WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule46WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.QualTable,
                    StudColumn = request.PqmTable,
                    DeceasedColumn = ""
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule46WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule46WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule46WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule46WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule46WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule46WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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
        // engagement's actual uploaded table — check before querying so a mismatch surfaces as a
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

        private static Rule46ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule46ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
