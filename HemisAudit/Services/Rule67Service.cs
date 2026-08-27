using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 67: CREG-STUD Pair Validation — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. Every distinct CREG student+qual
    // pair must exist in STUD, and CREG's E051 column must match a configurable filter (blank =
    // all). A student number missing from STUD entirely is flagged as a "ghost student". An
    // optional detail table (Rule 29 error-detail export) can be cross-referenced to confirm
    // FAIL findings — this reconciliation is skipped when no detail table is configured.
    public class Rule67Service : IRule67Service
    {
        private const int BrowserPreviewRowLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule67Service(
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

        public async Task<Rule67TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule67TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule67TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoCregTable = FindFirst(tables, ["dbo_CREG", "CREG"], ["creg"]),
                    AutoStudTable = FindFirst(tables, ["dbo_STUD_VALPAC", "STUD_VALPAC", "dbo_STUD"], ["stud_valpac", "stud"]),
                    AutoDetailTable = FindFirst(tables, ["dbo_CREG_VALIDATION_DETAIL"], ["validation_detail", "detail"])
                };
            }
            catch (Exception ex) { return new Rule67TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule67ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "creg_student_no"    => FindFirst(cols, ["_007"], []),
                    "creg_qual"          => FindFirst(cols, ["_001"], []),
                    "creg_e051"          => FindFirst(cols, ["_051"], []),
                    "stud_student_no"    => FindFirst(cols, ["_007"], []),
                    "stud_qual"          => FindFirst(cols, ["_001"], []),
                    "detail_error"       => FindFirst(cols, ["Error"], ["error"]),
                    "detail_elementinfo" => FindFirst(cols, ["Element_Information"], ["element_information", "element"]),
                    _ => null
                };
                return new Rule67ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule67ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule67VerifyResult> VerifyTablesAsync(Rule67ValidationRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.CregTable, [request.CregStudentNoCol, request.CregQualCol]);
                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, [request.StudStudentNoCol, request.StudQualCol]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var cregCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CregTable)}\";");
                var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.StudTable)}\";");

                return new Rule67VerifyResult { Success = true, CregRecordCount = cregCount, StudRecordCount = studCount };
            }
            catch (Exception ex) { return new Rule67VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule67ValidationSummary> RunValidationAsync(Rule67ValidationRequest request, string? userEmail = null, string? userName = null)
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
            catch (Exception ex) { return new Rule67ValidationSummary { Success = false, Error = ex.Message }; }
        }

        // AnalyseAsync has no row cap - it always computes the full unbounded population, so this
        // is just a named entry point for export callers (no re-save, no browser preview trim).
        public async Task<Rule67ValidationSummary> GetExportSummaryAsync(Rule67ValidationRequest request)
            => await AnalyseAsync(request);

        // No cheap COUNT-only SQL path exists that's separable from the full analysis (the pass/
        // fail determination depends on the STUD match join and optional Rule 29 reconciliation) -
        // reusing the full export for the population count is the established pattern for rules
        // like this (see Rule34/Rule38).
        public async Task<int> GetPopulationCountAsync(Rule67ValidationRequest request)
        {
            var summary = await GetExportSummaryAsync(request);
            return summary.TotalValidated;
        }

        private async Task<Rule67ValidationSummary> AnalyseAsync(Rule67ValidationRequest request)
        {
            var hasDetail = !string.IsNullOrWhiteSpace(request.DetailTable);
            var requiredCreg = new List<string> { request.CregStudentNoCol, request.CregQualCol, request.CregE051Col };
            await ValidateColumnsExistAsync(request.ClientId, request.CregTable, requiredCreg);
            await ValidateColumnsExistAsync(request.ClientId, request.StudTable, [request.StudStudentNoCol, request.StudQualCol]);
            if (hasDetail)
                await ValidateColumnsExistAsync(request.ClientId, request.DetailTable, [request.DetailErrorCol, request.DetailElementInfoCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var cregStudentCol = Sanitise(request.CregStudentNoCol);
            var cregQualCol = Sanitise(request.CregQualCol);
            var cregE051Col = Sanitise(string.IsNullOrWhiteSpace(request.CregE051Col) ? "_051" : request.CregE051Col);
            var studStudentCol = Sanitise(request.StudStudentNoCol);
            var studQualCol = Sanitise(request.StudQualCol);
            var e051Values = ParseFilterValues(request.E051FilterValues);
            var e051ValuesText = e051Values.Count > 0 ? string.Join(", ", e051Values) : "ALL — no filter applied";
            var detailErrCodes = ParseDetailErrorCodes(request.DetailErrorCode);

            var cregCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.CregTable)}\";");
            var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(request.StudTable)}\";");
            var detailCount = 0;
            if (hasDetail)
            {
                var detailErrInSql = BuildDetailErrInSql(detailErrCodes);
                var detailErrCol = Sanitise(request.DetailErrorCol);
                detailCount = await CountAsync(connection, $@"
SELECT COUNT(*) FROM ""{schema}"".""{Sanitise(request.DetailTable)}""
WHERE (CASE WHEN TRIM(CAST(""{detailErrCol}"" AS text)) ~ '^[0-9]+$' THEN TRIM(CAST(""{detailErrCol}"" AS text))::bigint END) IN ({detailErrInSql});");
            }

            var ctes = BuildValidationCtes(schema, request, cregStudentCol, cregQualCol, cregE051Col, studStudentCol, studQualCol, e051Values, hasDetail, detailErrCodes);

            int totalChecked = 0, passCount = 0, notInStudCount = 0, invalidE051Count = 0, notInStudE051ValidCount = 0, notInStudE051InvalidCount = 0, ghostStudentCount = 0;
            int confirmedByRule29Count = 0, notInRule29Count = 0, rule29OnlyCountTotal = 0;
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = $@"
WITH {ctes}
SELECT
    COUNT(*) AS total_checked,
    COUNT(*) FILTER (WHERE validation_result = 'PASS') AS pass_count,
    COUNT(*) FILTER (WHERE fail_reason = 'Not found in STUD') AS not_in_stud_count,
    COUNT(*) FILTER (WHERE fail_reason = 'E051 code not in expected values') AS invalid_e051_count,
    COUNT(*) FILTER (WHERE fail_reason = 'Not found in STUD' AND e051_valid = 'Yes') AS not_in_stud_e051_valid_count,
    COUNT(*) FILTER (WHERE fail_reason = 'Not found in STUD' AND e051_valid = 'No') AS not_in_stud_e051_invalid_count,
    COUNT(*) FILTER (WHERE fail_reason = 'Not found in STUD' AND ghost_student = 'Yes') AS ghost_student_count,
    COUNT(*) FILTER (WHERE reconciliation_status = 'Confirmed by Rule 29') AS confirmed_by_rule29_count,
    COUNT(*) FILTER (WHERE reconciliation_status = 'Not in Rule 29') AS not_in_rule29_count,
    (SELECT COUNT(*) FROM detail_pairs) AS rule29_only_total
FROM results;";
                await using var reader = await countCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    totalChecked = Convert.ToInt32(reader.GetValue(0));
                    passCount = Convert.ToInt32(reader.GetValue(1));
                    notInStudCount = Convert.ToInt32(reader.GetValue(2));
                    invalidE051Count = Convert.ToInt32(reader.GetValue(3));
                    notInStudE051ValidCount = Convert.ToInt32(reader.GetValue(4));
                    notInStudE051InvalidCount = Convert.ToInt32(reader.GetValue(5));
                    ghostStudentCount = Convert.ToInt32(reader.GetValue(6));
                    confirmedByRule29Count = Convert.ToInt32(reader.GetValue(7));
                    notInRule29Count = Convert.ToInt32(reader.GetValue(8));
                    rule29OnlyCountTotal = Convert.ToInt32(reader.GetValue(9));
                }
            }

            var reviewRows = new List<Rule67ValidationRowRecord>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
