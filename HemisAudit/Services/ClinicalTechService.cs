using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 69: ClinicalTech — Qualification & Surname Validation — validates against the engagement's
    // own uploaded Supabase data instead of a live SQL Server connection. Population is every row in
    // the ClinicalTech source table with a non-blank qualification code. PASS when that qualification
    // code exists in the Clinical Production table; FAIL when missing. The matching Production surname
    // is carried through for informational display alongside the result.
    public class ClinicalTechService : IClinicalTechService
    {
        // Storage/Excel population cap — effectively "full population" at this data scale.
        private const int RowLimit = 200000;
        private const int UiSampleLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public ClinicalTechService(
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

        public async Task<ClinicalTechTableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new ClinicalTechTableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new ClinicalTechTableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoClinicaltechTable = FindFirst(tables, ["Clinicaltech", "clinicaltech"], ["clinical"]),
                    AutoProductionTable = FindFirst(tables, ["Clinical_Production", "ClinicalProduction"], ["production"])
                };
            }
            catch (Exception ex) { return new ClinicalTechTableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<ClinicalTechColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
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
                return new ClinicalTechColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new ClinicalTechColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<ClinicalTechVerifyResult> VerifyTablesAsync(ClinicalTechVerifyRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.ClinicaltechTable, [request.QualificationColumn, request.SurnameColumn]);
                await ValidateColumnsExistAsync(request.ClientId, request.ProductionTable, [request.QualificationColumn]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var ctTable = Sanitise(request.ClinicaltechTable);
                var prodTable = Sanitise(request.ProductionTable);
                var qualCol = Sanitise(request.QualificationColumn);

                var ctCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{ctTable}\";");
                var prodCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{prodTable}\";");

                var ctes = BuildValidationCtes(schema, ctTable, prodTable, qualCol, Sanitise(request.SurnameColumn));
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

                var result = new ClinicalTechVerifyResult { Success = true, ClinicaltechRecordCount = ctCount, ProductionRecordCount = prodCount };
                if (await reader.ReadAsync())
                {
                    result.TotalTested  = Convert.ToInt32(reader.GetValue(0));
                    result.MatchedCount = Convert.ToInt32(reader.GetValue(1));
                    result.MissingCount = Convert.ToInt32(reader.GetValue(2));
                }
                return result;
            }
            catch (Exception ex) { return new ClinicalTechVerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<ClinicalTechValidationSummary> RunValidationAsync(ClinicalTechValidationRequest request, string? userEmail = null, string? userName = null)
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

                return ApplyUiSample(summary);
            }
            catch (Exception ex) { return new ClinicalTechValidationSummary { Success = false, Error = ex.Message }; }
        }

        private async Task<ClinicalTechValidationSummary> AnalyseAsync(ClinicalTechValidationRequest request)
        {
            await ValidateColumnsExistAsync(request.ClientId, request.ClinicaltechTable, [request.QualificationColumn, request.SurnameColumn]);
            await ValidateColumnsExistAsync(request.ClientId, request.ProductionTable, [request.QualificationColumn]);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var qualCol = Sanitise(request.QualificationColumn);
            var surnameCol = Sanitise(request.SurnameColumn);

            var ctCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.ClinicaltechTable)}\";");
            var prodCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.ProductionTable)}\";");

            var ctes = BuildValidationCtes(schema, request.ClinicaltechTable, request.ProductionTable, qualCol, surnameCol);

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

            var rows = new List<ClinicalTechValidationRowRecord>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
