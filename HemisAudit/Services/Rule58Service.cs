using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 58: STAFF VALPAC Data in STAFF PRODUCTION — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. Every non-blank VALPAC._037
    // row (not deduplicated - one test per row) must match a PERSONEL_NUMBER in the PRODUCTION table.
    public class Rule58Service : IRule58Service
    {
        private const int BrowserPreviewRowLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule58Service(
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

        public async Task<Rule58TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule58TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule58TableDiscoveryResult
                {
                    Success         = true,
                    Tables          = tables,
                    AutoValpacTable = FindFirst(tables, ["dbo_STAFF_VALPAC", "STAFF_VALPAC"], ["staff_valpac", "staffvalpac"]),
                    AutoProdTable   = FindFirst(tables, ["dbo_STAFF_PRODUCTION", "STAFF_PRODUCTION"], ["staff_production", "staffproduction"])
                };
            }
            catch (Exception ex) { return new Rule58TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule58ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "valpac_037"   => FindFirst(cols, ["_037"], []),
                    "prod_personel" => FindFirst(cols, ["PERSONEL_NUMBER"], ["personel", "personnel"]),
                    _ => null
                };
                return new Rule58ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule58ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule58VerifyResult> VerifyDataAsync(Rule58ValidationRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.ValpacTable, [request.ValpacCol037]);
                await ValidateColumnsExistAsync(request.ClientId, request.ProdTable, [request.ProdColPersonelNumber]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var valpacCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.ValpacTable)}\";");
                var prodCount   = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.ProdTable)}\";");

                var (bodySql, _) = BuildValidationSqlParts(schema, request);
                var countSql = $@"
WITH validation AS ({bodySql})
SELECT
    COUNT(*) AS total,
    COUNT(*) FILTER (WHERE validation_result = 'PASS') AS pass_count,
    COUNT(*) FILTER (WHERE validation_result = 'FAIL') AS fail_count