WITH {ctes}
SELECT creg_stud_no, creg_qual, creg_e051, stud_no, stud_qual, in_stud, stud_student_exists,
       ghost_student, ghost_student_note, e051_valid, validation_result, fail_reason, reconciliation_status
FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, creg_stud_no, creg_qual;";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var validationResult = GetString(reader, 10) ?? "FAIL";
                    var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["CREG_STUD_NO"] = GetString(reader, 0),
                        ["CREG_QUAL"] = GetString(reader, 1),
                        ["CREG_E051"] = GetString(reader, 2),
                        ["STUD_NO"] = GetString(reader, 3),
                        ["STUD_QUAL"] = GetString(reader, 4),
                        ["IN_STUD"] = GetString(reader, 5),
                        ["STUD_STUDENT_EXISTS"] = GetString(reader, 6),
                        ["GHOST_STUDENT"] = GetString(reader, 7),
                        ["GHOST_STUDENT_NOTE"] = GetString(reader, 8),
                        ["E051_VALID"] = GetString(reader, 9),
                        ["ValidationResult"] = validationResult,
                        ["FailReason"] = GetString(reader, 11),
                        ["Reconciliation_Status"] = GetString(reader, 12)
                    };
                    var row = new Rule67ValidationRowRecord
                    {
                        ValidationNumber = reviewRows.Count + 1,
                        ControlType = "Control_1",
                        ControlLabel = "Control 1",
                        ValidationResult = validationResult,
                        ExceptionCode = string.Equals(validationResult, "FAIL", StringComparison.OrdinalIgnoreCase) ? "00708" : "",
                        DisplayValues = displayValues
                    };
                    EnrichDisplayValues(row);
                    reviewRows.Add(row);
                }
            }

            var rule29OnlyRows = new List<Rule67Rule29OnlyRow>();
            if (hasDetail)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
