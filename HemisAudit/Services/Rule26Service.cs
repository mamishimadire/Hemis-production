using System.Globalization;
using System.Security.Cryptography;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 26: dbo_PROF to Payroll_Sample 4-Control Validation — validates against the
    // engagement's own uploaded Supabase data instead of a live SQL Server connection. Unlike the
    // pure-SQL reconciliation rules (23-25), Rule26's matching logic (first-letter comparisons,
    // a configurable blank-group-code pass list, lenient YYYYMMDD birth-date parsing) is genuinely
    // C#-side business logic, not a simple equality join — so this port keeps the comparison
    // algorithm verbatim and only translates the connection/data-loading layer to Postgres. PROF is
    // staff-scale (not student-scale) and "Payroll_Sample" is, by name, an auditor-drawn sample, so
    // this doesn't carry the same CREG-scale OOM risk as Rule18 — a defensive cap on the persisted
    // exceptions list is still applied from the start, matching house style.
    public class Rule26Service : IRule26Service
    {
        private const int RuleNumber = 26;
        private const string RuleName = "Rule 26 - dbo_PROF to Payroll_Sample 4-Control Validation";
        private const int BrowserPreviewRowLimit = 10;
        private const int MaxSavedExceptionRows = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule26Service(
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

        // ── Engagement data source (uploaded tables, not a live SQL Server) ────────────────

        public async Task<TableListResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new TableListResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                var profTable = tables.FirstOrDefault(t => t.Equals("dbo_PROF", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Equals("PROF", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Contains("prof", StringComparison.OrdinalIgnoreCase));
                var payrollTable = tables.FirstOrDefault(t => t.Equals("Payroll_Sample", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Equals("PAYROLL", StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Contains("payroll", StringComparison.OrdinalIgnoreCase));

                return new TableListResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = profTable,
                    AutoDeceasedTable = payrollTable
                };
            }
            catch (Exception ex)
            {
                return new TableListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule26ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName, bool isProfTable)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);

                return new Rule26ColumnSelectionResult
                {
                    Success = true,
                    Columns = columns,
                    AutoPersonnelColumn = isProfTable
                        ? FindFirst(columns, ["_037"], ["037", "personnel", "staff", "employee"])
                        : FindFirst(columns, ["PERSONNEL_NUMBER"], ["personnel_number", "personnel", "employee", "staff"]),
                    AutoEmploymentTypeColumn = isProfTable
                        ? FindFirst(columns, ["_041"], ["041", "perm", "temp", "employment"])
                        : FindFirst(columns, ["PERMANENT_OR_TEMP"], ["permanent_or_temp", "perm", "temp", "employment"]),
                    AutoGenderColumn = isProfTable
                        ? FindFirst(columns, ["_012"], ["012", "gender", "sex"])
                        : FindFirst(columns, ["GENDER"], ["gender", "sex"]),
                    AutoGroupColumn = isProfTable
                        ? FindFirst(columns, ["_013"], ["013", "group", "race"])
                        : FindFirst(columns, ["GROUP_NAME"], ["group_name", "group", "race"]),
                    AutoBirthDateColumn = isProfTable
                        ? FindFirst(columns, ["_011"], ["011", "birth", "dob", "date"])
                        : FindFirst(columns, ["BIRTH_DATE"], ["birth_date", "birth", "dob", "date"])
                };
            }
            catch (Exception ex)
            {
                return new Rule26ColumnSelectionResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule26VerifyResult> VerifyTablesAsync(Rule26VerifyRequest request)
        {
            try
            {
                ValidateVerifyRequest(request);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var profKeyExpr = $"UPPER(TRIM(CAST(\"{request.ProfPersonnelColumn}\" AS text)))";
                var payrollKeyExpr = $"UPPER(TRIM(CAST(\"{request.PayrollPersonnelColumn}\" AS text)))";

                var sql = $@"
WITH prof_base AS (
    SELECT {profKeyExpr} AS personnel_key
    FROM ""{schema}"".""{request.ProfTable}""
    WHERE ""{request.ProfPersonnelColumn}"" IS NOT NULL
),
payroll_base AS (
    SELECT {payrollKeyExpr} AS personnel_key
    FROM ""{schema}"".""{request.PayrollTable}""
)
SELECT
    (SELECT COUNT(*) FROM prof_base) AS prof_count,
    (SELECT COUNT(*) FROM payroll_base) AS payroll_count,
    (SELECT COUNT(*) FROM prof_base p WHERE EXISTS (SELECT 1 FROM payroll_base py WHERE py.personnel_key = p.personnel_key)) AS linked_count,
    (SELECT COUNT(*) FROM prof_base p WHERE NOT EXISTS (SELECT 1 FROM payroll_base py WHERE py.personnel_key = p.personnel_key)) AS prof_without_payroll,
    (SELECT COUNT(*) FROM payroll_base py WHERE NOT EXISTS (SELECT 1 FROM prof_base p WHERE p.personnel_key = py.personnel_key)) AS payroll_without_prof;";

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                    return new Rule26VerifyResult { Success = false, Error = "No verification data returned." };

                return new Rule26VerifyResult
                {
                    Success = true,
                    ProfRecordCount = Convert.ToInt32(reader.GetValue(0)),
                    PayrollRecordCount = Convert.ToInt32(reader.GetValue(1)),
                    LinkedRecordCount = Convert.ToInt32(reader.GetValue(2)),
                    ProfWithoutPayrollCount = Convert.ToInt32(reader.GetValue(3)),
                    PayrollWithoutProfCount = Convert.ToInt32(reader.GetValue(4))
                };
            }
            catch (Exception ex)
            {
                return new Rule26VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule26ValidationSummary> RunValidationAsync(Rule26ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request);

                var summary = await AnalyseAsync(request);
                if (summary.Success && request.ClientId > 0)
                {
                    try
                    {
                        summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName);
                    }
                    catch (Exception ex)
                    {
                        summary.Warning = $"Validation completed, but the workspace could not be saved automatically: {ex.Message}";
                    }
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule26ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule26ValidationSummary> GetExportSummaryAsync(Rule26ValidationRequest request)
        {
            ValidateRequest(request);
            return await AnalyseAsync(request);
        }

        public async Task<int> GetPopulationCountAsync(Rule26ValidationRequest request)
        {
            var summary = await GetExportSummaryAsync(request);
            return summary.TotalValidated;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule26WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, RuleNumber);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule26WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                ProfTable = deserializedSummary?.ProfTable ?? row.StudTable,
                PayrollTable = deserializedSummary?.PayrollTable ?? row.DeceasedTable,
                ProfPersonnelColumn = deserializedSummary?.ProfPersonnelColumn ?? row.StudColumn,
                PayrollPersonnelColumn = deserializedSummary?.PayrollPersonnelColumn ?? row.DeceasedColumn,
                ProfEmploymentTypeColumn = deserializedSummary?.ProfEmploymentTypeColumn ?? "",
                ProfGenderColumn = deserializedSummary?.ProfGenderColumn ?? "",
                ProfGroupColumn = deserializedSummary?.ProfGroupColumn ?? "",
                ProfBirthDateColumn = deserializedSummary?.ProfBirthDateColumn ?? "",
                BlankPayrollGroupPassCodes = deserializedSummary?.BlankPayrollGroupPassCodes ?? "Z",
                PayrollEmploymentTypeColumn = deserializedSummary?.PayrollEmploymentTypeColumn ?? "",
                PayrollGenderColumn = deserializedSummary?.PayrollGenderColumn ?? "",
                PayrollGroupColumn = deserializedSummary?.PayrollGroupColumn ?? "",
                PayrollBirthDateColumn = deserializedSummary?.PayrollBirthDateColumn ?? "",
                CurrentStatus = deserializedSummary?.Status ?? row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            workspace.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(clientId, currentUser.Id) ?? ""
                : "";

            var signoffs = await _systemDb.GetRuleRunSignoffsAsync(workspace.RunId!.Value, currentUser?.Id);
            workspace.HasDataAnalystSignoff = signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            var currentRoleSignoff = signoffs.FirstOrDefault(s =>
                ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, workspace.CurrentUserEngagementRole));
            workspace.CurrentUserHasSignedOff = currentRoleSignoff != null;
            workspace.CurrentUserSignoffComment = currentRoleSignoff?.Comment ?? "";
            workspace.IsWorkspaceSaved = await _systemDb.IsWorkspaceSavedAsync(workspace.RunId!.Value);

            return workspace;
        }

        public async Task<Rule26RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, RuleNumber);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule26RunReviewViewModel
            {
                RunId = row.RunId,
                ClientId = row.ClientId,
                IsCurrentRun = row.IsCurrent,
                EngagementName = row.EngagementName,
                MaconomyNumber = row.MaconomyNumber,
                Summary = summary
            };

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            review.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(review.ClientId, currentUser.Id) ?? ""
                : "";

            review.Signoffs = await _systemDb.GetRuleRunSignoffsAsync(runId, currentUser?.Id);
            review.HasDataAnalystSignoff = review.Signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));

            return review;
        }

        public async Task<Rule26WorkspaceSaveResult> SaveWorkspaceAsync(Rule26ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0)
                    return new Rule26WorkspaceSaveResult { Success = false, Error = "Run the validation first so the workspace can be saved." };

                ValidateRequest(request);

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule26WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.ProfTable,
                    DeceasedTable = request.PayrollTable,
                    StudColumn = request.ProfPersonnelColumn,
                    DeceasedColumn = request.PayrollPersonnelColumn
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule26WorkspaceSaveResult
                {
                    Success = true,
                    Message = clearedSignoffs > 0
                        ? "Workspace saved. Existing signoffs were removed and the run must be reviewed again."
                        : "Workspace saved and marked for review again.",
                    SignoffsCleared = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace = workspace
                };
            }
            catch (Exception ex)
            {
                return new Rule26WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule26WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule26WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule26WorkspaceSaveResult
                {
                    Success = true,
                    Message = clearedSignoffs > 0
                        ? "Workspace editing enabled. Existing signoffs were removed."
                        : "Workspace editing enabled.",
                    SignoffsCleared = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace = workspace
                };
            }
            catch (Exception ex)
            {
                return new Rule26WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 26 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 26 run.");

            if (!string.Equals(signoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase) &&
                !await _systemDb.HasRuleSignoffRoleAsync(runId, "DataAnalyst"))
            {
                throw new InvalidOperationException("The assigned data analyst must sign off before this review can be completed.");
            }

            await _systemDb.AddOrUpdateRuleSignoffAsync(runId, clientId, reviewer.Id, signoffRole!, comment);
        }

        public async Task RemoveSignoffAsync(int runId, string reviewerEmail)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 26 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public Task<string> GenerateSqlAsync(Rule26ValidationRequest request)
        {
            ValidateRequest(request);

            var blankPayrollGroupPassCodeSqlList = BuildRule26SqlCharacterList(request.BlankPayrollGroupPassCodes);
            var profBirthExpr = BuildProfBirthSqlExpression($"TRIM(CAST(p.\"{request.ProfBirthDateColumn}\" AS text))");

            var sql = $@"-- ============================================================================
-- HEMIS RULE 26: DBO_PROF TO PAYROLL_SAMPLE 4-CONTROL VALIDATION
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- ============================================================================

DROP TABLE IF EXISTS prof_base;
CREATE TEMP TABLE prof_base AS
SELECT
    TRIM(CAST(p.""{request.ProfPersonnelColumn}"" AS text))                       AS personnel_key,
    LTRIM(CAST(p.""{request.ProfEmploymentTypeColumn}"" AS text))                 AS employment_type,
    UPPER(TRIM(CAST(p.""{request.ProfGenderColumn}"" AS text)))                   AS gender_value,
    TRIM(CAST(p.""{request.ProfGroupColumn}"" AS text))                          AS group_value,
    TRIM(CAST(p.""{request.ProfBirthDateColumn}"" AS text))                      AS birth_raw_value,
    {profBirthExpr}                                                              AS birth_date_value
FROM ""{{schema}}"".""{request.ProfTable}"" p
WHERE p.""{request.ProfPersonnelColumn}"" IS NOT NULL;

DROP TABLE IF EXISTS payroll_base;
CREATE TEMP TABLE payroll_base AS
SELECT
    TRIM(CAST(py.""{request.PayrollPersonnelColumn}"" AS text))                   AS personnel_key,
    LTRIM(CAST(py.""{request.PayrollEmploymentTypeColumn}"" AS text))             AS employment_type,
    UPPER(TRIM(CAST(py.""{request.PayrollGenderColumn}"" AS text)))               AS gender_value,
    TRIM(CAST(py.""{request.PayrollGroupColumn}"" AS text))                       AS group_value,
    TRIM(CAST(py.""{request.PayrollBirthDateColumn}"" AS text))                   AS birth_raw_value,
    TO_DATE(NULLIF(TRIM(CAST(py.""{request.PayrollBirthDateColumn}"" AS text)), ''), 'YYYY-MM-DD') AS birth_date_value
FROM ""{{schema}}"".""{request.PayrollTable}"" py;

-- STEP 1: population counts
SELECT 'PROF population' AS metric, COUNT(*) AS record_count FROM prof_base
UNION ALL
SELECT 'Payroll population', COUNT(*) FROM payroll_base
UNION ALL
SELECT 'Linked population', COUNT(*) FROM prof_base p WHERE EXISTS (SELECT 1 FROM payroll_base py WHERE py.personnel_key = p.personnel_key)
UNION ALL
SELECT 'PROF without Payroll', COUNT(*) FROM prof_base p LEFT JOIN payroll_base py ON py.personnel_key = p.personnel_key WHERE py.personnel_key IS NULL;

-- CONTROL 2: Employment Type Match (first letter) — LEFT(x,1) comparison, blank-safe
-- CONTROL 3: Gender Consistency (first letter)
-- CONTROL 4: Race/Group Code Accuracy (first letter, blank Payroll GROUP_NAME accepted when PROF group code is in: {blankPayrollGroupPassCodeSqlList})
-- CONTROL 5: Birth Date Integrity (PROF YYYYMMDD parsed and compared to Payroll BIRTH_DATE)
-- NOTE: The exact first-letter / blank-code / lenient-date matching semantics are evaluated in
-- application code (not pure SQL) so that blank/invalid values degrade gracefully instead of
-- producing false exceptions — this script reproduces the population and control intent for review.";

            return Task.FromResult(sql.Trim());
        }

        // ── Analysis ─────────────────────────────────────────────────────────────────────

        private async Task<Rule26ValidationSummary> AnalyseAsync(Rule26ValidationRequest request)
        {
            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var payrollColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.PayrollTable);
            var payrollNameColumn = payrollColumns.FirstOrDefault(c => c.Equals("PERSONNEL_NAME", StringComparison.OrdinalIgnoreCase));

            var profRecords = await LoadProfRecordsAsync(connection, schema, request);
            var payrollRecords = await LoadPayrollRecordsAsync(connection, schema, request, payrollNameColumn);

            var payrollByPersonnel = payrollRecords
                .Where(r => !string.IsNullOrWhiteSpace(r.PersonnelKey))
                .GroupBy(r => r.PersonnelKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var direction1 = BuildProfToPayrollDirection(profRecords, payrollByPersonnel, request);

            var totalValidated = direction1.Controls.Sum(c => c.TotalTested);
            var totalExceptions = direction1.TotalExceptions;
            var linkedRecordCount = direction1.LinkedRecordCount;
            var passCount = Math.Max(0, totalValidated - totalExceptions);

            var allExceptions = direction1.Exceptions.ToList();
            var exceptionsTruncated = allExceptions.Count > MaxSavedExceptionRows;
            var savedExceptions = exceptionsTruncated ? allExceptions.Take(MaxSavedExceptionRows).ToList() : allExceptions;
            direction1.Exceptions = savedExceptions;

            return new Rule26ValidationSummary
            {
                Success = true,
                TotalValidated = totalValidated,
                MatchingCount = linkedRecordCount,
                DisplayedCount = savedExceptions.Count,
                PassCount = passCount,
                FailCount = totalExceptions,
                ExceptionRate = totalValidated == 0 ? 0 : Math.Round((decimal)totalExceptions / totalValidated * 100m, 2),
                Status = totalExceptions == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ProfTable = request.ProfTable,
                PayrollTable = request.PayrollTable,
                ProfPersonnelColumn = request.ProfPersonnelColumn,
                ProfEmploymentTypeColumn = request.ProfEmploymentTypeColumn,
                ProfGenderColumn = request.ProfGenderColumn,
                ProfGroupColumn = request.ProfGroupColumn,
                ProfBirthDateColumn = request.ProfBirthDateColumn,
                BlankPayrollGroupPassCodes = request.BlankPayrollGroupPassCodes,
                PayrollPersonnelColumn = request.PayrollPersonnelColumn,
                PayrollEmploymentTypeColumn = request.PayrollEmploymentTypeColumn,
                PayrollGenderColumn = request.PayrollGenderColumn,
                PayrollGroupColumn = request.PayrollGroupColumn,
                PayrollBirthDateColumn = request.PayrollBirthDateColumn,
                ProfRecordCount = profRecords.Count,
                PayrollRecordCount = payrollRecords.Count,
                LinkedRecordCount = linkedRecordCount,
                ClientId = request.ClientId,
                ExceptionsTruncated = exceptionsTruncated,
                Directions = [direction1],
                Exceptions = savedExceptions,
                PassRows = direction1.PassRows.ToList(),
                Warning = exceptionsTruncated
                    ? $"Only the first {MaxSavedExceptionRows:N0} exception rows were saved for browser review and export performance. Total exceptions found: {totalExceptions:N0}."
                    : null
            };
        }

        private static async Task<List<ProfRecord>> LoadProfRecordsAsync(NpgsqlConnection connection, string schema, Rule26ValidationRequest request)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
