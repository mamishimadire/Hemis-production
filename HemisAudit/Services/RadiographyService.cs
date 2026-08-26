using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 71: Radiography — Qualification & Surname Validation — validates against the engagement's
    // own uploaded Supabase data instead of a live SQL Server connection. Population is every row in
    // the Radiography source table with a non-blank qualification code. PASS when that qualification
    // code exists in the Clinical Production table; FAIL when missing. The matching Production surname
    // is carried through for informational display alongside the result.
    public class RadiographyService : IRadiographyService
    {
        private const int RowLimit = 200000;
        private const int UiSampleLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public RadiographyService(
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

        public async Task<RadiographyTableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new RadiographyTableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new RadiographyTableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoRadiographyTable = FindFirst(tables, ["Radiography", "radiography"], ["radio"]),
                    AutoProductionTable = FindFirst(tables, ["Clinical_Production", "ClinicalProduction"], ["production"])
                };
            }
            catch (Exception ex) { return new RadiographyTableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<RadiographyColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "qualification" => FindFirst(cols, ["QUALIFICATION"], ["qual"]),
                    "surname"       => FindFirst(cols, ["Surname"], ["surname"]),
                    _ => null
                };
                return new RadiographyColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new RadiographyColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<RadiographyVerifyResult> VerifyTablesAsync(RadiographyVerifyRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.RadiographyTable, [request.QualificationColumn, request.SurnameColumn]);
                await ValidateColumnsExistAsync(request.ClientId, request.ProductionTable, [request.QualificationColumn]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var srcTable = Sanitise(request.RadiographyTable);
                var prodTable = Sanitise(request.ProductionTable);
                var qualCol = Sanitise(request.QualificationColumn);

                var srcCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{srcTable}\";");
                var prodCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{prodTable}\";");

                var ctes = BuildValidationCtes(schema, srcTable, prodTable, qualCol, Sanitise(request.SurnameColumn));
                var countSql = $@"
WITH {ctes}
SELECT
    COUNT(*) AS total,
    COUNT(*) FILTER (WHERE validation_result = 'PASS') AS pass_count,
    COUNT(*) FILTER (WHERE validation_result = 'FAIL') AS fail_count
FROM results;";

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = countSql;
                await using var reader = await cmd.ExecuteReaderAsync();

                var result = new RadiographyVerifyResult { Success = true, RadiographyRecordCount = srcCount, ProductionRecordCount = prodCount };
                if (await reader.ReadAsync())
                {
                    result.TotalTested  = Convert.ToInt32(reader.GetValue(0));
                    result.MatchedCount = Convert.ToInt32(reader.GetValue(1));
                    result.MissingCount = Convert.ToInt32(reader.GetValue(2));
                }
                return result;
            }
            catch (Exception ex) { return new RadiographyVerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<RadiographyValidationSummary> RunValidationAsync(RadiographyValidationRequest request, string? userEmail = null, string? userName = null)
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
                        summary.Error = $"Analysis completed, but the run could not be saved: {ex.Message}";
                        return summary;
                    }
                }

                return ApplyUiSample(summary);
            }
            catch (Exception ex) { return new RadiographyValidationSummary { Success = false, Error = ex.Message }; }
        }

        public async Task<RadiographyValidationSummary> GetExportSummaryAsync(RadiographyValidationRequest request)
            => await AnalyseAsync(request, rowLimit: null);

        public async Task<int> GetPopulationCountAsync(RadiographyValidationRequest request)
        {
            await ValidateColumnsExistAsync(request.ClientId, request.RadiographyTable, [request.QualificationColumn, request.SurnameColumn]);
            await ValidateColumnsExistAsync(request.ClientId, request.ProductionTable, [request.QualificationColumn]);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var qualCol = Sanitise(request.QualificationColumn);
            var surnameCol = Sanitise(request.SurnameColumn);
            var ctes = BuildValidationCtes(schema, request.RadiographyTable, request.ProductionTable, qualCol, surnameCol);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"WITH {ctes} SELECT COUNT(*) FROM results;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private async Task<RadiographyValidationSummary> AnalyseAsync(RadiographyValidationRequest request, int? rowLimit)
        {
            await ValidateColumnsExistAsync(request.ClientId, request.RadiographyTable, [request.QualificationColumn, request.SurnameColumn]);
            await ValidateColumnsExistAsync(request.ClientId, request.ProductionTable, [request.QualificationColumn]);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var qualCol = Sanitise(request.QualificationColumn);
            var surnameCol = Sanitise(request.SurnameColumn);

            var srcCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.RadiographyTable)}\";");
            var prodCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.ProductionTable)}\";");

            var ctes = BuildValidationCtes(schema, request.RadiographyTable, request.ProductionTable, qualCol, surnameCol);

            int total, passed, failed;
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
                    total  = Convert.ToInt32(reader.GetValue(0));
                    passed = Convert.ToInt32(reader.GetValue(1));
                    failed = Convert.ToInt32(reader.GetValue(2));
                }
                else { total = passed = failed = 0; }
            }

            var rows = new List<RadiographyValidationRowRecord>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
