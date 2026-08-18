using System.Globalization;
using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 68: Credit Overload Validation — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. For every student+qualification pair
    // in CREG, sums the credits of each registered course (via CRED) and fails when the total
    // exceeds a configurable threshold (default 1.0 — courses carry fractional credit weights that
    // should sum to a full qualification load). An optional detail table (Rule 32 error-detail
    // export) can be cross-referenced to confirm FAIL findings.
    public class Rule68Service : IRule68Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const long TargetErrorCode = 3603L; // 03603
        private static readonly long[] DefaultExclusionCodes = { 2202L, 2301L, 2302L, 708L, 7201L, 1501L };

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule68Service(
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

        public async Task<Rule68TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule68TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule68TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD"], ["stud"]),
                    AutoCregTable = FindFirst(tables, ["dbo_CREG"], ["creg"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL"], ["qual"]),
                    AutoCredTable = FindFirst(tables, ["dbo_CRED"], ["cred"]),
                    AutoCrseTable = FindFirst(tables, ["dbo_CRSE"], ["crse"]),
                    AutoDetailTable = FindFirst(tables, ["dbo_STUD_VALIDATION_DETAIL"], ["stud_validation", "validation_detail"])
                };
            }
            catch (Exception ex) { return new Rule68TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule68ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "creg_student_no" => FindFirst(cols, ["_007"], []),
                    "creg_qual" => FindFirst(cols, ["_001"], []),
                    "creg_course" => FindFirst(cols, ["_030"], []),
                    "qual_qual" => FindFirst(cols, ["_001"], []),
                    "qual_name" => FindFirst(cols, ["_003"], []),
                    "cred_qual" => FindFirst(cols, ["_001"], []),
                    "cred_course" => FindFirst(cols, ["_030"], []),
                    "cred_credits" => FindFirst(cols, ["_036"], []),
                    "crse_course" => FindFirst(cols, ["_030"], []),
                    "crse_name" => FindFirst(cols, ["_058"], []),
                    "detail_error" => FindFirst(cols, ["Error"], ["error"]),
                    "detail_errortype" => FindFirst(cols, ["ErrorType", "Error_Type"], ["errortype", "error_type"]),
                    "detail_elementinfo" => FindFirst(cols, ["Element_Information"], ["element_information", "element"]),
                    _ => null
                };
                return new Rule68ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule68ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule68VerifyResult> VerifyTablesAsync(Rule68ValidationRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.CregTable, [request.CregStudNoCol, request.CregQualCol, request.CregCourseCol]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                return new Rule68VerifyResult
                {
                    Success = true,
                    StudRecordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.StudTable)}\";"),
                    CregRecordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CregTable)}\";"),
                    QualRecordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.QualTable)}\";"),
                    CredRecordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CredTable)}\";"),
                    CrseRecordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CrseTable)}\";")
                };
            }
            catch (Exception ex) { return new Rule68VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule68ValidationSummary> RunValidationAsync(Rule68ValidationRequest request, string? userEmail = null, string? userName = null)
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

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex) { return new Rule68ValidationSummary { Success = false, Error = ex.Message }; }
        }

        private async Task<Rule68ValidationSummary> AnalyseAsync(Rule68ValidationRequest request)
        {
            var hasDetail = !string.IsNullOrWhiteSpace(request.DetailTable);
            var hasErrorTypeFilter = hasDetail && !string.IsNullOrWhiteSpace(request.DetailErrorTypeCol);

            await ValidateColumnsExistAsync(request.ClientId, request.CregTable, [request.CregStudNoCol, request.CregQualCol, request.CregCourseCol]);
            await ValidateColumnsExistAsync(request.ClientId, request.QualTable, [request.QualQualCol, request.QualNameCol]);
            await ValidateColumnsExistAsync(request.ClientId, request.CredTable, [request.CredQualCol, request.CredCourseCol, request.CredCreditsCol]);
            await ValidateColumnsExistAsync(request.ClientId, request.CrseTable, [request.CrseCourseCol, request.CrseNameCol]);
            if (hasDetail)
            {
                var detailCols = new List<string> { request.DetailErrorCol, request.DetailElementInfoCol };
                if (hasErrorTypeFilter) detailCols.Add(request.DetailErrorTypeCol);
                await ValidateColumnsExistAsync(request.ClientId, request.DetailTable, detailCols);
            }

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var maxTotalCredits = NormalizeThreshold(request.MaxTotalCredits);
            var thresholdText = maxTotalCredits.ToString(CultureInfo.InvariantCulture);
            var exclusionCodes = ParseExclusionCodes(request.DetailExclusionCodes);

            var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.StudTable)}\";");
            var cregCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CregTable)}\";");
            var qualCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.QualTable)}\";");
            var credCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CredTable)}\";");
            var crseCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CrseTable)}\";");
            var detailCount = 0;
            if (hasDetail)
            {
                detailCount = await CountAsync(connection, $@"
SELECT COUNT(*) FROM ""{schema}"".""{Sanitise(request.DetailTable)}""
WHERE (CASE WHEN TRIM(CAST(""{Sanitise(request.DetailErrorCol)}"" AS text)) ~ '^[0-9]+$' THEN TRIM(CAST(""{Sanitise(request.DetailErrorCol)}"" AS text))::bigint END) = {TargetErrorCode};");
            }

            var ctes = BuildValidationCtes(schema, request, exclusionCodes, hasDetail, hasErrorTypeFilter, thresholdText);

            int totalValidated = 0, passCount = 0, failCount = 0, confirmedByRule32Count = 0, notInRule32Count = 0, rule32OnlyCountTotal = 0;
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = $@"
WITH {ctes}
SELECT
    COUNT(*) AS total_validated,
    COUNT(*) FILTER (WHERE validation_result = 'PASS') AS pass_count,
    COUNT(*) FILTER (WHERE validation_result = 'FAIL') AS fail_count,
    COUNT(*) FILTER (WHERE reconciliation_status = 'Confirmed by Rule 32') AS confirmed_by_rule32,
    COUNT(*) FILTER (WHERE reconciliation_status = 'Not in Rule 32') AS not_in_rule32,
    (SELECT COUNT(*) FROM detail_pairs) AS rule32_only_total
FROM results;";
                await using var reader = await countCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    totalValidated = Convert.ToInt32(reader.GetValue(0));
                    passCount = Convert.ToInt32(reader.GetValue(1));
                    failCount = Convert.ToInt32(reader.GetValue(2));
                    confirmedByRule32Count = Convert.ToInt32(reader.GetValue(3));
                    notInRule32Count = Convert.ToInt32(reader.GetValue(4));
                    rule32OnlyCountTotal = Convert.ToInt32(reader.GetValue(5));
                }
            }

            var reviewRows = new List<Rule68ValidationRowRecord>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