FROM validation;";

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = countSql;
                await using var reader = await cmd.ExecuteReaderAsync();

                var result = new Rule58VerifyResult { Success = true, ValpacRecordCount = valpacCount, ProdRecordCount = prodCount };
                if (await reader.ReadAsync())
                {
                    result.TotalTested  = Convert.ToInt32(reader.GetValue(0));
                    result.MatchedCount = Convert.ToInt32(reader.GetValue(1));
                    result.MissingCount = Convert.ToInt32(reader.GetValue(2));
                }
                return result;
            }
            catch (Exception ex) { return new Rule58VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule58ValidationSummary> RunValidationAsync(Rule58ValidationRequest request, string? userEmail = null, string? userName = null)
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
            catch (Exception ex) { return new Rule58ValidationSummary { Success = false, Error = ex.Message }; }
        }

        // AnalyseAsync itself never truncates rows - the full population is always analysed and
        // returned here. Only RunValidationAsync's response to the browser gets trimmed afterwards.
        public async Task<Rule58ValidationSummary> GetExportSummaryAsync(Rule58ValidationRequest request)
            => await AnalyseAsync(request);

        public async Task<int> GetPopulationCountAsync(Rule58ValidationRequest request)
        {
            var summary = await GetExportSummaryAsync(request);
            return summary.TotalValidated;
        }

        private async Task<Rule58ValidationSummary> AnalyseAsync(Rule58ValidationRequest req)
        {
            await ValidateColumnsExistAsync(req.ClientId, req.ValpacTable, [req.ValpacCol037]);
            await ValidateColumnsExistAsync(req.ClientId, req.ProdTable, [req.ProdColPersonelNumber]);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var valpacCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(req.ValpacTable)}\";");
            var prodCount   = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(req.ProdTable)}\";");

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
 {orderSql};";

            var rows = new List<Rule58ValidationRowRecord>();
            int rowNo = 0;

            await using (var cmd = connection.CreateCommand())
            {
                // Store every row so Excel/CSV exports remain complete; browser responses are trimmed after saving.
                cmd.CommandText = rowsSql;
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rowNo++;
                    var validationResult = GetString(reader, "validation_result") ?? "FAIL";
                    var row = new Rule58ValidationRowRecord
                    {
                        ValidationNumber = rowNo,
                        ControlType      = "Control_1",
                        ControlLabel     = "Control 1",
                        ValidationResult = validationResult,
                        DisplayValues    = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["VALPAC__037"] = GetString(reader, "valpac_037"),
                            ["PROD_PERSONEL_NUMBER"] = GetString(reader, "prod_personel")
                        }
                    };
                    EnrichDisplayValues(row);
                    rows.Add(row);
                }
            }

            var rate = total == 0 ? 0m : Math.Round((decimal)failed / total * 100m, 2);
            var overallStatus = failed == 0 ? "PASS" : "FAIL";

            return new Rule58ValidationSummary
            {
                Success           = true,
                ValpacRecordCount = valpacCount,
                ProdRecordCount   = prodCount,
                TotalRequested    = total,
                TotalValidated    = total,
                DisplayedCount    = rows.Count,
                PassCount         = passed,
                FailCount         = failed,
                ExceptionRate     = rate,
                Status            = overallStatus,
                Timestamp         = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ValpacTable       = req.ValpacTable,
                ProdTable         = req.ProdTable,
                ValpacCol037      = req.ValpacCol037,
                ProdColPersonelNumber = req.ProdColPersonelNumber,
                TableLinkageText  = $"{req.ValpacTable}.{req.ValpacCol037} <> {req.ProdTable}.{req.ProdColPersonelNumber}",
                RuleModeText      = $"100% population testing of {req.ValpacTable} against {req.ProdTable}",
                ProcedureSteps    = BuildProcedureSteps(req.ValpacTable, req.ProdTable, req.ValpacCol037, req.ProdColPersonelNumber),
                ClientId          = req.ClientId,
                ControlSummaries  = BuildControlSummaries(total, passed, req.ValpacTable, req.ProdTable),
                ReviewRows        = rows,
                Warning = null
            };
        }

        // ─── SQL Builders ────────────────────────────────────────────────────────

        private static (string BodySql, string OrderSql) BuildValidationSqlParts(string schema, Rule58ValidationRequest req)
        {
            var vt = Sanitise(req.ValpacTable);
            var pt = Sanitise(req.ProdTable);

            var body = $@"
    SELECT
        TRIM(CAST(V.""{req.ValpacCol037}"" AS text)) AS valpac_037,
        (
            SELECT TRIM(CAST(P.""{req.ProdColPersonelNumber}"" AS text))
            FROM ""{schema}"".""{pt}"" P
            WHERE UPPER(TRIM(CAST(P.""{req.ProdColPersonelNumber}"" AS text))) = UPPER(TRIM(CAST(V.""{req.ValpacCol037}"" AS text)))
            LIMIT 1
        ) AS prod_personel,
        CASE
            WHEN EXISTS (
                SELECT 1 FROM ""{schema}"".""{pt}"" P
                WHERE UPPER(TRIM(CAST(P.""{req.ProdColPersonelNumber}"" AS text))) = UPPER(TRIM(CAST(V.""{req.ValpacCol037}"" AS text)))
            ) THEN 'PASS' ELSE 'FAIL'
        END AS validation_result
    FROM ""{schema}"".""{vt}"" V
    WHERE V.""{req.ValpacCol037}"" IS NOT NULL
      AND TRIM(CAST(V.""{req.ValpacCol037}"" AS text)) <> ''";

            var order = @"
ORDER BY
    CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END,
    valpac_037";

            return (body, order);
        }

        private static void EnrichDisplayValues(Rule58ValidationRowRecord row)
        {
            var v      = row.DisplayValues;
            var isPass = string.Equals(row.ValidationResult, "PASS", StringComparison.OrdinalIgnoreCase);
            var stNo   = v.TryGetValue("VALPAC__037", out var s) ? s ?? "" : "";
            var prodNo = v.TryGetValue("PROD_PERSONEL_NUMBER", out var p) ? p ?? "" : "";
            v["FINAL_RESULT_MESSAGE"] = isPass
                ? $"PASS: VALPAC record ({stNo}) found in PRODUCTION as {prodNo}."
                : $"FAIL: VALPAC record ({stNo}) not found in PRODUCTION.";
            row.ValidationExplanation = v["FINAL_RESULT_MESSAGE"] ?? "";
        }

        private static List<Rule58ControlSummaryItemViewModel> BuildControlSummaries(int total, int matched, string valpacTable, string prodTable)
        {
            var fail = Math.Max(total - matched, 0);
            return new List<Rule58ControlSummaryItemViewModel>
            {
                new()
                {
                    ControlType  = "Control_1",
                    ControlLabel = "Control 1",
                    CriteriaText = $"All {valpacTable}._037 records exist in {prodTable}.PERSONEL_NUMBER",
                    TotalCount   = total,
                    PassCount    = matched,
                    FailCount    = fail,
                    Status       = fail == 0 ? "PASS" : "FAIL"
                }
            };
        }

        private static List<string> BuildProcedureSteps(string valpacTable, string prodTable, string v037, string pPersonel) => new()
        {
            $"Select all records from {valpacTable} where {v037} is not null/empty as the population to test.",
            $"For each VALPAC record, attempt to find a matching row in {prodTable} where {pPersonel} = {v037}.",
            $"Mark PASS when a matching {pPersonel} exists in PRODUCTION; FAIL when no match is found.",
            "All VALPAC staff records are expected to exist in PRODUCTION."
        };

        // ─── SQL generation ────────────────────────────────────────────────────

        public string GenerateSql(Rule58ValidationRequest request)
        {
            var (bodySql, orderSql) = BuildValidationSqlParts("{schema}", request);

            return $@"-- ============================================================
-- HEMIS RULE 58 - STAFF VALPAC Data in STAFF PRODUCTION
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Check     : all {request.ValpacCol037} values in {Sanitise(request.ValpacTable)} must exist as {request.ProdColPersonelNumber} in {Sanitise(request.ProdTable)}.
-- ============================================================
WITH validation AS ({bodySql})
SELECT * FROM validation
{orderSql};".Trim();
        }

        // ─── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule58ValidationRequest request, Rule58ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 58);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 58,
                RuleName = "STAFF VALPAC Data in STAFF PRODUCTION",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.ValpacTable,
                DeceasedTable = request.ProdTable,
                StudColumn = request.ValpacCol037,
                DeceasedColumn = request.ProdColPersonelNumber,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(
                    summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList())),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule58WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 58);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);

            var workspace = new Rule58WorkspaceStateViewModel
            {
                ClientId     = row.ClientId,
                RunId        = row.RunId,
                ValpacTable  = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STAFF_VALPAC" : row.StudTable,
                ProdTable    = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_STAFF_PRODUCTION" : row.DeceasedTable,
                ValpacCol037 = string.IsNullOrWhiteSpace(row.StudColumn) ? "_037" : row.StudColumn,
                ProdColPersonelNumber = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "PERSONEL_NUMBER" : row.DeceasedColumn,
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

            if (workspace.Summary != null)
            {
                workspace.Summary.SavedRunId = workspace.RunId;
                LimitBrowserRows(workspace.Summary);
            }
            return workspace;
        }

        public async Task<Rule58RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 58);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary.SavedRunId = runId;

            var viewModel = new Rule58RunReviewViewModel
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

        public async Task<Rule58WorkspaceSaveResult> SaveWorkspaceAsync(Rule58ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule58WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule58WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.ValpacTable,
                    DeceasedTable = request.ProdTable,
                    StudColumn = request.ValpacCol037,
                    DeceasedColumn = request.ProdColPersonelNumber
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule58WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule58WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule58WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule58WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule58WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule58WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        // The browser-preview caption itself is rendered unconditionally by Views/Rule58/Index.cshtml;
        // Warning here only drives that view's "(showing N in preview)" FAIL-section annotation.
        private static void LimitBrowserRows(Rule58ValidationSummary summary)
        {
            if (summary.ReviewRows.Count > BrowserPreviewRowLimit)
                summary.Warning = "Showing preview (first 10 rows) — see stat cards above for full counts. Download for full results.";

            summary.ReviewRows = summary.ReviewRows.Take(BrowserPreviewRowLimit).ToList();
            summary.DisplayedCount = summary.ReviewRows.Count;
        }

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

        private static Rule58ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule58ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