WITH {ctes}
SELECT dp.detail_stud_no, dp.detail_qual,
       CASE WHEN cs.creg_stud_no IS NULL THEN 'Not in CREG'
            WHEN fr.creg_stud_no IS NOT NULL THEN 'Yes'
            ELSE 'No' END AS confirmed_by_r67
FROM detail_pairs dp
LEFT JOIN creg_stud_nos cs ON cs.creg_stud_no = dp.detail_stud_no
LEFT JOIN (SELECT DISTINCT creg_stud_no, creg_qual FROM results WHERE validation_result = 'FAIL') fr
    ON fr.creg_stud_no = dp.detail_stud_no AND fr.creg_qual = dp.detail_qual
ORDER BY dp.detail_stud_no, dp.detail_qual;";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rule29OnlyRows.Add(new Rule67Rule29OnlyRow
                    {
                        RowNumber = rule29OnlyRows.Count + 1,
                        StudentNo = GetString(reader, 0) ?? "",
                        QualCode = GetString(reader, 1) ?? "",
                        ConfirmedByR67 = GetString(reader, 2) ?? "No"
                    });
                }
            }

            var r29ConfirmedTotal = rule29OnlyRows.Count(r => string.Equals(r.ConfirmedByR67, "Yes", StringComparison.OrdinalIgnoreCase));
            var r29InCregPassTotal = rule29OnlyRows.Count(r => string.Equals(r.ConfirmedByR67, "No", StringComparison.OrdinalIgnoreCase));
            var r29NotInCregTotal = rule29OnlyRows.Count(r => string.Equals(r.ConfirmedByR67, "Not in CREG", StringComparison.OrdinalIgnoreCase));

            var failCount = notInStudCount + invalidE051Count;

            var controlSummaries = new List<Rule67ControlSummaryItemViewModel>
            {
                new()
                {
                    ControlType  = "Control_1",
                    ControlLabel = "Control 1",
                    CriteriaText = $"CREG [{cregStudentCol}]+[{cregQualCol}] pair must exist in STUD and [{cregE051Col}] IN ({e051ValuesText})",
                    TotalCount   = totalChecked,
                    PassCount    = passCount,
                    FailCount    = failCount,
                    Status       = failCount == 0 ? "PASS" : "FAIL"
                }
            };

            return new Rule67ValidationSummary
            {
                Success = true,
                CregRecordCount = cregCount,
                StudRecordCount = studCount,
                TotalValidated = totalChecked,
                DisplayedCount = reviewRows.Count,
                PassCount = passCount,
                FailCount = failCount,
                NotInStudCount = notInStudCount,
                NotInStudE051ValidCount = notInStudE051ValidCount,
                NotInStudE051InvalidCount = notInStudE051InvalidCount,
                GhostStudentCount = ghostStudentCount,
                InvalidE051Count = invalidE051Count,
                ExceptionRate = totalChecked == 0 ? 0m : Math.Round(failCount * 100m / totalChecked, 2),
                Status = failCount == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                CregTable = request.CregTable,
                StudTable = request.StudTable,
                CregStudentNoCol = cregStudentCol,
                CregQualCol = cregQualCol,
                CregE051Col = cregE051Col,
                StudStudentNoCol = studStudentCol,
                StudQualCol = studQualCol,
                E051FilterValues = e051Values.Count > 0 ? string.Join(",", e051Values) : "",
                DetailTable = hasDetail ? request.DetailTable : "",
                DetailErrorCode = detailErrCodes.Count > 0 ? string.Join(",", detailErrCodes) : "00708",
                DetailErrorCol = request.DetailErrorCol,
                DetailElementInfoCol = request.DetailElementInfoCol,
                DetailRecordCount = detailCount,
                ConfirmedByRule29Count = confirmedByRule29Count,
                NotInRule29Count = notInRule29Count,
                Rule29OnlyCount = rule29OnlyCountTotal,
                Rule29ConfirmedByR67Count = r29ConfirmedTotal,
                Rule29InCregPassCount = r29InCregPassTotal,
                Rule29NotInCregCount = r29NotInCregTotal,
                Rule29OnlyRows = rule29OnlyRows,
                TableLinkageText = $"{request.CregTable}.[{cregStudentCol}]+[{cregQualCol}] <> {request.StudTable}.[{studStudentCol}]+[{studQualCol}] (E051 filter: [{cregE051Col}] IN {e051ValuesText})",
                RuleModeText = $"CREG pairs checked against STUD, [{cregE051Col}] must be IN ({e051ValuesText})",
                ProcedureSteps = new List<string>
                {
                    $"Extract all distinct [{cregStudentCol}]+[{cregQualCol}]+[{cregE051Col}] combinations from {request.CregTable}.",
                    $"For each CREG pair, check if [{studStudentCol}]+[{studQualCol}] exists in {request.StudTable}.",
                    $"Also check that [{cregE051Col}] is IN ({e051ValuesText}).",
                    "Mark PASS when the pair is found in STUD AND E051 is in the filter. FAIL otherwise (exception code 00708).",
                    "A FAIL indicates: pair missing from STUD, or E051 code does not match the expected value(s).",
                    hasDetail ? $"Reconciliation: cross-reference FAIL results against {request.DetailTable} (error {request.DetailErrorCode}) to confirm findings." : ""
                }.Where(s => !string.IsNullOrEmpty(s)).ToList(),
                ClientId = request.ClientId,
                ControlSummaries = controlSummaries,
                ReviewRows = reviewRows,
                Warning = null
            };
        }

        private static void EnrichDisplayValues(Rule67ValidationRowRecord row)
        {
            var v = row.DisplayValues;
            var isPass = string.Equals(row.ValidationResult, "PASS", StringComparison.OrdinalIgnoreCase);
            var studNo = v.TryGetValue("CREG_STUD_NO", out var sn) ? sn ?? "" : "";
            var qual = v.TryGetValue("CREG_QUAL", out var q) ? q ?? "" : "";
            var e051 = v.TryGetValue("CREG_E051", out var e) ? e ?? "" : "";
            var reason = v.TryGetValue("FailReason", out var fr) ? fr ?? "" : "";
            var ghost = string.Equals(v.TryGetValue("GHOST_STUDENT", out var gs) ? gs : "", "Yes", StringComparison.OrdinalIgnoreCase);
            var studStudentExists = string.Equals(v.TryGetValue("STUD_STUDENT_EXISTS", out var sse) ? sse : "", "Yes", StringComparison.OrdinalIgnoreCase);
            var ghostNote = v.TryGetValue("GHOST_STUDENT_NOTE", out var gn) ? gn ?? "" : "";

            v["FINAL_RESULT_MESSAGE"] = isPass
                ? $"PASS: CREG pair '{studNo}' / '{qual}' (E051: {e051}) found in STUD with valid E051 code."
                : reason switch
                {
                    "Not found in STUD" when ghost
                        => $"FAIL (00708): CREG pair '{studNo}' / '{qual}' (E051: {e051}) - student number not found in STUD. Marked as Ghost Student. {ghostNote}",
                    "Not found in STUD" when studStudentExists
                        => $"FAIL (00708): CREG pair '{studNo}' / '{qual}' (E051: {e051}) - student number exists in STUD, but the qualification pair was not found.",
                    _ => $"FAIL (00708): CREG pair '{studNo}' / '{qual}' (E051: {e051}) - {reason}."
                };
            row.ValidationExplanation = v["FINAL_RESULT_MESSAGE"] ?? "";
        }

        // ── SQL builders ──────────────────────────────────────────────────────

        // Returns only the CTE definitions (no trailing SELECT) so callers can each append their
        // own final SELECT against "results"/"detail_pairs"/"creg_stud_nos" without illegal
        // nested-WITH syntax.
        private static string BuildValidationCtes(
            string schema, Rule67ValidationRequest request,
            string cregStudentCol, string cregQualCol, string cregE051Col, string studStudentCol, string studQualCol,
            IReadOnlyList<string> e051Values, bool hasDetail, IReadOnlyList<long> detailErrCodes)
        {
            var ct = Sanitise(request.CregTable);
            var st = Sanitise(request.StudTable);

            var e051ValidExpr = e051Values.Count > 0
                ? $"CASE WHEN cp.creg_e051 IN ({BuildInClauseSql(e051Values)}) THEN 'Yes' ELSE 'No' END"
                : "'Yes'";
            var passExpr = e051Values.Count > 0
                ? $"sp.stud_no IS NOT NULL AND cp.creg_e051 IN ({BuildInClauseSql(e051Values)})"
                : "sp.stud_no IS NOT NULL";
            var failReasonExpr = e051Values.Count > 0
                ? $"CASE WHEN sp.stud_no IS NULL THEN 'Not found in STUD' WHEN cp.creg_e051 NOT IN ({BuildInClauseSql(e051Values)}) THEN 'E051 code not in expected values' ELSE '' END"
                : "CASE WHEN sp.stud_no IS NULL THEN 'Not found in STUD' ELSE '' END";

            var detailPairsCte = hasDetail
                ? $@"detail_parsed AS (
    SELECT
        TRIM(SUBSTRING(el FROM POSITION('E007:' IN el) + 5 FOR GREATEST(POSITION('E001:' IN el) - POSITION('E007:' IN el) - 5, 0))) AS raw_stud_no,
        UPPER(TRIM(SUBSTRING(el FROM POSITION('E001:' IN el) + 5))) AS raw_qual
    FROM (
        SELECT TRIM(CAST(d.""{request.DetailElementInfoCol}"" AS text)) AS el, TRIM(CAST(d.""{request.DetailErrorCol}"" AS text)) AS err_raw
        FROM ""{schema}"".""{Sanitise(request.DetailTable)}"" d
    ) x
    WHERE POSITION('E007:' IN el) > 0 AND POSITION('E001:' IN el) > 0
      AND (CASE WHEN err_raw ~ '^[0-9]+$' THEN err_raw::bigint END) IN ({BuildDetailErrInSql(detailErrCodes)})
),
detail_pairs AS (
    SELECT raw_stud_no AS detail_stud_no, raw_qual AS detail_qual
    FROM detail_parsed
    WHERE raw_stud_no <> '' AND raw_qual <> ''
    GROUP BY raw_stud_no, raw_qual
)"
                : "detail_pairs AS (SELECT NULL::text AS detail_stud_no, NULL::text AS detail_qual WHERE FALSE)";

            var reconExpr = hasDetail
                ? $@"CASE WHEN {passExpr} THEN ''
             WHEN EXISTS (SELECT 1 FROM detail_pairs dp WHERE dp.detail_stud_no = cp.creg_stud_no AND dp.detail_qual = cp.creg_qual) THEN 'Confirmed by Rule 29'
             ELSE 'Not in Rule 29' END"
                : "''";

            return $@"