WITH {ctes}
SELECT student_no, qual_code, qual_name, course_count, total_credits, validation_result, error_code, reconciliation_status
FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, total_credits DESC, student_no, qual_code;";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var validationResult = GetString(reader, 5) ?? "FAIL";
                    var studentNo = GetString(reader, 0) ?? "";
                    var qualCode = GetString(reader, 1) ?? "";
                    var totalCredits = GetString(reader, 4) ?? "0";
                    var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Student_No"] = studentNo,
                        ["Qual_Code"] = qualCode,
                        ["Qual_Name"] = GetString(reader, 2),
                        ["Course_Count"] = GetString(reader, 3),
                        ["Total_Credits"] = totalCredits
                    };
                    var isPass = string.Equals(validationResult, "PASS", StringComparison.OrdinalIgnoreCase);
                    var explanation = isPass
                        ? $"PASS: Total credits ({totalCredits}) is within the {thresholdText} limit for qualification {qualCode}."
                        : $"FAIL (03603): Total credits ({totalCredits}) exceed {thresholdText} for student {studentNo} / qualification {qualCode}.";
                    displayValues["Validation_Explanation"] = explanation;

                    reviewRows.Add(new Rule68ValidationRowRecord
                    {
                        ValidationNumber = reviewRows.Count + 1,
                        ValidationResult = validationResult,
                        ErrorCode = GetString(reader, 6) ?? "",
                        ReconciliationStatus = GetString(reader, 7) ?? "",
                        ValidationExplanation = explanation,
                        DisplayValues = displayValues
                    });
                }
            }

            var rule32OnlyRows = new List<Rule68Rule32OnlyRow>();
            if (hasDetail)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
WITH {ctes}
SELECT dp.detail_stud_no, dp.detail_qual,
       CASE WHEN fr.student_no IS NOT NULL THEN 'Yes' ELSE 'No' END AS confirmed_by_r68
FROM detail_pairs dp
LEFT JOIN (SELECT DISTINCT student_no, qual_code FROM results WHERE validation_result = 'FAIL') fr
    ON fr.student_no = dp.detail_stud_no AND fr.qual_code = dp.detail_qual