SELECT ""{request.ProfPersonnelColumn}"", ""{request.ProfEmploymentTypeColumn}"", ""{request.ProfGenderColumn}"", ""{request.ProfGroupColumn}"", ""{request.ProfBirthDateColumn}""
FROM ""{schema}"".""{request.ProfTable}""
WHERE ""{request.ProfPersonnelColumn}"" IS NOT NULL;";

            await using var reader = await cmd.ExecuteReaderAsync();
            var records = new List<ProfRecord>();
            while (await reader.ReadAsync())
            {
                var rawPersonnel = ToInvariantString(reader.GetValue(0));
                records.Add(new ProfRecord
                {
                    PersonnelNumber = rawPersonnel,
                    PersonnelKey = rawPersonnel,
                    EmploymentType = ToNullableInvariantString(reader.GetValue(1)),
                    Gender = ToNullableInvariantString(reader.GetValue(2)),
                    GroupCode = ToNullableInvariantString(reader.GetValue(3)),
                    BirthRaw = ToNullableInvariantString(reader.GetValue(4)),
                    BirthDate = ConvertProfBirthDate(ToNullableInvariantString(reader.GetValue(4)))
                });
            }

            return records;
        }

        private static async Task<List<PayrollRecord>> LoadPayrollRecordsAsync(NpgsqlConnection connection, string schema, Rule26ValidationRequest request, string? payrollNameColumn)
        {
            var selectName = string.IsNullOrWhiteSpace(payrollNameColumn)
                ? "NULL::text"
                : $"\"{payrollNameColumn}\"";

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
SELECT ""{request.PayrollPersonnelColumn}"", {selectName}, ""{request.PayrollEmploymentTypeColumn}"", ""{request.PayrollGenderColumn}"", ""{request.PayrollGroupColumn}"", ""{request.PayrollBirthDateColumn}""
FROM ""{schema}"".""{request.PayrollTable}"";";

            await using var reader = await cmd.ExecuteReaderAsync();
            var records = new List<PayrollRecord>();
            while (await reader.ReadAsync())
            {
                var rawPersonnel = ToInvariantString(reader.GetValue(0));
                records.Add(new PayrollRecord
                {
                    PersonnelNumber = rawPersonnel,
                    PersonnelKey = rawPersonnel,
                    PersonnelName = ToNullableInvariantString(reader.GetValue(1)),
                    EmploymentType = ToNullableInvariantString(reader.GetValue(2)),
                    Gender = ToNullableInvariantString(reader.GetValue(3)),
                    GroupName = ToNullableInvariantString(reader.GetValue(4)),
                    BirthRaw = ToNullableInvariantString(reader.GetValue(5)),
                    BirthDate = NormalizeDate(ToNullableInvariantString(reader.GetValue(5)))
                });
            }

            return records;
        }

        private Rule26DirectionResultViewModel BuildProfToPayrollDirection(
            List<ProfRecord> profRecords,
            Dictionary<string, List<PayrollRecord>> payrollByPersonnel,
            Rule26ValidationRequest request)
        {
            var blankPayrollGroupPassCodes = ParseRule26BlankPayrollGroupPassCodes(request.BlankPayrollGroupPassCodes);
            var direction = new Rule26DirectionResultViewModel
            {
                DirectionKey = "prof_to_payroll",
                DirectionLabel = "dbo_PROF -> Payroll_Sample",
                BaseTable = request.ProfTable,
                ReferenceTable = request.PayrollTable,
                BaseRecordCount = profRecords.Count
            };

            var controls = CreateControlShell();
            var linkedGroups = new List<Rule26LinkedPayrollGroup>();

            foreach (var prof in profRecords)
            {
                if (!string.IsNullOrWhiteSpace(prof.PersonnelKey) &&
                    payrollByPersonnel.TryGetValue(prof.PersonnelKey, out var payrollMatches) &&
                    payrollMatches.Count > 0)
                {
                    linkedGroups.Add(new Rule26LinkedPayrollGroup
                    {
                        Prof = prof,
                        PayrollMatches = payrollMatches
                    });
                }
            }

            direction.LinkedRecordCount = linkedGroups.Count;

            EvaluateLinkedControlsForProfDirection(direction, controls, linkedGroups, blankPayrollGroupPassCodes);
            direction.Controls = controls;
            direction.TotalExceptions = direction.Exceptions.Count;

            var exceptionPersonnelProf = direction.Exceptions
                .Select(e => e.PersonnelNumber)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            direction.PassRows = linkedGroups
                .Where(group => !exceptionPersonnelProf.Contains(group.Prof.PersonnelNumber))
                .GroupBy(group => group.Prof.PersonnelNumber, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(group => new Rule26PassRowViewModel
                {
                    DirectionKey = direction.DirectionKey,
                    DirectionLabel = direction.DirectionLabel,
                    PersonnelNumber = group.Prof.PersonnelNumber,
                    PersonnelName = group.PayrollMatches
                        .Select(payroll => payroll.PersonnelName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                    EmploymentType = group.Prof.EmploymentType,
                    Gender = group.Prof.Gender
                })
                .ToList();

            return direction;
        }

        private static List<Rule26ControlSummaryViewModel> CreateControlShell() =>
        [
            new()
            {
                ControlNumber = 2,
                ControlName = "Employment Type Match",
                Explanation = "Compares the first letter of dbo_PROF _041 to the first letter of Payroll PERMANENT_OR_TEMP for linked personnel."
            },
            new()
            {
                ControlNumber = 3,
                ControlName = "Gender Consistency",
                Explanation = "Compares the first letter of dbo_PROF _012 to the first letter of Payroll GENDER for linked personnel."
            },
            new()
            {
                ControlNumber = 4,
                ControlName = "Race/Group Code Accuracy",
                Explanation = "Compares the first letter of Payroll GROUP_NAME to the first letter of dbo_PROF _013 for linked personnel. Blank Payroll GROUP_NAME is accepted when dbo_PROF _013 is in the configured blank-group pass list."
            },
            new()
            {
                ControlNumber = 5,
                ControlName = "Birth Date Integrity",
                Explanation = "Compares Payroll BIRTH_DATE to dbo_PROF _011 converted from YYYYMMDD for linked personnel."
            }
        ];

        private void EvaluateLinkedControlsForProfDirection(
            Rule26DirectionResultViewModel direction,
            List<Rule26ControlSummaryViewModel> controls,
            List<Rule26LinkedPayrollGroup> linkedGroups,
            HashSet<string> blankPayrollGroupPassCodes)
        {
            foreach (var group in linkedGroups)
            {
                var employmentMismatch = GetRule26FailingPayrollMatch(
                    group.PayrollMatches,
                    payroll => AreSqlComparableValuesDifferent(
                        GetFirstCharacterValue(group.Prof.EmploymentType),
                        GetFirstCharacterValue(payroll.EmploymentType)));
                if (employmentMismatch != null)
                {
                    direction.Exceptions.Add(CreateException(
                        direction, controls[0], group.Prof.PersonnelNumber, employmentMismatch.PersonnelName,
                        "dbo_PROF employment type first letter does not match Payroll_Sample PERMANENT_OR_TEMP first letter.",
                        group.Prof.EmploymentType, employmentMismatch.EmploymentType));
                }

                var genderMismatch = GetRule26FailingPayrollMatch(
                    group.PayrollMatches,
                    payroll => AreSqlComparableValuesDifferent(
                        GetFirstCharacterValue(group.Prof.Gender),
                        GetFirstCharacterValue(payroll.Gender)));
                if (genderMismatch != null)
                {
                    direction.Exceptions.Add(CreateException(
                        direction, controls[1], group.Prof.PersonnelNumber, genderMismatch.PersonnelName,
                        "dbo_PROF gender first letter does not match Payroll_Sample GENDER first letter.",
                        group.Prof.Gender, genderMismatch.Gender));
                }

                var groupMismatch = GetRule26FailingPayrollMatch(
                    group.PayrollMatches,
                    payroll => IsRule26GroupMismatch(group.Prof.GroupCode, payroll.GroupName, blankPayrollGroupPassCodes));
                if (groupMismatch != null)
                {
                    direction.Exceptions.Add(CreateException(
                        direction, controls[2], group.Prof.PersonnelNumber, groupMismatch.PersonnelName,
                        "dbo_PROF race/group code first letter does not match Payroll_Sample GROUP_NAME first letter.",
                        group.Prof.GroupCode, groupMismatch.GroupName));
                }

                if (!string.IsNullOrWhiteSpace(group.Prof.BirthRaw) && !group.Prof.BirthDate.HasValue)
                {
                    var payrollBirthSample = GetPreferredRule26PayrollBirthSample(group.PayrollMatches);
                    direction.Exceptions.Add(CreateException(
                        direction, controls[3], group.Prof.PersonnelNumber, payrollBirthSample?.PersonnelName,
                        "dbo_PROF birth date is not a valid YYYYMMDD value and cannot be matched to Payroll_Sample birth date.",
                        group.Prof.BirthRaw,
                        FormatRule26BirthDisplay(payrollBirthSample)));
                }
                else if (group.Prof.BirthDate.HasValue)
                {
                    var birthMismatch = GetRule26FailingPayrollMatch(
                        group.PayrollMatches,
                        payroll => !payroll.BirthDate.HasValue || group.Prof.BirthDate.Value != payroll.BirthDate.Value);
                    if (birthMismatch != null)
                    {
                        direction.Exceptions.Add(CreateException(
                            direction, controls[3], group.Prof.PersonnelNumber, birthMismatch.PersonnelName,
                            "dbo_PROF birth date does not match Payroll_Sample birth date.",
                            group.Prof.BirthDate.Value.ToString("yyyy-MM-dd"), birthMismatch.BirthDate?.ToString("yyyy-MM-dd") ?? ""));
                    }
                }
            }

            direction.Exceptions = direction.Exceptions
                .GroupBy(e => $"{e.DirectionKey}|{e.ControlNumber}|{e.PersonnelNumber}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            for (var i = 0; i < controls.Count; i++)
            {
                controls[i].TotalTested = direction.LinkedRecordCount;
                controls[i].ExceptionCount = direction.Exceptions.Count(e => e.ControlNumber == controls[i].ControlNumber);
                controls[i].Passed = controls[i].ExceptionCount == 0;
            }
        }

        private static PayrollRecord? GetRule26FailingPayrollMatch(
            IEnumerable<PayrollRecord> payrollMatches,
            Func<PayrollRecord, bool> isMismatch)
        {
            PayrollRecord? firstMismatch = null;
            foreach (var payroll in payrollMatches)
            {
                if (!isMismatch(payroll))
                    return null;

                firstMismatch ??= payroll;
            }

            return firstMismatch;
        }

        private static PayrollRecord? GetPreferredRule26PayrollBirthSample(IEnumerable<PayrollRecord> payrollMatches) =>
            payrollMatches
                .OrderBy(payroll => payroll.BirthDate.HasValue ? 0 : 1)
                .ThenBy(payroll => payroll.BirthRaw)
                .FirstOrDefault();

        private static bool IsRule26GroupMismatch(
            string? profGroupCode,
            string? payrollGroupName,
            HashSet<string> blankPayrollGroupPassCodes)
        {
            var profGroupFirst = GetFirstCharacterValue(profGroupCode);
            var payrollGroupFirst = GetFirstCharacterValue(payrollGroupName);
            var payrollGroupBlank = string.IsNullOrEmpty(NormalizeText(payrollGroupName));

            if (!string.IsNullOrEmpty(profGroupFirst) &&
                blankPayrollGroupPassCodes.Contains(profGroupFirst) &&
                payrollGroupBlank)
                return false;

            return AreSqlComparableValuesDifferent(profGroupFirst, payrollGroupFirst);
        }

        private static string NormalizeRule26BlankPayrollGroupPassCodes(string? value)
        {
            var codes = ParseRule26BlankPayrollGroupPassCodes(value);
            return codes.Count == 0 ? "Z" : string.Join(",", codes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase));
        }

        private static HashSet<string> ParseRule26BlankPayrollGroupPassCodes(string? value)
        {
            var tokens = (value ?? "Z")
                .Split([',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(GetFirstCharacterValue)
                .Where(token => !string.IsNullOrEmpty(token))
                .Select(token => token!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (tokens.Count == 0)
                tokens.Add("Z");

            return tokens;
        }

        private static string BuildRule26SqlCharacterList(string? value) =>
            string.Join(", ", ParseRule26BlankPayrollGroupPassCodes(value)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .Select(code => $"'{EscapeSqlString(code)}'"));

        private static string FormatRule26BirthDisplay(PayrollRecord? payroll) =>
            payroll?.BirthDate?.ToString("yyyy-MM-dd")
            ?? payroll?.BirthRaw
            ?? "";

        private static Rule26ExceptionRowViewModel CreateException(
            Rule26DirectionResultViewModel direction,
            Rule26ControlSummaryViewModel control,
            string personnelNumber,
            string? personnelName,
            string reason,
            string? baseValue,
            string? referenceValue)
        {
            var row = new Rule26ExceptionRowViewModel
            {
                DirectionKey = direction.DirectionKey,
                DirectionLabel = direction.DirectionLabel,
                ControlNumber = control.ControlNumber,
                ControlName = control.ControlName,
                PersonnelNumber = personnelNumber ?? "",
                PersonnelName = personnelName,
                ExceptionReason = reason,
                BaseValue = baseValue ?? "",
                ReferenceValue = referenceValue ?? ""
            };

            row.DisplayValues["Direction"] = row.DirectionLabel;
            row.DisplayValues["Control"] = row.ControlName;
            row.DisplayValues["Personnel Number"] = row.PersonnelNumber;
            row.DisplayValues["Personnel Name"] = row.PersonnelName;
            row.DisplayValues["Exception Reason"] = row.ExceptionReason;
            row.DisplayValues["Base Value"] = row.BaseValue;
            row.DisplayValues["Reference Value"] = row.ReferenceValue;
            return row;
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule26ValidationRequest request, Rule26ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, RuleNumber);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = RuleNumber,
                RuleName = RuleName,
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.ProfTable,
                DeceasedTable = request.PayrollTable,
                StudColumn = request.ProfPersonnelColumn,
                DeceasedColumn = request.PayrollPersonnelColumn,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.Exceptions)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Validation / helpers ─────────────────────────────────────────────────────────

        private static void ValidateVerifyRequest(Rule26VerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ProfTable) || string.IsNullOrWhiteSpace(request.PayrollTable))
                throw new InvalidOperationException("Both source tables are required.");
            if (string.IsNullOrWhiteSpace(request.ProfPersonnelColumn) || string.IsNullOrWhiteSpace(request.PayrollPersonnelColumn))
                throw new InvalidOperationException("Both personnel columns are required.");

            ValidateObjectName(request.ProfTable);
            ValidateObjectName(request.PayrollTable);
            ValidateObjectName(request.ProfPersonnelColumn);
            ValidateObjectName(request.PayrollPersonnelColumn);
        }

        private static void ValidateRequest(Rule26ValidationRequest request)
        {
            request.BlankPayrollGroupPassCodes = NormalizeRule26BlankPayrollGroupPassCodes(request.BlankPayrollGroupPassCodes);

            var values = new[]
            {
                request.ProfTable, request.PayrollTable,
                request.ProfPersonnelColumn, request.ProfEmploymentTypeColumn, request.ProfGenderColumn, request.ProfGroupColumn, request.ProfBirthDateColumn,
                request.PayrollPersonnelColumn, request.PayrollEmploymentTypeColumn, request.PayrollGenderColumn, request.PayrollGroupColumn, request.PayrollBirthDateColumn
            };

            if (values.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Select both tables and all ten required columns before running Rule 26.");

            foreach (var value in values)
                ValidateObjectName(value!);
        }

        private static void ValidateObjectName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Contains(';') || value.Contains("--") || value.Contains("/*") || value.Contains("*/"))
            {
                throw new InvalidOperationException("An invalid table or column name was supplied.");
            }
        }

        private static string? ToNullableInvariantString(object? value)
        {
            if (value == null || value == DBNull.Value) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        }

        private static string ToInvariantString(object? value) => ToNullableInvariantString(value) ?? "";

        private static string? NormalizeText(string? value) => value?.Trim().ToUpperInvariant();

        private static string? GetFirstCharacterValue(string? value)
        {
            var normalized = NormalizeText(value);
            return string.IsNullOrEmpty(normalized) ? string.Empty : normalized[..1];
        }

        private static bool AreSqlComparableValuesDifferent(string? left, string? right)
        {
            if (left == null || right == null) return false;
            return !string.Equals(left, right, StringComparison.Ordinal);
        }

        private static DateTime? ConvertProfBirthDate(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length != 8 || !trimmed.All(char.IsDigit)) return null;

            return DateTime.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt.Date
                : null;
        }

        private static DateTime? NormalizeDate(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return null;

            return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt.Date
                : null;
        }

        private static string BuildProfBirthSqlExpression(string trimmedExpression) =>
            $@"CASE
    WHEN {trimmedExpression} !~ '^[1-2][0-9]{{3}}[0-1][0-9][0-3][0-9]$' THEN NULL
    ELSE TO_DATE({trimmedExpression}, 'YYYYMMDD')