WITH {ctes}
SELECT source_qual, source_surname, matched_surname, validation_result FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, source_qual
{(rowLimit.HasValue ? "LIMIT @limit" : "")};";
                if (rowLimit.HasValue) cmd.Parameters.AddWithValue("limit", rowLimit.Value);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new RadiographyValidationRowRecord
                    {
                        ValidationNumber = rows.Count + 1,
                        ControlType = "Control_1",
                        ControlLabel = "Control 1",
                        ValidationResult = GetString(reader, 3) ?? "FAIL",
                        DisplayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["SOURCE_QUAL"] = GetString(reader, 0),
                            ["SOURCE_SURNAME"] = GetString(reader, 1),
                            ["PRODUCTION_SURNAME"] = GetString(reader, 2)
                        }
                    };
                    EnrichDisplayValues(row);
                    rows.Add(row);
                }
            }

            var rate = total == 0 ? 0m : Math.Round((decimal)failed / total * 100m, 2);
            var overallStatus = failed == 0 ? "PASS" : "FAIL";

            var controlSummaries = new List<RadiographyControlSummaryItemViewModel>
            {
                new()
                {
                    ControlType  = "Control_1",
                    ControlLabel = "Control 1",
                    CriteriaText = $"Every {request.RadiographyTable} [{qualCol}] value must exist in {request.ProductionTable} [{qualCol}]",
                    TotalCount   = total,
                    PassCount    = passed,
                    FailCount    = failed,
                    Status       = failed == 0 ? "PASS" : "FAIL"
                }
            };

            return new RadiographyValidationSummary
            {
                Success               = true,
                RadiographyRecordCount = srcCount,
                ProductionRecordCount  = prodCount,
                TotalValidated        = total,
                DisplayedCount        = rows.Count,
                PassCount             = passed,
                FailCount             = failed,
                ExceptionRate         = rate,
                Status                = overallStatus,
                Timestamp             = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                RadiographyTable      = request.RadiographyTable,
                ProductionTable       = request.ProductionTable,
                QualificationColumn   = qualCol,
                SurnameColumn         = surnameCol,
                TableLinkageText      = $"{request.RadiographyTable}.{qualCol} <> {request.ProductionTable}.{qualCol}",
                RuleModeText          = $"100% population testing of {request.RadiographyTable} against {request.ProductionTable}",
                ProcedureSteps        = new List<string>
                {
                    $"Select all records from {request.RadiographyTable} where [{qualCol}] is not null/empty as the population to test.",
                    $"For each record, check if a matching row exists in {request.ProductionTable} on [{qualCol}] (case-insensitive, trimmed).",
                    "Mark PASS when the qualification code is found in Production; FAIL when it is missing.",
                    "The matching Production surname (when found) is shown alongside the result for reference."
                },
                ClientId              = request.ClientId,
                ControlSummaries      = controlSummaries,
                ReviewRows            = rows,
                Warning = total > rows.Count
                    ? $"{total:N0} rows were found; only the first {rows.Count:N0} are stored to keep the app responsive. All totals above are exact."
                    : null
            };
        }

        private static void EnrichDisplayValues(RadiographyValidationRowRecord row)
        {
            var v = row.DisplayValues;
            var isPass = string.Equals(row.ValidationResult, "PASS", StringComparison.OrdinalIgnoreCase);
            var qual = v.TryGetValue("SOURCE_QUAL", out var q) ? q ?? "" : "";
            var surname = v.TryGetValue("SOURCE_SURNAME", out var s) ? s ?? "" : "";

            v["FINAL_RESULT_MESSAGE"] = isPass
                ? $"PASS: qualification '{qual}' (surname: {surname}) found in Production."
                : $"FAIL: qualification '{qual}' (surname: {surname}) not found in Production.";
            row.ValidationExplanation = v["FINAL_RESULT_MESSAGE"] ?? "";
        }

        private static RadiographyValidationSummary ApplyUiSample(RadiographyValidationSummary full)
        {
            if (full.ReviewRows.Count <= UiSampleLimit) return full;
            return new RadiographyValidationSummary
            {
                Success = full.Success, RadiographyRecordCount = full.RadiographyRecordCount, ProductionRecordCount = full.ProductionRecordCount,
                TotalValidated = full.TotalValidated, DisplayedCount = full.DisplayedCount,
                PassCount = full.PassCount, FailCount = full.FailCount, ExceptionRate = full.ExceptionRate,
                Status = full.Status, Timestamp = full.Timestamp,
                RadiographyTable = full.RadiographyTable, ProductionTable = full.ProductionTable,
                QualificationColumn = full.QualificationColumn, SurnameColumn = full.SurnameColumn,
                TableLinkageText = full.TableLinkageText, RuleModeText = full.RuleModeText,
                ProcedureSteps = full.ProcedureSteps, ClientId = full.ClientId, SavedRunId = full.SavedRunId,
                ControlSummaries = full.ControlSummaries,
                ReviewRows = full.ReviewRows.Take(UiSampleLimit).ToList(),
                Warning = "Showing preview (first 10 rows) — see stat cards above for full counts. Download for full results."
            };
        }

        // ─── SQL Builders ────────────────────────────────────────────────────────

        private static string BuildValidationCtes(string schema, string radiographyTable, string productionTable, string qualCol, string surnameCol)
        {
            var srcTable = Sanitise(radiographyTable);
            var prodTable = Sanitise(productionTable);

            return $@"
results AS (
    SELECT
        TRIM(CAST(rg.""{qualCol}"" AS text)) AS source_qual,
        TRIM(CAST(rg.""{surnameCol}"" AS text)) AS source_surname,
        (
            SELECT TRIM(CAST(p.""{surnameCol}"" AS text))
            FROM ""{schema}"".""{prodTable}"" p
            WHERE UPPER(TRIM(CAST(p.""{qualCol}"" AS text))) = UPPER(TRIM(CAST(rg.""{qualCol}"" AS text)))
            LIMIT 1
        ) AS matched_surname,
        CASE
            WHEN EXISTS (
                SELECT 1 FROM ""{schema}"".""{prodTable}"" p
                WHERE UPPER(TRIM(CAST(p.""{qualCol}"" AS text))) = UPPER(TRIM(CAST(rg.""{qualCol}"" AS text)))
            ) THEN 'PASS' ELSE 'FAIL'
        END AS validation_result
    FROM ""{schema}"".""{srcTable}"" rg
    WHERE rg.""{qualCol}"" IS NOT NULL AND TRIM(CAST(rg.""{qualCol}"" AS text)) <> ''
)";
        }

        public string GenerateSql(RadiographyValidationRequest request)
        {
            var qualCol = Sanitise(request.QualificationColumn);
            var surnameCol = Sanitise(request.SurnameColumn);
            var ctes = BuildValidationCtes("{schema}", request.RadiographyTable, request.ProductionTable, qualCol, surnameCol);

            return $@"-- ============================================================
-- HEMIS RULE 71 - Radiography Qualification & Surname Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Check     : all {Sanitise(request.RadiographyTable)}.[{qualCol}] values must exist in {Sanitise(request.ProductionTable)}.[{qualCol}]
-- ============================================================
WITH {ctes}
SELECT * FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, source_qual;".Trim();
        }

        // ─── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(RadiographyValidationRequest request, RadiographyValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 71);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 71,
                RuleName = "Radiography Qualification & Surname Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.RadiographyTable,
                DeceasedTable = request.ProductionTable,
                StudColumn = request.QualificationColumn,
                DeceasedColumn = request.SurnameColumn,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(
                    summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList())),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<RadiographyValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 71);
            if (row == null) return null;
            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        public async Task<RadiographyWorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 71);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary = ApplyUiSample(summary);

            var workspace = new RadiographyWorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                RadiographyTable = string.IsNullOrWhiteSpace(row.StudTable) ? "Radiography" : row.StudTable,
                ProductionTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "Clinical_Production" : row.DeceasedTable,
                QualificationColumn = string.IsNullOrWhiteSpace(row.StudColumn) ? "QUALIFICATION" : row.StudColumn,
                SurnameColumn = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "Surname" : row.DeceasedColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (summary != null) workspace.CurrentStatus = summary.Status;

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

            if (workspace.Summary != null) workspace.Summary.SavedRunId = workspace.RunId;
            return workspace;
        }

        public async Task<RadiographyRunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 71);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary = ApplyUiSample(summary);
            summary.SavedRunId = runId;

            var viewModel = new RadiographyRunReviewViewModel
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
            viewModel.GeneratedSql = GenerateSql(new RadiographyValidationRequest
            {
                ClientId = viewModel.ClientId,
                RadiographyTable = summary.RadiographyTable,
                ProductionTable = summary.ProductionTable,
                QualificationColumn = summary.QualificationColumn,
                SurnameColumn = summary.SurnameColumn
            });

            return viewModel;
        }

        public async Task<RadiographyWorkspaceSaveResult> SaveWorkspaceAsync(RadiographyValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new RadiographyWorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new RadiographyWorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.RadiographyTable,
                    DeceasedTable = request.ProductionTable,
                    StudColumn = request.QualificationColumn,
                    DeceasedColumn = request.SurnameColumn
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new RadiographyWorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new RadiographyWorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<RadiographyWorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new RadiographyWorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new RadiographyWorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new RadiographyWorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        private static RadiographyValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<RadiographyValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