ORDER BY dp.detail_stud_no, dp.detail_qual;";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rule32OnlyRows.Add(new Rule68Rule32OnlyRow
                    {
                        RowNumber = rule32OnlyRows.Count + 1,
                        StudentNo = GetString(reader, 0) ?? "",
                        QualCode = GetString(reader, 1) ?? "",
                        ConfirmedByR68 = GetString(reader, 2) ?? "No"
                    });
                }
            }

            var r32ConfirmedTotal = rule32OnlyRows.Count(r => string.Equals(r.ConfirmedByR68, "Yes", StringComparison.OrdinalIgnoreCase));
            var r32NotInCregTotal = rule32OnlyRows.Count(r => string.Equals(r.ConfirmedByR68, "Not in CREG", StringComparison.OrdinalIgnoreCase));
            var exRate = totalValidated == 0 ? 0m : Math.Round(failCount * 100m / totalValidated, 2);

            return new Rule68ValidationSummary
            {
                Success = true,
                StudRecordCount = studCount,
                CregRecordCount = cregCount,
                QualRecordCount = qualCount,
                CredRecordCount = credCount,
                CrseRecordCount = crseCount,
                DetailRecordCount = detailCount,
                TotalValidated = totalValidated,
                PassCount = passCount,
                FailCount = failCount,
                DisplayedCount = reviewRows.Count,
                ConfirmedByRule32Count = confirmedByRule32Count,
                NotInRule32Count = notInRule32Count,
                Rule32OnlyCount = rule32OnlyCountTotal,
                Rule32ConfirmedByR68Count = r32ConfirmedTotal,
                Rule32NotInCregCount = r32NotInCregTotal,
                ExceptionRate = exRate,
                Status = failCount == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable = request.StudTable,
                CregTable = request.CregTable,
                QualTable = request.QualTable,
                CredTable = request.CredTable,
                CrseTable = request.CrseTable,
                DetailTable = hasDetail ? request.DetailTable : "",
                CregStudNoCol = request.CregStudNoCol,
                CregQualCol = request.CregQualCol,
                CregCourseCol = request.CregCourseCol,
                QualQualCol = request.QualQualCol,
                QualNameCol = request.QualNameCol,
                CredQualCol = request.CredQualCol,
                CredCourseCol = request.CredCourseCol,
                CredCreditsCol = request.CredCreditsCol,
                CrseCourseCol = request.CrseCourseCol,
                CrseNameCol = request.CrseNameCol,
                MaxTotalCredits = maxTotalCredits,
                DetailErrorTypeCol = request.DetailErrorTypeCol,
                DetailErrorCol = request.DetailErrorCol,
                DetailErrorTypeValue = request.DetailErrorTypeValue,
                DetailExclusionCodes = exclusionCodes.Count > 0 ? string.Join(",", exclusionCodes) : "",
                DetailElementInfoCol = request.DetailElementInfoCol,
                TableLinkageText = $"{request.CregTable}.[{request.CregStudNoCol}]+[{request.CregQualCol}] -> CRED.[{request.CredCreditsCol}] (SUM > {thresholdText} = error 03603)",
                RuleModeText = $"Sum of CRED.[{request.CredCreditsCol}] per student-qualification must be <= {thresholdText}",
                ProcedureSteps = new List<string>
                {
                    $"Extract all student-course registrations from {request.CregTable} using [{request.CregStudNoCol}] (student) and [{request.CregQualCol}] (qualification).",
                    $"Join to {request.CredTable}.[{request.CredCreditsCol}] via [{request.CredCourseCol}] to get the credit value per course.",
                    $"Also join {request.QualTable} for qualification names and {request.CrseTable} for course names.",
                    $"Group by (Student_No, Qual_Code) and compute SUM([{request.CredCreditsCol}]).",
                    $"If SUM > {thresholdText} -> FAIL (error code 03603). Otherwise PASS.",
                    hasDetail ? $"Reconciliation: cross-reference FAIL results against {request.DetailTable} (error 03603) to confirm findings." : ""
                }.Where(s => !string.IsNullOrEmpty(s)).ToList(),
                ClientId = request.ClientId,
                ReviewRows = reviewRows,
                Rule32OnlyRows = rule32OnlyRows,
                Warning = null
            };
        }

        // ── SQL builders ──────────────────────────────────────────────────────

        // Returns only the CTE definitions (no trailing SELECT) so callers can each append their
        // own final SELECT against "results"/"detail_pairs" without illegal nested-WITH syntax.
        private static string BuildValidationCtes(
            string schema, Rule68ValidationRequest request, IReadOnlyList<long> exclusionCodes,
            bool hasDetail, bool hasErrorTypeFilter, string thresholdText)
        {
            var cg = Sanitise(request.CregTable);
            var q = Sanitise(request.QualTable);
            var cr = Sanitise(request.CredTable);
            var cs = Sanitise(request.CrseTable);

            var detailPairsCte = hasDetail
                ? BuildDetailPairsCte(schema, request, exclusionCodes, hasErrorTypeFilter)
                : "detail_pairs AS (SELECT NULL::text AS detail_stud_no, NULL::text AS detail_qual WHERE FALSE)";

            var reconExpr = hasDetail
                ? @"CASE WHEN validation_result = 'PASS' THEN ''
             WHEN EXISTS (SELECT 1 FROM detail_pairs dp WHERE dp.detail_stud_no = base_agg.student_no AND dp.detail_qual = base_agg.qual_code) THEN 'Confirmed by Rule 32'
             ELSE 'Not in Rule 32' END"
                : "''";

            return $@"
base AS (
    SELECT
        TRIM(CAST(cg.""{request.CregStudNoCol}"" AS text)) AS student_no,
        UPPER(TRIM(CAST(cg.""{request.CregQualCol}"" AS text))) AS qual_code,
        UPPER(TRIM(CAST(cg.""{request.CregCourseCol}"" AS text))) AS course_code,
        COALESCE(
            CASE WHEN TRIM(CAST(cr.""{request.CredCreditsCol}"" AS text)) ~ '^-?[0-9]+(\.[0-9]+)?$'
                 THEN TRIM(CAST(cr.""{request.CredCreditsCol}"" AS text))::numeric
                 ELSE NULL END,
            0) AS credits,
        COALESCE(TRIM(CAST(cs.""{request.CrseNameCol}"" AS text)), '') AS course_name,
        COALESCE(TRIM(CAST(q.""{request.QualNameCol}"" AS text)), '') AS qual_name
    FROM ""{schema}"".""{cg}"" cg
    LEFT JOIN ""{schema}"".""{q}"" q
        ON UPPER(TRIM(CAST(q.""{request.QualQualCol}"" AS text))) = UPPER(TRIM(CAST(cg.""{request.CregQualCol}"" AS text)))
    LEFT JOIN ""{schema}"".""{cr}"" cr
        ON UPPER(TRIM(CAST(cr.""{request.CredCourseCol}"" AS text))) = UPPER(TRIM(CAST(cg.""{request.CregCourseCol}"" AS text)))
       AND UPPER(TRIM(CAST(cr.""{request.CredQualCol}"" AS text))) = UPPER(TRIM(CAST(cg.""{request.CregQualCol}"" AS text)))
    LEFT JOIN ""{schema}"".""{cs}"" cs
        ON UPPER(TRIM(CAST(cs.""{request.CrseCourseCol}"" AS text))) = UPPER(TRIM(CAST(cg.""{request.CregCourseCol}"" AS text)))
    WHERE TRIM(CAST(cg.""{request.CregStudNoCol}"" AS text)) <> ''
      AND cg.""{request.CregQualCol}"" IS NOT NULL
),
base_agg AS (
    SELECT
        student_no, qual_code, MAX(qual_name) AS qual_name,
        COUNT(DISTINCT course_code) AS course_count,
        SUM(credits) AS total_credits,
        CASE WHEN SUM(credits) > {thresholdText} THEN 'FAIL' ELSE 'PASS' END AS validation_result,
        CASE WHEN SUM(credits) > {thresholdText} THEN '03603' ELSE '' END AS error_code
    FROM base
    GROUP BY student_no, qual_code
),
{detailPairsCte},
results AS (
    SELECT
        student_no, qual_code, qual_name, course_count, total_credits, validation_result, error_code,
        {reconExpr} AS reconciliation_status
    FROM base_agg
)";
        }

        private static string BuildDetailPairsCte(string schema, Rule68ValidationRequest request, IReadOnlyList<long> exclusionCodes, bool hasErrorTypeFilter)
        {
            var dt = Sanitise(request.DetailTable);
            var errorTypeSelect = hasErrorTypeFilter ? $@", TRIM(CAST(d.""{Sanitise(request.DetailErrorTypeCol)}"" AS text)) AS err_type_raw" : "";
            var errorTypeFilter = hasErrorTypeFilter
                ? $"AND UPPER(err_type_raw) = UPPER('{request.DetailErrorTypeValue.Replace("'", "''")}')"
                : "";
            var exclusionFilter = exclusionCodes.Count > 0
                ? $"AND (CASE WHEN err_raw ~ '^[0-9]+$' THEN err_raw::bigint END) NOT IN ({string.Join(",", exclusionCodes)})"
                : "";

            return $@"detail_parsed AS (
    SELECT
        TRIM(SUBSTRING(el FROM POSITION('E007:' IN el) + 5 FOR GREATEST(POSITION('E001:' IN el) - POSITION('E007:' IN el) - 5, 0))) AS raw_stud_no,
        UPPER(TRIM(
            CASE WHEN POSITION('Sum E036:' IN el) > 0
                 THEN SUBSTRING(el FROM POSITION('E001:' IN el) + 5 FOR GREATEST(POSITION('Sum E036:' IN el) - POSITION('E001:' IN el) - 5, 0))
                 ELSE SUBSTRING(el FROM POSITION('E001:' IN el) + 5 FOR 50)
            END
        )) AS raw_qual
    FROM (
        SELECT TRIM(CAST(d.""{request.DetailElementInfoCol}"" AS text)) AS el, TRIM(CAST(d.""{request.DetailErrorCol}"" AS text)) AS err_raw{errorTypeSelect}
        FROM ""{schema}"".""{dt}"" d
    ) x
    WHERE POSITION('E007:' IN el) > 0 AND POSITION('E001:' IN el) > 0
      AND (CASE WHEN err_raw ~ '^[0-9]+$' THEN err_raw::bigint END) = {TargetErrorCode}
      {errorTypeFilter}
      {exclusionFilter}
),
detail_pairs AS (
    SELECT raw_stud_no AS detail_stud_no, raw_qual AS detail_qual
    FROM detail_parsed
    WHERE raw_stud_no <> '' AND raw_qual <> ''
    GROUP BY raw_stud_no, raw_qual
)";
        }

        public string GenerateSql(Rule68ValidationRequest request)
        {
            var maxTotalCredits = NormalizeThreshold(request.MaxTotalCredits);
            var thresholdText = maxTotalCredits.ToString(CultureInfo.InvariantCulture);
            var hasDetail = !string.IsNullOrWhiteSpace(request.DetailTable);
            var hasErrorTypeFilter = hasDetail && !string.IsNullOrWhiteSpace(request.DetailErrorTypeCol);
            var exclusionCodes = ParseExclusionCodes(request.DetailExclusionCodes);
            var ctes = BuildValidationCtes("{schema}", request, exclusionCodes, hasDetail, hasErrorTypeFilter, thresholdText);

            return $@"-- ============================================================
-- HEMIS RULE 68 - Credit Overload Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Check     : SUM of CRED.[{request.CredCreditsCol}] per student/qualification must be <= {thresholdText}
-- Error code 03603 when total credits exceed {thresholdText}
-- Tables    : {Sanitise(request.StudTable)}, {Sanitise(request.CregTable)}, {Sanitise(request.QualTable)}, {Sanitise(request.CredTable)}, {Sanitise(request.CrseTable)}
-- ============================================================
WITH {ctes}
SELECT * FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, total_credits DESC, student_no, qual_code;".Trim();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule68ValidationRequest request, Rule68ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 68);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 68,
                RuleName = "Credit Overload Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.CregTable,
                DeceasedTable = request.StudTable,
                StudColumn = request.CregStudNoCol,
                DeceasedColumn = request.CregQualCol,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(
                    summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList())),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule68WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 68);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);

            var workspace = new Rule68WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                CregTable = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_CREG" : row.StudTable,
                StudTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_STUD" : row.DeceasedTable,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (summary != null)
            {
                workspace.CurrentStatus = summary.Status;
                workspace.CregTable = summary.CregTable;
                workspace.StudTable = summary.StudTable;
                workspace.QualTable = summary.QualTable;
                workspace.CredTable = summary.CredTable;
                workspace.CrseTable = summary.CrseTable;
                workspace.DetailTable = summary.DetailTable;
                workspace.CregStudNoCol = summary.CregStudNoCol;
                workspace.CregQualCol = summary.CregQualCol;
                workspace.CregCourseCol = summary.CregCourseCol;
                workspace.QualQualCol = summary.QualQualCol;
                workspace.QualNameCol = summary.QualNameCol;
                workspace.CredQualCol = summary.CredQualCol;
                workspace.CredCourseCol = summary.CredCourseCol;
                workspace.CredCreditsCol = summary.CredCreditsCol;
                workspace.CrseCourseCol = summary.CrseCourseCol;
                workspace.CrseNameCol = summary.CrseNameCol;
                workspace.MaxTotalCredits = summary.MaxTotalCredits;
                workspace.DetailErrorTypeCol = summary.DetailErrorTypeCol;
                workspace.DetailErrorCol = summary.DetailErrorCol;
                workspace.DetailErrorTypeValue = summary.DetailErrorTypeValue;
                workspace.DetailExclusionCodes = summary.DetailExclusionCodes;
                workspace.DetailElementInfoCol = summary.DetailElementInfoCol;
            }

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

            if (workspace.Summary != null)
            {
                workspace.Summary.SavedRunId = workspace.RunId;
                ApplyBrowserPreview(workspace.Summary);
            }
            return workspace;
        }

        public async Task<Rule68RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 68);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary.SavedRunId = runId;

            var viewModel = new Rule68RunReviewViewModel
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
            viewModel.GeneratedSql = GenerateSql(new Rule68ValidationRequest
            {
                ClientId = viewModel.ClientId,
                StudTable = summary.StudTable,
                CregTable = summary.CregTable,
                QualTable = summary.QualTable,
                CredTable = summary.CredTable,
                CrseTable = summary.CrseTable,
                DetailTable = summary.DetailTable,
                CregStudNoCol = summary.CregStudNoCol,
                CregQualCol = summary.CregQualCol,
                CregCourseCol = summary.CregCourseCol,
                QualQualCol = summary.QualQualCol,
                QualNameCol = summary.QualNameCol,
                CredQualCol = summary.CredQualCol,
                CredCourseCol = summary.CredCourseCol,
                CredCreditsCol = summary.CredCreditsCol,
                CrseCourseCol = summary.CrseCourseCol,
                CrseNameCol = summary.CrseNameCol,
                MaxTotalCredits = summary.MaxTotalCredits,
                DetailErrorTypeCol = summary.DetailErrorTypeCol,
                DetailErrorCol = summary.DetailErrorCol,
                DetailErrorTypeValue = summary.DetailErrorTypeValue,
                DetailExclusionCodes = summary.DetailExclusionCodes,
                DetailElementInfoCol = summary.DetailElementInfoCol
            });

            ApplyBrowserPreview(summary);
            return viewModel;
        }

        public async Task<Rule68WorkspaceSaveResult> SaveWorkspaceAsync(Rule68ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule68WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule68WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.CregTable,
                    DeceasedTable = request.StudTable,
                    StudColumn = request.CregStudNoCol,
                    DeceasedColumn = request.CregQualCol
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule68WorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new Rule68WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule68WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule68WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule68WorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new Rule68WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        public async Task<Rule68ValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 68);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static void ApplyBrowserPreview(Rule68ValidationSummary summary)
        {
            if (summary.Rule32OnlyRows.Count > BrowserPreviewRowLimit)
                summary.Rule32OnlyRows = summary.Rule32OnlyRows.Take(BrowserPreviewRowLimit).ToList();

            var rows = summary.ReviewRows;
            if (rows.Count <= BrowserPreviewRowLimit)
            {
                summary.DisplayedCount = rows.Count;
                summary.IsPreviewOnly = false;
                summary.PreviewLimit = 0;
                return;
            }

            var failRows = rows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.ValidationNumber).ToList();
            var passRows = rows.Where(r => !string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.ValidationNumber).ToList();

            var halfLimit = BrowserPreviewRowLimit / 2;
            var failTake = Math.Min(failRows.Count, passRows.Count > 0 ? halfLimit : BrowserPreviewRowLimit);
            var passTake = Math.Min(passRows.Count, BrowserPreviewRowLimit - failTake);

            var preview = failRows.Take(failTake).Concat(passRows.Take(passTake)).ToList();
            summary.ReviewRows = preview;
            summary.DisplayedCount = preview.Count;
            summary.IsPreviewOnly = summary.TotalValidated > preview.Count;
            summary.PreviewLimit = preview.Count;
        }

        private static decimal NormalizeThreshold(decimal value) => value < 0m ? 0m : value;

        private static IReadOnlyList<long> ParseExclusionCodes(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DefaultExclusionCodes;
            var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => { var t = s.TrimStart('0'); return long.TryParse(t == "" ? "0" : t, out var n) ? n : (long?)null; })
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .Distinct()
                .ToList();
            return parsed.Count > 0 ? parsed : DefaultExclusionCodes;
        }

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

        private static Rule68ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule68ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