creg_stud_nos AS (
    SELECT DISTINCT TRIM(CAST(c.""{cregStudentCol}"" AS text)) AS creg_stud_no
    FROM ""{schema}"".""{ct}"" c
    WHERE TRIM(CAST(c.""{cregStudentCol}"" AS text)) <> ''
),
creg_pairs AS (
    SELECT DISTINCT
        TRIM(CAST(c.""{cregStudentCol}"" AS text)) AS creg_stud_no,
        UPPER(TRIM(CAST(c.""{cregQualCol}"" AS text))) AS creg_qual,
        UPPER(TRIM(CAST(c.""{cregE051Col}"" AS text))) AS creg_e051
    FROM ""{schema}"".""{ct}"" c
    WHERE TRIM(CAST(c.""{cregStudentCol}"" AS text)) <> ''
      AND TRIM(CAST(c.""{cregQualCol}"" AS text)) <> ''
),
stud_pairs AS (
    SELECT DISTINCT
        TRIM(CAST(s.""{studStudentCol}"" AS text)) AS stud_no,
        UPPER(TRIM(CAST(s.""{studQualCol}"" AS text))) AS stud_qual
    FROM ""{schema}"".""{st}"" s
    WHERE s.""{studStudentCol}"" IS NOT NULL AND s.""{studQualCol}"" IS NOT NULL
),
stud_nos AS (
    SELECT DISTINCT TRIM(CAST(s.""{studStudentCol}"" AS text)) AS stud_student_no
    FROM ""{schema}"".""{st}"" s
    WHERE s.""{studStudentCol}"" IS NOT NULL
),
stud_first_qual AS (
    SELECT stud_no, MIN(stud_qual) AS stud_actual_qual FROM stud_pairs GROUP BY stud_no
),
{detailPairsCte},
results AS (
    SELECT
        cp.creg_stud_no, cp.creg_qual, cp.creg_e051,
        COALESCE(sp.stud_no, CASE WHEN ssn.stud_student_no IS NOT NULL THEN cp.creg_stud_no END) AS stud_no,
        COALESCE(sp.stud_qual, CASE WHEN ssn.stud_student_no IS NOT NULL THEN sfq.stud_actual_qual END) AS stud_qual,
        CASE WHEN ssn.stud_student_no IS NOT NULL THEN 'Yes' ELSE 'No' END AS in_stud,
        CASE WHEN ssn.stud_student_no IS NOT NULL THEN 'Yes' ELSE 'No' END AS stud_student_exists,
        CASE WHEN ssn.stud_student_no IS NULL THEN 'Yes' ELSE 'No' END AS ghost_student,
        CASE WHEN ssn.stud_student_no IS NULL THEN 'Ghost student - tested in Rule 9.'
             WHEN sp.stud_no IS NULL THEN 'Student found in STUD with different qualification code'
             ELSE '' END AS ghost_student_note,
        {e051ValidExpr} AS e051_valid,
        CASE WHEN {passExpr} THEN 'PASS' ELSE 'FAIL' END AS validation_result,
        {failReasonExpr} AS fail_reason,
        {reconExpr} AS reconciliation_status
    FROM creg_pairs cp
    LEFT JOIN stud_pairs sp ON sp.stud_no = cp.creg_stud_no AND sp.stud_qual = cp.creg_qual
    LEFT JOIN stud_nos ssn ON ssn.stud_student_no = cp.creg_stud_no
    LEFT JOIN stud_first_qual sfq ON sfq.stud_no = cp.creg_stud_no
)";
        }

        public string GenerateSql(Rule67ValidationRequest request)
        {
            var cregStudentCol = Sanitise(request.CregStudentNoCol);
            var cregQualCol = Sanitise(request.CregQualCol);
            var cregE051Col = Sanitise(string.IsNullOrWhiteSpace(request.CregE051Col) ? "_051" : request.CregE051Col);
            var studStudentCol = Sanitise(request.StudStudentNoCol);
            var studQualCol = Sanitise(request.StudQualCol);
            var e051Values = ParseFilterValues(request.E051FilterValues);
            var e051ValuesText = e051Values.Count > 0 ? string.Join(", ", e051Values.Select(v => $"'{v}'")) : "ALL — no filter applied";
            var hasDetail = !string.IsNullOrWhiteSpace(request.DetailTable);
            var detailErrCodes = ParseDetailErrorCodes(request.DetailErrorCode);
            var ctes = BuildValidationCtes("{schema}", request, cregStudentCol, cregQualCol, cregE051Col, studStudentCol, studQualCol, e051Values, hasDetail, detailErrCodes);

            return $@"-- ============================================================
-- HEMIS RULE 67 - CREG-STUD Pair Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Check     : CREG.[{cregStudentCol}] + [{cregQualCol}] pair must exist in STUD.[{studStudentCol}] + [{studQualCol}]
-- AND CREG.[{cregE051Col}] must be IN ({e051ValuesText})
-- PASS when pair found in STUD and E051 matches; FAIL otherwise (Exception code: 00708)
-- ============================================================
WITH {ctes}
SELECT * FROM results
ORDER BY CASE WHEN validation_result = 'FAIL' THEN 0 ELSE 1 END, creg_stud_no, creg_qual;".Trim();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule67ValidationRequest request, Rule67ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 67);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 67,
                RuleName = "CREG-STUD Pair Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.CregTable,
                DeceasedTable = request.StudTable,
                StudColumn = request.CregStudentNoCol,
                DeceasedColumn = request.StudStudentNoCol,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(
                    summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList())),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule67WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 67);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);

            var workspace = new Rule67WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                CregTable = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                StudTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_STUD_VALPAC" : row.DeceasedTable,
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
                workspace.CregStudentNoCol = summary.CregStudentNoCol;
                workspace.CregQualCol = summary.CregQualCol;
                workspace.CregE051Col = summary.CregE051Col;
                workspace.StudStudentNoCol = summary.StudStudentNoCol;
                workspace.StudQualCol = summary.StudQualCol;
                workspace.E051FilterValues = summary.E051FilterValues;
                workspace.DetailTable = summary.DetailTable;
                workspace.DetailErrorCode = summary.DetailErrorCode;
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

        public async Task<Rule67RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 67);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary.SavedRunId = runId;

            var viewModel = new Rule67RunReviewViewModel
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
            viewModel.GeneratedSql = GenerateSql(new Rule67ValidationRequest
            {
                ClientId = viewModel.ClientId,
                CregTable = summary.CregTable,
                StudTable = summary.StudTable,
                CregStudentNoCol = summary.CregStudentNoCol,
                CregQualCol = summary.CregQualCol,
                CregE051Col = summary.CregE051Col,
                StudStudentNoCol = summary.StudStudentNoCol,
                StudQualCol = summary.StudQualCol,
                E051FilterValues = summary.E051FilterValues,
                DetailTable = summary.DetailTable,
                DetailErrorCode = summary.DetailErrorCode,
                DetailErrorCol = summary.DetailErrorCol,
                DetailElementInfoCol = summary.DetailElementInfoCol
            });

            ApplyBrowserPreview(summary);
            return viewModel;
        }

        public async Task<Rule67WorkspaceSaveResult> SaveWorkspaceAsync(Rule67ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule67WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule67WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.CregTable,
                    DeceasedTable = request.StudTable,
                    StudColumn = request.CregStudentNoCol,
                    DeceasedColumn = request.StudStudentNoCol
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule67WorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new Rule67WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule67WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule67WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule67WorkspaceSaveResult
                {
                    Success = true,
                    Message = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace = workspace
                };
            }
            catch (Exception ex) { return new Rule67WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        public async Task<Rule67ValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 67);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static void ApplyBrowserPreview(Rule67ValidationSummary summary)
        {
            if (summary.Rule29OnlyRows.Count > BrowserPreviewRowLimit)
                summary.Rule29OnlyRows = summary.Rule29OnlyRows.Take(BrowserPreviewRowLimit).ToList();

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

        private static List<string> ParseFilterValues(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(SanitiseFilterValue)
                .Where(v => v.Length > 0)
                .Select(v => v.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string SanitiseFilterValue(string value) =>
            value.Replace("'", "").Replace(";", "").Replace("--", "").Trim();

        private static string BuildInClauseSql(IReadOnlyList<string> values) =>
            string.Join(",", values.Select(v => $"'{v.Replace("'", "''")}'"));

        // Parse comma-separated error codes like "00708,00159" → [708, 159].
        // Normalises leading zeros so "00708" == "708" in the IN clause.
        private static IReadOnlyList<long> ParseDetailErrorCodes(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new[] { 708L };
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => { var t = s.TrimStart('0'); return long.TryParse(t == "" ? "0" : t, out var n) ? n : (long?)null; })
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .Distinct()
                .ToList();
        }

        private static string BuildDetailErrInSql(IReadOnlyList<long> codes) =>
            codes.Count == 0 ? "0" : string.Join(",", codes);

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

        private static Rule67ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<Rule67ValidationSummary>(ValidationPayloadCodec.Decode(json)); }
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
