using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 53: dbo_CRSE Subject Code in MT-audit-prod-CRSE — validates against the engagement's
    // own uploaded Supabase data instead of a live SQL Server connection. Checks that every
    // distinct subject code in the VALPAC-equivalent table exists as a matching code in the
    // PROD-equivalent (H16-style) reference table. Unlike Rule52, there is no approval-status
    // filter — this is a plain 100% population existence check.
    public class Rule53Service : IRule53Service
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

        public Rule53Service(
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

        public async Task<Rule53TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule53TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule53TableDiscoveryResult
                {
                    Success         = true,
                    Tables          = tables,
                    AutoValpacTable = FindFirst(tables, ["dbo_CRSE", "CRSE"], ["crse"]),
                    AutoProdTable   = FindFirst(tables, ["MT-audit-prod-CRSE", "H16CRSE"], ["prod-crse", "h16crse", "auditcrse"])
                };
            }
            catch (Exception ex) { return new Rule53TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule53ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "valpac_subj" => FindFirst(cols, ["_030"], []),
                    "prod_subj"   => FindFirst(cols, ["IALSUBJ"], ["subj"]),
                    _ => null
                };
                return new Rule53ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule53ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule53VerifyResult> VerifyTablesAsync(Rule53ValidationRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.ValpacTable, [request.ValpacSubjCol]);
                await ValidateColumnsExistAsync(request.ClientId, request.ProdTable, [request.ProdSubjCol]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                return new Rule53VerifyResult
                {
                    Success           = true,
                    ValpacRecordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.ValpacTable)}\";"),
                    ProdRecordCount   = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.ProdTable)}\";")
                };
            }
            catch (Exception ex) { return new Rule53VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule53ValidationSummary> RunValidationAsync(Rule53ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
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

                return ApplyUiSample(summary);
            }
            catch (Exception ex) { return new Rule53ValidationSummary { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule53ValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 53);
            if (row == null) return null;
            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        public async Task<Rule53ValidationSummary> GetExportSummaryAsync(Rule53ValidationRequest request)
            => await AnalyseAsync(request, rowLimit: null);

        public async Task<int> GetPopulationCountAsync(Rule53ValidationRequest req)
        {
            await ValidateColumnsExistAsync(req.ClientId, req.ValpacTable, [req.ValpacSubjCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.ProdTable, [req.ProdSubjCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var (bodySql, _) = BuildValidationSqlParts(schema, req);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"WITH validation AS ({bodySql}) SELECT COUNT(*) FROM validation;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private async Task<Rule53ValidationSummary> AnalyseAsync(Rule53ValidationRequest req, int? rowLimit)
        {
            await ValidateColumnsExistAsync(req.ClientId, req.ValpacTable, [req.ValpacSubjCol]);
            await ValidateColumnsExistAsync(req.ClientId, req.ProdTable, [req.ProdSubjCol]);

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
{orderSql}
{(rowLimit.HasValue ? "LIMIT @limit" : "")};";

            var rows = new List<Rule53ValidationRow>();
            int rowNo = 0;

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = rowsSql;
                if (rowLimit.HasValue) cmd.Parameters.AddWithValue("limit", rowLimit.Value);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rowNo++;
                    rows.Add(new Rule53ValidationRow
                    {
                        RowNumber        = rowNo,
                        ControlType      = "Control_1",
                        ValpacSubj       = GetString(reader, "valpac_subj") ?? "",
                        ProdSubj         = GetString(reader, "prod_subj") ?? "",
                        ValidationResult = GetString(reader, "validation_result") ?? "",
                        ResultDetail     = GetString(reader, "result_detail") ?? ""
                    });
                }
            }

            var rate = total == 0 ? 0m : Math.Round((decimal)failed / total * 100m, 2);
            var overallStatus = failed == 0 ? "PASS" : "FAIL";

            return new Rule53ValidationSummary
            {
                Success       = true,
                Status        = overallStatus,
                Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ClientId      = req.ClientId,
                ValpacTable          = req.ValpacTable, ValpacSubjCol = req.ValpacSubjCol,
                ProdTable            = req.ProdTable, ProdSubjCol = req.ProdSubjCol,
                ValpacRecordCount = valpacCount,
                ProdRecordCount   = prodCount,
                TotalValidated    = total,
                PassCount         = passed,
                FailCount         = failed,
                ExceptionRate     = rate,
                TableLinkageText = $"{req.ValpacTable}.{req.ValpacSubjCol} <> {req.ProdTable}.{req.ProdSubjCol}",
                RuleModeText     = $"100% population testing: all [{req.ValpacSubjCol}] values in {req.ValpacTable} must exist as [{req.ProdSubjCol}] in {req.ProdTable}",
                ProcedureSteps   = BuildProcedureSteps(req),
                ControlSummaries = new List<Rule53ControlSummary>
                {
                    new()
                    {
                        ControlType   = "Control_1",
                        ControlLabel  = "Control 1",
                        CriteriaText  = $"All [{req.ValpacSubjCol}] values in {req.ValpacTable} exist as [{req.ProdSubjCol}] in {req.ProdTable}",
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
        private static Rule53ValidationSummary ApplyUiSample(Rule53ValidationSummary full)
        {
            if (full.ValidationRows.Count <= UiSampleLimit) return full;

            return new Rule53ValidationSummary
            {
                Success = full.Success, Error = full.Error, SavedRunId = full.SavedRunId, ClientId = full.ClientId,
                Status = full.Status, Timestamp = full.Timestamp,
                ValpacTable = full.ValpacTable, ValpacSubjCol = full.ValpacSubjCol,
                ProdTable = full.ProdTable, ProdSubjCol = full.ProdSubjCol,
                ValpacRecordCount = full.ValpacRecordCount, ProdRecordCount = full.ProdRecordCount,
                TotalValidated = full.TotalValidated, PassCount = full.PassCount, FailCount = full.FailCount,
                ExceptionRate = full.ExceptionRate, TableLinkageText = full.TableLinkageText, RuleModeText = full.RuleModeText,
                ProcedureSteps = full.ProcedureSteps, ControlSummaries = full.ControlSummaries,
                ValidationRows = full.ValidationRows.Take(UiSampleLimit).ToList(),
                Warning = "UI results show only the first 10 sample rows. Download the results to access the full tested population."
            };
        }

        private static (string BodySql, string OrderSql) BuildValidationSqlParts(string schema, Rule53ValidationRequest req)
        {
            var vt = Sanitise(req.ValpacTable);
            var pt = Sanitise(req.ProdTable);

            var body = $@"
    SELECT
        VD.valpac_subj,
        (
            SELECT TRIM(CAST(P.""{req.ProdSubjCol}"" AS text))
            FROM ""{schema}"".""{pt}"" P
            WHERE UPPER(TRIM(CAST(P.""{req.ProdSubjCol}"" AS text))) = VD.valpac_subj
            LIMIT 1
        ) AS prod_subj,
        CASE
            WHEN EXISTS (
                SELECT 1 FROM ""{schema}"".""{pt}"" P
                WHERE UPPER(TRIM(CAST(P.""{req.ProdSubjCol}"" AS text))) = VD.valpac_subj
            ) THEN 'PASS' ELSE 'FAIL'
        END AS validation_result,
        CASE
            WHEN EXISTS (
                SELECT 1 FROM ""{schema}"".""{pt}"" P
                WHERE UPPER(TRIM(CAST(P.""{req.ProdSubjCol}"" AS text))) = VD.valpac_subj
            ) THEN 'PASS: Subject code found in ' || '{pt}'
            ELSE 'FAIL: Subject code not found in ' || '{pt}'
        END AS result_detail
    FROM (
        SELECT DISTINCT UPPER(TRIM(CAST(V.""{req.ValpacSubjCol}"" AS text))) AS valpac_subj
        FROM ""{schema}"".""{vt}"" V
        WHERE V.""{req.ValpacSubjCol}"" IS NOT NULL
          AND TRIM(CAST(V.""{req.ValpacSubjCol}"" AS text)) <> ''
    ) VD";

            var order = @"
ORDER BY
    CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END,
    valpac_subj";

            return (body, order);
        }

        private static List<string> BuildProcedureSteps(Rule53ValidationRequest req) => new()
        {
            $"Select all distinct [{req.ValpacSubjCol}] values from {req.ValpacTable} as the population to test.",
            $"For each value, attempt to find a matching row in {req.ProdTable} on column [{req.ProdSubjCol}].",
            "Mark PASS when a matching value exists; FAIL when no match is found.",
            $"All [{req.ValpacSubjCol}] values in {req.ValpacTable} are expected to be present as [{req.ProdSubjCol}] in {req.ProdTable}."
        };

        // ── SQL generation ────────────────────────────────────────────────────

        public string GenerateValidationSql(Rule53ValidationRequest req)
        {
            var (bodySql, orderSql) = BuildValidationSqlParts("{schema}", req);

            return $@"-- ============================================================
-- HEMIS RULE 53 - dbo_CRSE Subject Code in MT-audit-prod-CRSE
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Check     : all [{req.ValpacSubjCol}] values in ""{Sanitise(req.ValpacTable)}"" must exist as [{req.ProdSubjCol}] in ""{Sanitise(req.ProdTable)}"".
-- ============================================================
WITH validation AS ({bodySql})
SELECT * FROM validation
{orderSql};".Trim();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule53ValidationRequest request, Rule53ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 53);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 53,
                RuleName = "dbo_CRSE Subject Code in MT-audit-prod-CRSE",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.ValpacTable,
                DeceasedTable = request.ProdTable,
                StudColumn = request.ValpacSubjCol,
                DeceasedColumn = request.ProdSubjCol,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.ValidationRows.Where(r => r.ValidationResult == "FAIL"))),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule53WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 53);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary = ApplyUiSample(summary);

            var workspace = new Rule53WorkspaceStateViewModel
            {
                ClientId     = row.ClientId,
                RunId        = row.RunId,
                ValpacTable  = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_CRSE" : row.StudTable,
                ProdTable    = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "MT-audit-prod-CRSE" : row.DeceasedTable,
                ValpacSubjCol = string.IsNullOrWhiteSpace(row.StudColumn) ? "_030" : row.StudColumn,
                ProdSubjCol   = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "IALSUBJ" : row.DeceasedColumn,
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

        public async Task<Rule53RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 53);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary = ApplyUiSample(summary);
            summary.SavedRunId = runId;

            var viewModel = new Rule53RunReviewViewModel
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

        public async Task<Rule53WorkspaceSaveResult> SaveWorkspaceAsync(Rule53ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule53WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule53WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.ValpacTable,
                    DeceasedTable = request.ProdTable,
                    StudColumn = request.ValpacSubjCol,
                    DeceasedColumn = request.ProdSubjCol
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule53WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule53WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule53WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule53WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule53WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule53WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        private static Rule53ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule53ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