WITH {ctes}
SELECT source_qual, source_surname, matched_surname, validation_result FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, source_qual
LIMIT @limit;";
                cmd.Parameters.AddWithValue("limit", RowLimit);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new ClinicalTechValidationRowRecord
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

            var controlSummaries = new List<ClinicalTechControlSummaryItemViewModel>
            {
                new()
                {
                    ControlType  = "Control_1",
                    ControlLabel = "Control 1",
                    CriteriaText = $"Every {request.ClinicaltechTable} [{qualCol}] value must exist in {request.ProductionTable} [{qualCol}]",
                    TotalCount   = total,
                    PassCount    = passed,
                    FailCount    = failed,
                    Status       = failed == 0 ? "PASS" : "FAIL"
                }
            };

            return new ClinicalTechValidationSummary
            {
                Success               = true,
                ClinicaltechRecordCount = ctCount,
                ProductionRecordCount   = prodCount,
                TotalValidated        = total,
                DisplayedCount        = rows.Count,
                PassCount             = passed,
                FailCount             = failed,
                ExceptionRate         = rate,
                Status                = overallStatus,
                Timestamp             = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ClinicaltechTable     = request.ClinicaltechTable,
                ProductionTable       = request.ProductionTable,
                QualificationColumn   = qualCol,
                SurnameColumn         = surnameCol,
                TableLinkageText      = $"{request.ClinicaltechTable}.{qualCol} <> {request.ProductionTable}.{qualCol}",
                RuleModeText          = $"100% population testing of {request.ClinicaltechTable} against {request.ProductionTable}",
                ProcedureSteps        = new List<string>
                {
                    $"Select all records from {request.ClinicaltechTable} where [{qualCol}] is not null/empty as the population to test.",
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

        private static void EnrichDisplayValues(ClinicalTechValidationRowRecord row)
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

        private static ClinicalTechValidationSummary ApplyUiSample(ClinicalTechValidationSummary full)
        {
            if (full.ReviewRows.Count <= UiSampleLimit) return full;
            return new ClinicalTechValidationSummary
            {
                Success = full.Success, ClinicaltechRecordCount = full.ClinicaltechRecordCount, ProductionRecordCount = full.ProductionRecordCount,
                TotalValidated = full.TotalValidated, DisplayedCount = full.DisplayedCount,
                PassCount = full.PassCount, FailCount = full.FailCount, ExceptionRate = full.ExceptionRate,
                Status = full.Status, Timestamp = full.Timestamp,
                ClinicaltechTable = full.ClinicaltechTable, ProductionTable = full.ProductionTable,
                QualificationColumn = full.QualificationColumn, SurnameColumn = full.SurnameColumn,
                TableLinkageText = full.TableLinkageText, RuleModeText = full.RuleModeText,
                ProcedureSteps = full.ProcedureSteps, ClientId = full.ClientId, SavedRunId = full.SavedRunId,
                ControlSummaries = full.ControlSummaries,
                ReviewRows = full.ReviewRows.Take(UiSampleLimit).ToList(),
                Warning = "Showing preview (first 10 rows) — see stat cards above for full counts. Download for full results."
            };
        }

        // ─── SQL Builders ────────────────────────────────────────────────────────

        private static string BuildValidationCtes(string schema, string clinicaltechTable, string productionTable, string qualCol, string surnameCol)
        {
            var ctTable = Sanitise(clinicaltechTable);
            var prodTable = Sanitise(productionTable);

            return $@"
results AS (
    SELECT
        TRIM(CAST(ct.""{qualCol}"" AS text)) AS source_qual,
        TRIM(CAST(ct.""{surnameCol}"" AS text)) AS source_surname,
        (
            SELECT TRIM(CAST(p.""{surnameCol}"" AS text))
            FROM ""{schema}"".""{prodTable}"" p
            WHERE UPPER(TRIM(CAST(p.""{qualCol}"" AS text))) = UPPER(TRIM(CAST(ct.""{qualCol}"" AS text)))
            LIMIT 1
        ) AS matched_surname,
        CASE
            WHEN EXISTS (
                SELECT 1 FROM ""{schema}"".""{prodTable}"" p
                WHERE UPPER(TRIM(CAST(p.""{qualCol}"" AS text))) = UPPER(TRIM(CAST(ct.""{qualCol}"" AS text)))
            ) THEN 'PASS' ELSE 'FAIL'
        END AS validation_result
    FROM ""{schema}"".""{ctTable}"" ct
    WHERE ct.""{qualCol}"" IS NOT NULL AND TRIM(CAST(ct.""{qualCol}"" AS text)) <> ''
)";
        }

        public string GenerateSql(ClinicalTechValidationRequest request)
        {
            var qualCol = Sanitise(request.QualificationColumn);
            var surnameCol = Sanitise(request.SurnameColumn);
            var ctes = BuildValidationCtes("{schema}", request.ClinicaltechTable, request.ProductionTable, qualCol, surnameCol);

            return $@"-- ============================================================
-- HEMIS RULE 69 - ClinicalTech Qualification & Surname Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Check     : all {Sanitise(request.ClinicaltechTable)}.[{qualCol}] values must exist in {Sanitise(request.ProductionTable)}.[{qualCol}]
-- ============================================================
WITH {ctes}
SELECT * FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, source_qual;".Trim();
        }

        // ─── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(ClinicalTechValidationRequest request, ClinicalTechValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 69);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 69,
                RuleName = "ClinicalTech Qualification & Surname Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.ClinicaltechTable,
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

        public async Task<ClinicalTechValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 69);
            if (row == null) return null;
            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        public async Task<ClinicalTechWorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 69);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary = ApplyUiSample(summary);

            var workspace = new ClinicalTechWorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                ClinicaltechTable = string.IsNullOrWhiteSpace(row.StudTable) ? "Clinicaltech" : row.StudTable,
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

        public async Task<ClinicalTechRunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 69);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary = ApplyUiSample(summary);
            summary.SavedRunId = runId;

            var viewModel = new ClinicalTechRunReviewViewModel
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
            viewModel.GeneratedSql = GenerateSql(new ClinicalTechValidationRequest
            {
                ClientId = viewModel.ClientId,
                ClinicaltechTable = summary.ClinicaltechTable,
                ProductionTable = summary.ProductionTable,
                QualificationColumn = summary.QualificationColumn,
                SurnameColumn = summary.SurnameColumn
            });

            return viewModel;
        }

        public async Task<ClinicalTechWorkspaceSaveResult> SaveWorkspaceAsync(ClinicalTechValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new ClinicalTechWorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new ClinicalTechWorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.ClinicaltechTable,
                    DeceasedTable = request.ProductionTable,
                    StudColumn = request.QualificationColumn,
                    DeceasedColumn = request.SurnameColumn
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new ClinicalTechWorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new ClinicalTechWorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<ClinicalTechWorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new ClinicalTechWorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new ClinicalTechWorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new ClinicalTechWorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        private static ClinicalTechValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<ClinicalTechValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
