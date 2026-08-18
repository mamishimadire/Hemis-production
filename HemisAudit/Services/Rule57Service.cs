using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 57: Registration Documentation Agreement — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. Selects CREG rows where a
    // configurable registration-type column equals a configurable filter value, LEFT JOINs to
    // STUD on student ID, and checks that STUD's registration-type column agrees with CREG's.
    // Agrees to the student's signed application and/or registration documentation.
    public class Rule57Service : IRule57Service
    {
        private const int BrowserPreviewRowLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule57Service(
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

        public async Task<Rule57TableListResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule57TableListResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule57TableListResult
                {
                    Success       = true,
                    Tables        = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "STUD"], ["stud"]),
                    AutoCregTable = FindFirst(tables, ["dbo_CREG", "CREG"], ["creg"])
                };
            }
            catch (Exception ex) { return new Rule57TableListResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule57ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "stud_id"      => FindFirst(cols, ["_007"], []),
                    "stud_code"    => FindFirst(cols, ["_001"], []),
                    "stud_regtype" => FindFirst(cols, ["_024"], []),
                    "creg_id"      => FindFirst(cols, ["_007"], []),
                    "creg_code"    => FindFirst(cols, ["_001"], []),
                    "creg_regtype" => FindFirst(cols, ["_064"], []),
                    _ => null
                };
                return new Rule57ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule57ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule57VerifyResult> VerifyDataAsync(Rule57ValidationRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.CregTable, [request.CregRegTypeCol]);
                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, []);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var st = Sanitise(request.StudTable);
                var ct = Sanitise(request.CregTable);
                var fval = NormalizeFilterValue(request.CregRegTypeFilterValue);

                var studTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{st}\";");
                var cregTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{ct}\";");
                var filteredTotal = await CountAsync(connection, $@"
SELECT COUNT(*) FROM ""{schema}"".""{ct}""
WHERE UPPER(TRIM(CAST(""{request.CregRegTypeCol}"" AS text))) = '{fval}';");

                return new Rule57VerifyResult
                {
                    Success       = true,
                    StudTotal     = studTotal,
                    CregTotal     = cregTotal,
                    FilteredTotal = filteredTotal,
                    FilterColumn  = request.CregRegTypeCol,
                    FilterValue   = fval
                };
            }
            catch (Exception ex) { return new Rule57VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule57ValidationSummary> RunValidationAsync(Rule57ValidationRequest request, string? userEmail = null, string? userName = null)
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

                LimitBrowserRows(summary);
                return summary;
            }
            catch (Exception ex) { return new Rule57ValidationSummary { Success = false, Error = ex.Message }; }
        }

        private async Task<Rule57ValidationSummary> AnalyseAsync(Rule57ValidationRequest req)
        {
            await ValidateColumnsExistAsync(req.ClientId, req.StudTable, [req.StudIdCol, req.StudCodeCol, req.StudRegTypeCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.CregTable, [req.CregIdCol, req.CregCodeCol, req.CregRegTypeCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var st = Sanitise(req.StudTable);
            var ct = Sanitise(req.CregTable);
            var studRecordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{st}\";");
            var cregRecordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{ct}\";");

            var fval = NormalizeFilterValue(req.CregRegTypeFilterValue);
            var bodySql = BuildValidationSql(schema, req, fval);

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

            var rows = new List<Rule57ValidationRow>();
            int rowNo = 0;

            await using (var cmd = connection.CreateCommand())
            {
                // Store every row so Excel/CSV exports remain complete; browser responses are trimmed after saving.
                cmd.CommandText = bodySql;
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rowNo++;
                    var studCode = GetString(reader, "stud_code");
                    var validationResult = GetString(reader, "validation_result") ?? "FAIL";

                    rows.Add(new Rule57ValidationRow
                    {
                        ValidationNumber = rowNo,
                        StudentId        = GetString(reader, "student_id") ?? "",
                        StudCode         = studCode ?? "",
                        StudRegType      = GetString(reader, "stud_regtype") ?? "",
                        CregCode         = GetString(reader, "creg_code"),
                        CregRegType      = GetString(reader, "creg_regtype"),
                        ValidationResult = validationResult,
                        ExceptionReason  = GetString(reader, "exception_reason")
                    });
                }
            }

            var total     = exactTotal;
            var passCount = exactPass;
            var failCount = exactFail;
            var rate      = total > 0 ? Math.Round((decimal)failCount / total * 100, 2) : 0;

            var exceptions = rows
                .Where(r => r.ValidationResult == "FAIL")
                .Select(r => new Rule57ExceptionRecord
                {
                    ValidationNumber = r.ValidationNumber,
                    StudentId        = r.StudentId,
                    StudCode         = r.StudCode,
                    StudRegType      = r.StudRegType,
                    CregCode         = r.CregCode,
                    CregRegType      = r.CregRegType,
                    ValidationResult = r.ValidationResult,
                    ExceptionReason  = r.ExceptionReason ?? ""
                })
                .ToList();

            return new Rule57ValidationSummary
            {
                Success                = true,
                StudRecordCount        = studRecordCount,
                CregRecordCount        = cregRecordCount,
                TotalValidated         = total,
                PassCount              = passCount,
                FailCount              = failCount,
                ExceptionRate          = rate,
                Status                 = failCount == 0 ? "PASS" : "FAIL",
                Timestamp              = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable              = req.StudTable,
                CregTable              = req.CregTable,
                StudIdCol              = req.StudIdCol,
                StudCodeCol            = req.StudCodeCol,
                StudRegTypeCol         = req.StudRegTypeCol,
                CregIdCol              = req.CregIdCol,
                CregCodeCol            = req.CregCodeCol,
                CregRegTypeCol         = req.CregRegTypeCol,
                CregRegTypeFilterValue = fval,
                ClientId               = req.ClientId,
                ValidationRows         = rows,
                Exceptions             = exceptions,
                Warning = null
            };
        }

        private static string BuildValidationSql(string schema, Rule57ValidationRequest req, string fval)
        {
            var st = Sanitise(req.StudTable);
            var ct = Sanitise(req.CregTable);

            return $@"
    SELECT
        TRIM(CAST(c.""{req.CregIdCol}"" AS text)) AS student_id,
        TRIM(CAST(c.""{req.CregCodeCol}"" AS text)) AS creg_code,
        TRIM(CAST(c.""{req.CregRegTypeCol}"" AS text)) AS creg_regtype,
        TRIM(CAST(s.""{req.StudCodeCol}"" AS text)) AS stud_code,
        TRIM(CAST(s.""{req.StudRegTypeCol}"" AS text)) AS stud_regtype,
        CASE
            WHEN s.""{req.StudIdCol}"" IS NULL THEN 'FAIL'
            WHEN UPPER(TRIM(CAST(s.""{req.StudRegTypeCol}"" AS text))) <> UPPER(TRIM(CAST(c.""{req.CregRegTypeCol}"" AS text))) THEN 'FAIL'
            ELSE 'PASS'
        END AS validation_result,
        CASE
            WHEN s.""{req.StudIdCol}"" IS NULL THEN 'Student not found in ' || '{st}'
            WHEN UPPER(TRIM(CAST(s.""{req.StudRegTypeCol}"" AS text))) <> UPPER(TRIM(CAST(c.""{req.CregRegTypeCol}"" AS text)))
                THEN 'Registration type mismatch - {req.StudTable}.{req.StudRegTypeCol}: ''' || COALESCE(TRIM(CAST(s.""{req.StudRegTypeCol}"" AS text)), '') || ''', {req.CregTable}.{req.CregRegTypeCol}: ''' || COALESCE(TRIM(CAST(c.""{req.CregRegTypeCol}"" AS text)), '') || ''''
            ELSE NULL
        END AS exception_reason
    FROM ""{schema}"".""{ct}"" c
    LEFT JOIN ""{schema}"".""{st}"" s
        ON UPPER(TRIM(CAST(s.""{req.StudIdCol}"" AS text))) = UPPER(TRIM(CAST(c.""{req.CregIdCol}"" AS text)))
    WHERE UPPER(TRIM(CAST(c.""{req.CregRegTypeCol}"" AS text))) = '{fval}'";
        }

        // ── SQL generation ────────────────────────────────────────────────────

        public string GenerateSql(Rule57ValidationRequest req)
        {
            var fval = NormalizeFilterValue(req.CregRegTypeFilterValue);
            var bodySql = BuildValidationSql("{schema}", req, fval);

            return $@"-- ============================================================
-- HEMIS RULE 57 - Registration Documentation Agreement
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Description: Agrees to the student's signed application and/or registration documentation.
-- Filter    : {Sanitise(req.CregTable)}.[{req.CregRegTypeCol}] = '{fval}'
-- JOIN      : {Sanitise(req.CregTable)}.[{req.CregIdCol}] = {Sanitise(req.StudTable)}.[{req.StudIdCol}] (LEFT JOIN)
-- PASS      : {Sanitise(req.StudTable)}.[{req.StudRegTypeCol}] = {Sanitise(req.CregTable)}.[{req.CregRegTypeCol}]
-- ============================================================
WITH validation AS ({bodySql})
SELECT * FROM validation
ORDER BY student_id;".Trim();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule57ValidationRequest request, Rule57ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 57);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 57,
                RuleName = "Registration Documentation Agreement",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.CregTable,
                StudColumn = request.StudRegTypeCol,
                DeceasedColumn = summary.CregRegTypeFilterValue,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.Exceptions)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule57WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 57);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            var restoredFilterValue = summary?.CregRegTypeFilterValue is { Length: > 0 } v ? v : (row.DeceasedColumn ?? "");

            var workspace = new Rule57WorkspaceStateViewModel
            {
                ClientId       = row.ClientId,
                RunId          = row.RunId,
                StudTable      = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable,
                CregTable      = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_CREG" : row.DeceasedTable,
                StudRegTypeCol = string.IsNullOrWhiteSpace(row.StudColumn) ? "_024" : row.StudColumn,
                CregRegTypeFilterValue = restoredFilterValue,
                CurrentStatus  = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt   = row.LastEditedAt,
                Summary        = summary
            };

            if (summary != null)
            {
                workspace.StudIdCol      = summary.StudIdCol;
                workspace.StudCodeCol    = summary.StudCodeCol;
                workspace.CregIdCol      = summary.CregIdCol;
                workspace.CregCodeCol    = summary.CregCodeCol;
                workspace.CregRegTypeCol = summary.CregRegTypeCol;
                workspace.CurrentStatus  = summary.Status;
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

            if (workspace.Summary != null)
            {
                workspace.Summary.SavedRunId = workspace.RunId;
                LimitBrowserRows(workspace.Summary);
            }
            return workspace;
        }

        public async Task<Rule57RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 57);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary.SavedRunId = runId;

            var viewModel = new Rule57RunReviewViewModel
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

        public async Task<Rule57WorkspaceSaveResult> SaveWorkspaceAsync(Rule57ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule57WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule57WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.CregTable,
                    StudColumn = request.StudRegTypeCol,
                    DeceasedColumn = NormalizeFilterValue(request.CregRegTypeFilterValue)
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule57WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule57WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule57WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule57WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule57WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule57WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        private static void LimitBrowserRows(Rule57ValidationSummary summary)
        {
            if (summary.ValidationRows.Count > BrowserPreviewRowLimit || summary.Exceptions.Count > BrowserPreviewRowLimit)
                summary.Warning = "Showing preview (first 10 rows) — see stat cards above for full counts. Download for full results.";

            summary.ValidationRows = summary.ValidationRows.Take(BrowserPreviewRowLimit).ToList();
            summary.Exceptions = summary.Exceptions.Take(BrowserPreviewRowLimit).ToList();
        }

        private static string Sanitise(string name) => name.Replace("\"", "").Replace("'", "").Replace(";", "").Trim();

        private static string NormalizeFilterValue(string? value)
        {
            var v = (value ?? "").Trim();
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

        private static Rule57ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule57ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