END";

        private static string EscapeSqlString(string? value) => (value ?? "").Replace("'", "''");

        private static string? FindFirst(IEnumerable<string> values, IEnumerable<string> preferredExact, IEnumerable<string> preferredContains)
        {
            var list = values.ToList();
            foreach (var exact in preferredExact)
            {
                var match = list.FirstOrDefault(value => value.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            foreach (var contains in preferredContains)
            {
                var match = list.FirstOrDefault(value => value.Contains(contains, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return list.FirstOrDefault();
        }

        private static Rule26ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule26ValidationSummary>(decoded);
            }
            catch { return null; }
        }

        private static void ApplyBrowserPreview(Rule26ValidationSummary summary)
        {
            summary.Exceptions = summary.Exceptions.Take(BrowserPreviewRowLimit).ToList();
            summary.PassRows = summary.PassRows.Take(BrowserPreviewRowLimit).ToList();
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

        private sealed class ProfRecord
        {
            public string PersonnelNumber { get; set; } = "";
            public string PersonnelKey { get; set; } = "";
            public string? EmploymentType { get; set; }
            public string? Gender { get; set; }
            public string? GroupCode { get; set; }
            public string? BirthRaw { get; set; }
            public DateTime? BirthDate { get; set; }
        }

        private sealed class PayrollRecord
        {
            public string PersonnelNumber { get; set; } = "";
            public string PersonnelKey { get; set; } = "";
            public string? PersonnelName { get; set; }
            public string? EmploymentType { get; set; }
            public string? Gender { get; set; }
            public string? GroupName { get; set; }
            public string? BirthRaw { get; set; }
            public DateTime? BirthDate { get; set; }
        }

        private sealed class Rule26LinkedPayrollGroup
        {
            public ProfRecord Prof { get; set; } = new();
            public List<PayrollRecord> PayrollMatches { get; set; } = new();
        }
    }
}
