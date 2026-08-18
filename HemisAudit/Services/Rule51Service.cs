using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using Newtonsoft.Json;

namespace HemisAudit.Services
{
    // Rule 51: VALPAC Data in Production — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. 100% population test of the VALPAC
    // table against the PRODUCTION table on N configurable mapped column pairs, with a
    // foreign-national exemption (citizen/resident status + Z-placeholder ID + blank PROD ID),
    // an optional CREG lookup for NOT_IN_CREG / CREG_WITHDRAWN exemptions, and a PASS_REVIEW
    // status for students who already passed on their primary qualification record.
    public class Rule51Service : IRule51Service
    {
        // Storage/Excel population cap — effectively "full population" for any realistically-sized
        // engagement dataset, while still guarding against a pathological runaway query.
        private const int RowLimit = 200000;
        // UI results show only this many sample rows; the full population is stored (RowLimit above)
        // and available via Excel/CSV download.
        private const int UiSampleLimit = 10;
        private const string MatchMarkerAlias = "PROD_MATCH_FOUND";

        private static readonly Rule51ColumnMapping[] DefaultColumnMappings =
        {
            new() { ValpacColumn = "_007", ProdColumn = "IAGSTNO", Label = "Student No" },
            new() { ValpacColumn = "_008", ProdColumn = "IADIDNO", Label = "ID No" },
            new() { ValpacColumn = "_001", ProdColumn = "IAGQUAL", Label = "Qualification" },
            new() { ValpacColumn = "ColYear", ProdColumn = "IAGCYR", Label = "Year" }
        };

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule51Service(
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

        public async Task<Rule51TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule51TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule51TableDiscoveryResult
                {
                    Success         = true,
                    Tables          = tables,
                    // Some engagements upload the raw pre-validation STUD extract as just "dbo_stud"
                    // (no "_valpac" suffix) rather than a table literally named "...VALPAC...".
                    // Prefer an explicit VALPAC-named table, but fall back to a plain STUD table -
                    // as long as that candidate isn't itself a "prod"-named table.
                    AutoValpacTable = FindFirst(tables, ["dbo_STUD_VALPAC", "STUD_VALPAC", "VALPAC"], ["valpac"])
                                      ?? FindStudFallback(tables),
                    // Engagements often upload several "prod"-named tables (mt_audit_prod_std,
                    // mt_audit_prod_qual, mt_audit_prod_crse, ...) - a bare "prod" contains-match
                    // would silently grab whichever comes first, not necessarily the STUD one.
                    // Require "prod"+"std" together (or a known exact name) before falling back
                    // to the loose single-token match.
                    AutoProdTable   = FindFirst(tables, ["dbo_STUD_PRODUCTION", "STUD_PRODUCTION", "MT-audit-prod-std", "MT_AUDIT_PROD_STD", "mt_audit_prod_std"], [])
                                      ?? FindFirstContainsAll(tables, "prod", "std")
                                      ?? FindFirst(tables, [], ["production", "prod"]),
                    AutoCregTable   = FindFirst(tables, ["dbo_CREG", "CREG"], ["creg"])
                };
            }
            catch (Exception ex) { return new Rule51TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule51ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "valpac_007"      => FindFirstExact(cols, "_007"),
                    "valpac_008"      => FindFirstExact(cols, "_008"),
                    "valpac_001"      => FindFirstExact(cols, "_001"),
                    "valpac_year"     => FindFirstContains(cols, "colyear", "year"),
                    "valpac_049"      => FindFirstExact(cols, "_049"),
                    "prod_stno"       => FindFirstContains(cols, "iagstno", "stno"),
                    "prod_idno"       => FindFirstContains(cols, "iadidno", "idno"),
                    "prod_qual"       => FindFirstContains(cols, "iagqual", "qual"),
                    "prod_year"       => FindFirstContains(cols, "iagcyr", "cyr", "year"),
                    "creg_id"         => FindFirstExact(cols, "_007"),
                    "creg_completion" => FindFirstExact(cols, "_032"),
                    _ => null
                };
                return new Rule51ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule51ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule51VerifyResult> VerifyDataAsync(Rule51ValidationRequest request)
        {
            try
            {
                var valpacTable = Sanitise(request.ValpacTable);
                var prodTable   = Sanitise(request.ProdTable);
                var mappings    = SanitizeMappings(GetMappings(request));
                if (mappings.Count == 0) throw new InvalidOperationException("At least one column mapping is required.");

                await ValidateColumnsExistAsync(request.ClientId, request.ValpacTable, mappings.Select(m => m.ValpacColumn));
                await ValidateColumnsExistAsync(request.ClientId, request.ProdTable, mappings.Select(m => m.ProdColumn));

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var valpacCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{valpacTable}\";");
                var prodCount   = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{prodTable}\";");

                var col049             = !string.IsNullOrWhiteSpace(request.ValpacCol049) ? Sanitise(request.ValpacCol049) : null;
                var saValues           = ParseSaValues(request.SaNationalValues);
                var zPlaceholders      = ParseZPlaceholders(request.ValpacCol008ZPlaceholders);
                var cregTable          = !string.IsNullOrWhiteSpace(request.CregTable) ? Sanitise(request.CregTable) : null;
                var cregIdCol          = !string.IsNullOrWhiteSpace(request.CregIdCol) ? Sanitise(request.CregIdCol) : null;
                var cregCompletionCol  = !string.IsNullOrWhiteSpace(request.CregCompletionStatusCol) ? Sanitise(request.CregCompletionStatusCol) : null;
                var cregCompletionVals = ParseFilterValues(request.CregCompletionStatusValues);

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = BuildPopulationCountSql(schema, valpacTable, prodTable, mappings, col049, saValues, zPlaceholders, cregTable, cregIdCol, cregCompletionCol, cregCompletionVals);
                await using var reader = await cmd.ExecuteReaderAsync();

                var result = new Rule51VerifyResult { Success = true, ValpacRecordCount = valpacCount, ProdRecordCount = prodCount };
                if (await reader.ReadAsync())
                {
                    result.TotalTested  = GetInt(reader, 0);
                    result.MatchedCount = GetInt(reader, 1);
                    result.MissingCount = GetInt(reader, 2);
                }
                return result;
            }
            catch (Exception ex) { return new Rule51VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule51ValidationSummary> RunValidationAsync(Rule51ValidationRequest request, string? userEmail = null, string? userName = null)
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
            catch (Exception ex) { return new Rule51ValidationSummary { Success = false, Error = ex.Message }; }
        }

        private async Task<Rule51ValidationSummary> AnalyseAsync(Rule51ValidationRequest req)
        {
            var valpacTable = Sanitise(req.ValpacTable);
            var prodTable   = Sanitise(req.ProdTable);
            var mappings    = SanitizeMappings(GetMappings(req));
            if (mappings.Count == 0) throw new InvalidOperationException("At least one column mapping is required.");

            await ValidateColumnsExistAsync(req.ClientId, req.ValpacTable, mappings.Select(m => m.ValpacColumn));
            await ValidateColumnsExistAsync(req.ClientId, req.ProdTable, mappings.Select(m => m.ProdColumn));

            var col049             = !string.IsNullOrWhiteSpace(req.ValpacCol049) ? Sanitise(req.ValpacCol049) : null;
            var saValues           = ParseSaValues(req.SaNationalValues);
            var zPlaceholders      = ParseZPlaceholders(req.ValpacCol008ZPlaceholders);
            var cregTable          = !string.IsNullOrWhiteSpace(req.CregTable) ? Sanitise(req.CregTable) : null;
            var cregIdCol          = !string.IsNullOrWhiteSpace(req.CregIdCol) ? Sanitise(req.CregIdCol) : null;
            var cregCompletionCol  = !string.IsNullOrWhiteSpace(req.CregCompletionStatusCol) ? Sanitise(req.CregCompletionStatusCol) : null;
            var cregCompletionVals = ParseFilterValues(req.CregCompletionStatusValues);

            var (conn, schema) = await OpenEngagementConnectionAsync(req.ClientId);
            await using var connection = conn;

            var valpacCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{valpacTable}\";");
            var prodCount   = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{prodTable}\";");

            int totalTested, matched, missing, passReviewCount, notInCregCount, cregWithdrawnCount;
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = BuildPopulationCountSql(schema, valpacTable, prodTable, mappings, col049, saValues, zPlaceholders, cregTable, cregIdCol, cregCompletionCol, cregCompletionVals);
                await using var countReader = await countCmd.ExecuteReaderAsync();
                totalTested = matched = missing = passReviewCount = notInCregCount = cregWithdrawnCount = 0;
                if (await countReader.ReadAsync())
                {
                    totalTested        = GetInt(countReader, 0);
                    matched            = GetInt(countReader, 1);
                    missing            = GetInt(countReader, 2);
                    passReviewCount    = GetInt(countReader, 3);
                    notInCregCount     = GetInt(countReader, 4);
                    cregWithdrawnCount = GetInt(countReader, 5);
                }
            }

            var reviewRows = await LoadRowsAsync(connection, schema, valpacTable, prodTable, RowLimit, mappings, col049, saValues, zPlaceholders, cregTable, cregIdCol, cregCompletionCol, cregCompletionVals);

            var foreignExemptCount = reviewRows.Count(r => string.Equals(ReadValue(r.DisplayValues, "FOREIGN_NATIONAL_EXEMPT"), "1"));
            var controlSummaries   = BuildControlSummaries(totalTested, matched, req.ValpacTable, req.ProdTable, mappings.Count);
            var totalValidated     = controlSummaries.Sum(x => x.TotalCount);
            var passCount          = controlSummaries.Sum(x => x.PassCount);
            var failCount          = controlSummaries.Sum(x => x.FailCount);
            var exceptionCategories = BuildExceptionCategories(reviewRows, mappings);

            var summary = new Rule51ValidationSummary
            {
                Success           = true,
                ValpacRecordCount = valpacCount,
                ProdRecordCount   = prodCount,
                TotalRequested    = totalValidated,
                TotalValidated    = totalValidated,
                DisplayedCount    = reviewRows.Count,
                PassCount         = passCount,
                FailCount         = failCount,
                ExceptionRate     = totalValidated == 0 ? 0m : Math.Round(failCount * 100m / totalValidated, 2),
                Status            = failCount == 0 ? "PASS" : "FAIL",
                Timestamp         = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ValpacTable       = req.ValpacTable,
                ProdTable         = req.ProdTable,
                ValpacCol049      = req.ValpacCol049 ?? "",
                SaNationalValues  = req.SaNationalValues ?? "SA,PR",
                ValpacCol008ZPlaceholders = req.ValpacCol008ZPlaceholders ?? "ZZZZZZZZZZZZZ",
                ForeignNationalExemptCount = foreignExemptCount,
                PassWithReviewCount = passReviewCount,
                NotInCregCount    = notInCregCount,
                CregWithdrawnCount = cregWithdrawnCount,
                CregTable         = req.CregTable ?? "",
                CregIdCol         = req.CregIdCol ?? "_007",
                CregCompletionStatusCol    = req.CregCompletionStatusCol ?? "_032",
                CregCompletionStatusValues = req.CregCompletionStatusValues ?? "W",
                ColumnMappings    = CloneMappings(mappings),
                TableLinkageText  = BuildTableLinkageText(req.ValpacTable, req.ProdTable, mappings),
                RuleModeText      = $"100% population testing of {req.ValpacTable} against {req.ProdTable} on {mappings.Count} mapped column pair{(mappings.Count == 1 ? "" : "s")}",
                ProcedureSteps    = BuildProcedureSteps(req.ValpacTable, req.ProdTable, mappings),
                ClientId          = req.ClientId,
                ControlSummaries  = controlSummaries,
                ExceptionCategories = exceptionCategories,
                ReviewRows        = reviewRows,
                Warning = totalValidated > reviewRows.Count
                    ? $"{totalValidated:N0} rows were found; only the first {reviewRows.Count:N0} are stored to keep the app responsive. All totals above are exact."
                    : null
            };

            ApplyMappings(summary, mappings);
            return summary;
        }

        private static Rule51ValidationSummary ApplyUiSample(Rule51ValidationSummary full)
        {
            if (full.ReviewRows.Count <= UiSampleLimit) return full;
            return new Rule51ValidationSummary
            {
                Success = full.Success, ValpacRecordCount = full.ValpacRecordCount, ProdRecordCount = full.ProdRecordCount,
                TotalRequested = full.TotalRequested, TotalValidated = full.TotalValidated, DisplayedCount = full.DisplayedCount,
                PassCount = full.PassCount, FailCount = full.FailCount, ForeignNationalExemptCount = full.ForeignNationalExemptCount,
                PassWithReviewCount = full.PassWithReviewCount, NotInCregCount = full.NotInCregCount, CregWithdrawnCount = full.CregWithdrawnCount,
                ExceptionRate = full.ExceptionRate, Status = full.Status, Timestamp = full.Timestamp,
                ValpacTable = full.ValpacTable, ProdTable = full.ProdTable,
                ValpacCol007 = full.ValpacCol007, ValpacCol008 = full.ValpacCol008, ValpacCol001 = full.ValpacCol001, ValpacColYear = full.ValpacColYear,
                ProdColStNo = full.ProdColStNo, ProdColIdNo = full.ProdColIdNo, ProdColQual = full.ProdColQual, ProdColYear = full.ProdColYear,
                ValpacCol049 = full.ValpacCol049, SaNationalValues = full.SaNationalValues, ValpacCol008ZPlaceholders = full.ValpacCol008ZPlaceholders,
                CregTable = full.CregTable, CregIdCol = full.CregIdCol,
                CregCompletionStatusCol = full.CregCompletionStatusCol, CregCompletionStatusValues = full.CregCompletionStatusValues,
                ColumnMappings = full.ColumnMappings, TableLinkageText = full.TableLinkageText, RuleModeText = full.RuleModeText,
                ProcedureSteps = full.ProcedureSteps, ClientId = full.ClientId, SavedRunId = full.SavedRunId,
                ControlSummaries = full.ControlSummaries, ExceptionCategories = full.ExceptionCategories,
                ReviewRows = full.ReviewRows.Take(UiSampleLimit).ToList(),
                Warning = "UI results show only the first 10 sample rows. Download the results to access the full tested population."
            };
        }

        // ─── SQL Builders ────────────────────────────────────────────────────────

        private static string BuildSourceCtes(
            string schema,
            string valpacTable,
            string prodTable,
            IReadOnlyList<Rule51ColumnMapping> mappings,
            string? col049 = null)
        {
            var valpacSelectItems = new List<string>();
            for (var i = 0; i < mappings.Count; i++)
            {
                var m = mappings[i];
                valpacSelectItems.Add($"CAST(V.\"{m.ValpacColumn}\" AS text) AS {ValpacDisplayAlias(i)}");
                valpacSelectItems.Add($"UPPER(TRIM(CAST(V.\"{m.ValpacColumn}\" AS text))) AS {ValpacKeyAlias(i)}");
            }
            if (!string.IsNullOrWhiteSpace(col049))
            {
                valpacSelectItems.Add($"CAST(V.\"{col049}\" AS text) AS VALPAC_049_DISP");
                valpacSelectItems.Add($"UPPER(TRIM(CAST(V.\"{col049}\" AS text))) AS VALPAC_049_KEY");
            }

            var prodSelectItems = new List<string>();
            var prodGroupByItems = new List<string>();
            for (var i = 0; i < mappings.Count; i++)
            {
                var m = mappings[i];
                var keyExpr = $"UPPER(TRIM(CAST(P.\"{m.ProdColumn}\" AS text)))";
                prodSelectItems.Add($"MIN(CAST(P.\"{m.ProdColumn}\" AS text)) AS {ProdDisplayAlias(i)}");
                prodSelectItems.Add($"{keyExpr} AS {ProdKeyAlias(i)}");
                prodGroupByItems.Add(keyExpr);
            }
            prodSelectItems.Add($"1 AS {MatchMarkerAlias}");

            var partialSelectItems = new List<string>
            {
                $"UPPER(TRIM(CAST(P.\"{mappings[0].ProdColumn}\" AS text))) AS PART_KEY_0"
            };
            for (var i = 0; i < mappings.Count; i++)
                partialSelectItems.Add($"MIN(CAST(P.\"{mappings[i].ProdColumn}\" AS text)) AS PART_DISP_{i}");

            var validationSelectItems = new List<string>();
            var joinConditions = new List<string>();
            for (var i = 0; i < mappings.Count; i++)
            {
                validationSelectItems.Add($"VD.{ValpacDisplayAlias(i)}");
                validationSelectItems.Add($"VD.{ValpacKeyAlias(i)}");
                validationSelectItems.Add($"COALESCE(PU.{ProdDisplayAlias(i)}, PP.PART_DISP_{i}) AS {ProdDisplayAlias(i)}");
                joinConditions.Add($"PU.{ProdKeyAlias(i)} = VD.{ValpacKeyAlias(i)}");
            }
            validationSelectItems.Add($"PU.{MatchMarkerAlias}");
            validationSelectItems.Add("CASE WHEN PP.PART_KEY_0 IS NOT NULL THEN 1 ELSE 0 END AS PARTIAL_MATCH_FOUND");
            if (!string.IsNullOrWhiteSpace(col049))
            {
                validationSelectItems.Add("VD.VALPAC_049_DISP");
                validationSelectItems.Add("VD.VALPAC_049_KEY");
            }

            return $@"
WITH ValpacData AS
(
    SELECT
{BuildIndentedList(valpacSelectItems, "        ")}
    FROM ""{schema}"".""{valpacTable}"" V
),
ProdUnique AS
(
    SELECT
{BuildIndentedList(prodSelectItems, "        ")}
    FROM ""{schema}"".""{prodTable}"" P
    GROUP BY {string.Join("," + Environment.NewLine + "        ", prodGroupByItems)}
),
ProdPartial AS
(
    SELECT
{BuildIndentedList(partialSelectItems, "        ")}
    FROM ""{schema}"".""{prodTable}"" P
    GROUP BY UPPER(TRIM(CAST(P.""{mappings[0].ProdColumn}"" AS text)))
),
ValidationResults AS
(
    SELECT
{BuildIndentedList(validationSelectItems, "        ")}
    FROM ValpacData VD
    LEFT JOIN ProdUnique PU
        ON {string.Join(Environment.NewLine + "        AND ", joinConditions)}
    LEFT JOIN ProdPartial PP
        ON PP.PART_KEY_0 = VD.{ValpacKeyAlias(0)}
)";
        }

        private static string BuildPopulationCountSql(
            string schema,
            string valpacTable,
            string prodTable,
            IReadOnlyList<Rule51ColumnMapping> mappings,
            string? col049,
            IReadOnlyList<string>? saNationalValues,
            IReadOnlyList<string>? zPlaceholders,
            string? cregTable,
            string? cregIdCol,
            string? cregCompletionCol,
            IReadOnlyList<string>? cregCompletionValues)
        {
            var exemptWhen = BuildForeignNationalExemptWhen(col049, saNationalValues, mappings, zPlaceholders);
            var hasCregCheck = !string.IsNullOrWhiteSpace(cregTable) && !string.IsNullOrWhiteSpace(cregIdCol);
            var hasCregCompletionCheck = hasCregCheck && !string.IsNullOrWhiteSpace(cregCompletionCol) && cregCompletionValues != null && cregCompletionValues.Count > 0;
            var passExpr = string.IsNullOrEmpty(exemptWhen)
                ? $"{MatchMarkerAlias} IS NOT NULL"
                : $"({MatchMarkerAlias} IS NOT NULL OR ({exemptWhen}))";
            var studentPassFlagsCte = string.IsNullOrEmpty(exemptWhen)
                ? $@",
StudentPassFlags AS
(
    SELECT DISTINCT {ValpacKeyAlias(0)} AS SPF_STUDENT_KEY
    FROM ValidationResults
    WHERE {MatchMarkerAlias} IS NOT NULL
)"
                : $@",
StudentPassFlags AS
(
    SELECT DISTINCT {ValpacKeyAlias(0)} AS SPF_STUDENT_KEY
    FROM ValidationResults
    WHERE {MatchMarkerAlias} IS NOT NULL
       OR ({exemptWhen})
)";
            var cregStudentsCte = hasCregCheck
                ? $@",
CregStudents AS
(
    SELECT DISTINCT UPPER(TRIM(CAST(""{cregIdCol}"" AS text))) AS CREG_STUDENT_KEY
    FROM ""{schema}"".""{cregTable}""
)"
                : "";
            var cregWithdrawnCte = hasCregCompletionCheck
                ? $@",
CregWithdrawnStudents AS
(
    SELECT DISTINCT UPPER(TRIM(CAST(""{cregIdCol}"" AS text))) AS CREG_WD_STUDENT_KEY
    FROM ""{schema}"".""{cregTable}""
    WHERE UPPER(TRIM(CAST(""{cregCompletionCol}"" AS text))) IN ({BuildInClauseSql(cregCompletionValues!)})
)"
                : "";
            var cregJoin = hasCregCheck ? $"\nLEFT JOIN CregStudents ON CREG_STUDENT_KEY = {ValpacKeyAlias(0)}" : "";
            var cregWithdrawnJoin = hasCregCompletionCheck ? $"\nLEFT JOIN CregWithdrawnStudents ON CREG_WD_STUDENT_KEY = {ValpacKeyAlias(0)}" : "";
            var notInCregExpr = hasCregCheck
                ? $",\n    SUM(CASE WHEN NOT ({passExpr}) AND SPF_STUDENT_KEY IS NULL AND CREG_STUDENT_KEY IS NULL THEN 1 ELSE 0 END) AS NotInCregCount"
                : ",\n    0 AS NotInCregCount";
            var cregWithdrawnExpr = hasCregCompletionCheck
                ? $",\n    SUM(CASE WHEN NOT ({passExpr}) AND SPF_STUDENT_KEY IS NULL AND CREG_STUDENT_KEY IS NOT NULL AND CREG_WD_STUDENT_KEY IS NOT NULL THEN 1 ELSE 0 END) AS CregWithdrawnCount"
                : ",\n    0 AS CregWithdrawnCount";
            var missingExpr = hasCregCheck
                ? (hasCregCompletionCheck
                    ? $"CASE WHEN NOT ({passExpr}) AND SPF_STUDENT_KEY IS NULL AND CREG_STUDENT_KEY IS NOT NULL AND CREG_WD_STUDENT_KEY IS NULL THEN 1 ELSE 0 END"
                    : $"CASE WHEN NOT ({passExpr}) AND SPF_STUDENT_KEY IS NULL AND CREG_STUDENT_KEY IS NOT NULL THEN 1 ELSE 0 END")
                : $"CASE WHEN NOT ({passExpr}) AND SPF_STUDENT_KEY IS NULL THEN 1 ELSE 0 END";

            return $@"
{BuildSourceCtes(schema, valpacTable, prodTable, mappings, col049)}{studentPassFlagsCte}{cregStudentsCte}{cregWithdrawnCte}
SELECT
    COUNT(1) AS TotalTested,
    SUM(CASE WHEN {passExpr} OR SPF_STUDENT_KEY IS NOT NULL THEN 1 ELSE 0 END) AS MatchedCount,
    SUM({missingExpr}) AS MissingCount,
    SUM(CASE WHEN NOT ({passExpr}) AND SPF_STUDENT_KEY IS NOT NULL THEN 1 ELSE 0 END) AS PassReviewCount{notInCregExpr}{cregWithdrawnExpr}
FROM ValidationResults
LEFT JOIN StudentPassFlags ON SPF_STUDENT_KEY = {ValpacKeyAlias(0)}{cregJoin}{cregWithdrawnJoin};";
        }

        private static string BuildInClauseSql(IReadOnlyList<string> values) =>
            string.Join(",", values.Select(v => $"'{v}'"));

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

        private static string BuildAllRowsSql(
            string schema,
            string valpacTable,
            string prodTable,
            int? maxRows,
            IReadOnlyList<Rule51ColumnMapping> mappings,
            string? col049,
            IReadOnlyList<string>? saNationalValues,
            IReadOnlyList<string>? zPlaceholders,
            string? cregTable,
            string? cregIdCol,
            string? cregCompletionCol,
            IReadOnlyList<string>? cregCompletionValues)
        {
            var exemptWhen = BuildForeignNationalExemptWhen(col049, saNationalValues, mappings, zPlaceholders);
            var hasCregCheck = !string.IsNullOrWhiteSpace(cregTable) && !string.IsNullOrWhiteSpace(cregIdCol);
            var hasCregCompletionCheck = hasCregCheck && !string.IsNullOrWhiteSpace(cregCompletionCol) && cregCompletionValues != null && cregCompletionValues.Count > 0;

            var studentPassFlagsCte = string.IsNullOrEmpty(exemptWhen)
                ? $@",
StudentPassFlags AS
(
    SELECT DISTINCT {ValpacKeyAlias(0)} AS SPF_STUDENT_KEY
    FROM ValidationResults
    WHERE {MatchMarkerAlias} IS NOT NULL
)"
                : $@",
StudentPassFlags AS
(
    SELECT DISTINCT {ValpacKeyAlias(0)} AS SPF_STUDENT_KEY
    FROM ValidationResults
    WHERE {MatchMarkerAlias} IS NOT NULL
       OR ({exemptWhen})
)";

            var cregStudentsCte = hasCregCheck
                ? $@",
CregStudents AS
(
    SELECT DISTINCT UPPER(TRIM(CAST(""{cregIdCol}"" AS text))) AS CREG_STUDENT_KEY
    FROM ""{schema}"".""{cregTable}""
)"
                : "";

            var cregWithdrawnCte = hasCregCompletionCheck
                ? $@",
CregWithdrawnStudents AS
(
    SELECT
        UPPER(TRIM(CAST(""{cregIdCol}"" AS text))) AS CREG_WD_STUDENT_KEY,
        MIN(CAST(""{cregCompletionCol}"" AS text)) AS CREG_WD_STATUS_DISP
    FROM ""{schema}"".""{cregTable}""
    WHERE UPPER(TRIM(CAST(""{cregCompletionCol}"" AS text))) IN ({BuildInClauseSql(cregCompletionValues!)})
    GROUP BY UPPER(TRIM(CAST(""{cregIdCol}"" AS text)))
)"
                : "";

            var resultWhens = new List<string> { $"WHEN {MatchMarkerAlias} IS NOT NULL THEN 'PASS'" };
            var explanationWhens = new List<string> { $"WHEN {MatchMarkerAlias} IS NOT NULL THEN 'VALPAC record found in PRODUCTION.'" };

            if (!string.IsNullOrEmpty(exemptWhen))
            {
                resultWhens.Add($"WHEN {exemptWhen} THEN 'PASS'");
                explanationWhens.Add($"WHEN {exemptWhen} THEN 'Foreign national exemption: {col049} is not SA/PR, ID is all-Z placeholder, PROD ID is blank.'");
            }

            resultWhens.Add("WHEN SPF_STUDENT_KEY IS NOT NULL THEN 'PASS_REVIEW'");
            explanationWhens.Add("WHEN SPF_STUDENT_KEY IS NOT NULL THEN 'Student passed on primary qualification in PRODUCTION. This additional VALPAC qualification record is not an exception.'");

            if (hasCregCheck)
            {
                resultWhens.Add("WHEN CREG_STUDENT_KEY IS NULL THEN 'NOT_IN_CREG'");
                explanationWhens.Add($"WHEN CREG_STUDENT_KEY IS NULL THEN 'Student not found in CREG table {cregTable}. The university did not provide a service for this student, so absence from PRODUCTION is expected. Not an exception.'");
            }

            if (hasCregCompletionCheck)
            {
                resultWhens.Add("WHEN CREG_WD_STUDENT_KEY IS NOT NULL THEN 'CREG_WITHDRAWN'");
                explanationWhens.Add($"WHEN CREG_WD_STUDENT_KEY IS NOT NULL THEN 'Student found in CREG with completion status {cregCompletionCol} matching the configured withdrawal filter. The student withdrew from the course registration, so absence from PRODUCTION is expected. Not an exception.'");
            }

            var validationResultExpr = $"CASE\n        {string.Join("\n        ", resultWhens)}\n        ELSE 'FAIL'\n    END";
            var validationExplanationExpr = $"CASE\n        {string.Join("\n        ", explanationWhens)}\n        ELSE 'VALPAC record not found in PRODUCTION.'\n    END";

            var isForeignExemptExpr = string.IsNullOrEmpty(exemptWhen)
                ? "0"
                : $"CASE WHEN {MatchMarkerAlias} IS NULL AND ({exemptWhen}) THEN 1 ELSE 0 END";

            var selectItems = new List<string>
            {
                "1 AS Control_Sort",
                "'Control_1' AS Control_Type",
                $"'CONTROL 1: {valpacTable} data exists in {prodTable}' AS Control_Label",
                $"{validationResultExpr} AS Validation_Result",
                $"{validationExplanationExpr} AS Validation_Explanation"
            };

            selectItems.AddRange(mappings.Select((_, index) => ValpacDisplayAlias(index)));
            selectItems.AddRange(mappings.Select((_, index) => ProdDisplayAlias(index)));
            selectItems.Add("PARTIAL_MATCH_FOUND");
            selectItems.Add($"{isForeignExemptExpr} AS FOREIGN_NATIONAL_EXEMPT");
            if (!string.IsNullOrWhiteSpace(col049))
                selectItems.Add("VALPAC_049_DISP");
            if (hasCregCompletionCheck)
                selectItems.Add("CREG_WD_STATUS_DISP AS CREG_COMPLETION_STATUS_DISP");

            var cregJoin = hasCregCheck
                ? $"\nLEFT JOIN CregStudents ON CREG_STUDENT_KEY = {ValpacKeyAlias(0)}"
                : "";
            var cregWithdrawnJoin = hasCregCompletionCheck
                ? $"\nLEFT JOIN CregWithdrawnStudents ON CREG_WD_STUDENT_KEY = {ValpacKeyAlias(0)}"
                : "";
            var limitClause = maxRows.HasValue && maxRows.Value > 0 ? $"\nLIMIT {maxRows.Value}" : "";

            return $@"
{BuildSourceCtes(schema, valpacTable, prodTable, mappings, col049)}{studentPassFlagsCte}{cregStudentsCte}{cregWithdrawnCte}
SELECT
{BuildIndentedList(selectItems, "    ")}
FROM ValidationResults
LEFT JOIN StudentPassFlags ON SPF_STUDENT_KEY = {ValpacKeyAlias(0)}{cregJoin}{cregWithdrawnJoin}
ORDER BY {BuildOrderByClause(mappings.Count, maxRows.HasValue && maxRows.Value > 0, hasCregCheck, hasCregCompletionCheck)}{limitClause};";
        }

        // Returns the SQL WHEN condition (without the WHEN keyword) for the foreign-national exemption.
        // Returns empty string if _049 is not configured.
        private static string BuildForeignNationalExemptWhen(
            string? col049,
            IReadOnlyList<string>? saNationalValues,
            IReadOnlyList<Rule51ColumnMapping> mappings,
            IReadOnlyList<string>? zPlaceholders = null)
        {
            if (string.IsNullOrWhiteSpace(col049) || mappings.Count < 2)
                return string.Empty;

            var saList = saNationalValues != null && saNationalValues.Count > 0
                ? string.Join(",", saNationalValues.Select(v => $"'{v.Trim().ToUpperInvariant()}'"))
                : "'SA','PR'";

            var zList = zPlaceholders != null && zPlaceholders.Count > 0
                ? string.Join(",", zPlaceholders.Select(v => $"'{v.Trim().ToUpperInvariant()}'"))
                : "'ZZZZZZZZZZZZZ'";

            var idKeyAlias  = ValpacKeyAlias(1);
            var prodIdAlias = ProdDisplayAlias(1);

            return $@"VALPAC_049_KEY NOT IN ({saList})
        AND {idKeyAlias} IN ({zList})
        AND COALESCE(UPPER(TRIM(CAST({prodIdAlias} AS text))), '') = ''";
        }

        private async Task<List<Rule51ValidationRowRecord>> LoadRowsAsync(
            NpgsqlConnection connection,
            string schema,
            string valpacTable,
            string prodTable,
            int? maxRows,
            IReadOnlyList<Rule51ColumnMapping> mappings,
            string? col049,
            IReadOnlyList<string>? saValues,
            IReadOnlyList<string>? zPlaceholders,
            string? cregTable,
            string? cregIdCol,
            string? cregCompletionCol,
            IReadOnlyList<string>? cregCompletionValues)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = BuildAllRowsSql(schema, valpacTable, prodTable, maxRows, mappings, col049, saValues, zPlaceholders, cregTable, cregIdCol, cregCompletionCol, cregCompletionValues);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule51ValidationRowRecord>();
            while (await reader.ReadAsync())
            {
                var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    displayValues[reader.GetName(i)] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);

                rows.Add(new Rule51ValidationRowRecord
                {
                    ValidationNumber = rows.Count + 1,
                    ControlType = ReadValue(displayValues, "Control_Type"),
                    ControlLabel = ReadValue(displayValues, "Control_Label"),
                    ValidationResult = ReadValue(displayValues, "Validation_Result"),
                    ValidationExplanation = ReadValue(displayValues, "Validation_Explanation"),
                    DisplayValues = displayValues
                });
                EnrichDisplayValues(rows[^1], mappings);
            }
            return rows;
        }

        private static void EnrichDisplayValues(Rule51ValidationRowRecord row, IReadOnlyList<Rule51ColumnMapping> mappings)
        {
            var v = row.DisplayValues;
            var result = ReadValue(v, "Validation_Result") ?? "";
            var isPass = string.Equals(result, "PASS", StringComparison.OrdinalIgnoreCase);
            var isPassReview  = string.Equals(result, "PASS_REVIEW",  StringComparison.OrdinalIgnoreCase);
            var isNotInCreg   = string.Equals(result, "NOT_IN_CREG",  StringComparison.OrdinalIgnoreCase);
            var isCregWithdrawn = string.Equals(result, "CREG_WITHDRAWN", StringComparison.OrdinalIgnoreCase);
            var valpacRef = BuildDisplayReference(v, mappings, useProdValues: false);

            if (isPass)
            {
                var isForeignNationalExempt = string.Equals(ReadValue(v, "FOREIGN_NATIONAL_EXEMPT"), "1");
                if (isForeignNationalExempt)
                {
                    var citizenVal = ReadValue(v, "VALPAC_049_DISP") ?? "";
                    var idVal = ReadValue(v, ValpacDisplayAlias(1)) ?? "";
                    v["FINAL_RESULT_MESSAGE"] = $"PASS (Exempt): Foreign national — citizen/resident status ({citizenVal}) is not SA/PR, ID placeholder ({idVal}) is expected, PROD ID is blank.";
                    v["EXCEPTION_REASON"] = $"Exempt: citizen/resident status = '{citizenVal}' (not SA/PR), ID = '{idVal}' (all-Z placeholder for foreign national with no SA ID), PRODUCTION ID is blank — no ID number required.";
                    v["EXCEPTION_CATEGORY"] = "PASS__FOREIGN_NATIONAL";
                }
                else
                {
                    var prodRef = BuildDisplayReference(v, mappings, useProdValues: true);
                    v["FINAL_RESULT_MESSAGE"] = $"PASS: VALPAC record matched in PRODUCTION. VALPAC: {valpacRef} | PRODUCTION: {prodRef}";
                    v["EXCEPTION_REASON"] = "";
                    v["EXCEPTION_CATEGORY"] = "PASS";
                }
            }
            else if (isPassReview)
            {
                var stNo = ReadValue(v, ValpacDisplayAlias(0)) ?? "";
                var qualVal = mappings.Count > 1 ? ReadValue(v, ValpacDisplayAlias(1)) ?? "" : "";
                var prodQualVal = mappings.Count > 1 ? ReadValue(v, ProdDisplayAlias(1)) ?? "" : "";
                var reviewNote = mappings.Count > 1 && !string.IsNullOrWhiteSpace(qualVal)
                    ? $"Qualification ({mappings[1].ValpacColumn}): VALPAC='{qualVal}' ≠ PROD='{prodQualVal}'. "
                    : "";
                v["FINAL_RESULT_MESSAGE"] = $"PASS (Review): Student No ({mappings[0].ValpacColumn} = '{stNo}') passed on primary qualification in PRODUCTION. {reviewNote}No exception — this additional VALPAC record does not require a match.";
                v["EXCEPTION_REASON"] = $"Student passed on primary qualification. {reviewNote}PRODUCTION stores only the primary qualification record; this additional VALPAC entry is not expected in PRODUCTION.";
                v["EXCEPTION_CATEGORY"] = "PASS_REVIEW";
            }
            else if (isNotInCreg)
            {
                var stNo = ReadValue(v, ValpacDisplayAlias(0)) ?? "";
                var label0 = mappings.Count > 0 ? mappings[0].Label : mappings[0].ValpacColumn;
                v["FINAL_RESULT_MESSAGE"] = $"NOT IN CREG: {label0} ({mappings[0].ValpacColumn} = '{stNo}') not found in CREG. The university did not provide a service for this student — absence from PRODUCTION is expected. Not an exception.";
                v["EXCEPTION_REASON"] = $"Student ({label0} = '{stNo}') does not exist in the CREG table. The university never registered a service record for this student, so the student is not expected to appear in PRODUCTION.";
                v["EXCEPTION_CATEGORY"] = "NOT_IN_CREG";
            }
            else if (isCregWithdrawn)
            {
                var stNo = ReadValue(v, ValpacDisplayAlias(0)) ?? "";
                var label0 = mappings.Count > 0 ? mappings[0].Label : mappings[0].ValpacColumn;
                var completionVal = ReadValue(v, "CREG_COMPLETION_STATUS_DISP") ?? "";
                v["FINAL_RESULT_MESSAGE"] = $"WITHDREW (CREG): {label0} ({mappings[0].ValpacColumn} = '{stNo}') not found in PRODUCTION, but is in CREG with completion status = '{completionVal}'. The student withdrew from the course registration, so absence from PRODUCTION is expected. Not an exception.";
                v["EXCEPTION_REASON"] = $"{label0} ({mappings[0].ValpacColumn} = '{stNo}') does not exist in PRODUCTION table, but was found in CREG with completion status = '{completionVal}', matching the configured withdrawal filter. The student withdrew from the course registration, so the student is not expected to appear in PRODUCTION.";
                v["EXCEPTION_CATEGORY"] = "CREG_WITHDRAWN";
            }
            else
            {
                var partialFound = string.Equals(ReadValue(v, "PARTIAL_MATCH_FOUND"), "1");
                if (!partialFound)
                {
                    var stNo = ReadValue(v, ValpacDisplayAlias(0)) ?? "";
                    var label0 = mappings.Count > 0 ? mappings[0].Label : mappings[0].ValpacColumn;
                    v["FINAL_RESULT_MESSAGE"] = $"FAIL: {label0} ({mappings[0].ValpacColumn} = '{stNo}') not found in PRODUCTION.";
                    v["EXCEPTION_REASON"] = $"{label0} ({mappings[0].ValpacColumn} = '{stNo}') does not exist in PRODUCTION table. The record cannot be matched because the student is not present.";
                    v["EXCEPTION_CATEGORY"] = $"NOT_FOUND__{label0}";
                }
                else
                {
                    var diffParts = new List<string>();
                    var categories = new List<string>();
                    for (var i = 1; i < mappings.Count; i++)
                    {
                        var valpacVal = (ReadValue(v, ValpacDisplayAlias(i)) ?? "").Trim();
                        var prodVal   = (ReadValue(v, ProdDisplayAlias(i)) ?? "").Trim();
                        if (!string.Equals(valpacVal, prodVal, StringComparison.OrdinalIgnoreCase))
                        {
                            var lbl = !string.IsNullOrWhiteSpace(mappings[i].Label) ? mappings[i].Label : mappings[i].ValpacColumn;
                            diffParts.Add($"{lbl} ({mappings[i].ValpacColumn}): VALPAC='{valpacVal}' ≠ PROD='{prodVal}'");
                            categories.Add($"DIFF__{lbl}");
                        }
                    }
                    var diffSummary = diffParts.Count > 0 ? string.Join("; ", diffParts) : "values differ";
                    var categoryKey = categories.Count == 1 ? categories[0]
                        : categories.Count > 1 ? "DIFF__MULTIPLE"
                        : "DIFF__UNKNOWN";
                    v["FINAL_RESULT_MESSAGE"] = $"FAIL: {mappings[0].Label} found in PRODUCTION but record does not match. {diffSummary}.";
                    v["EXCEPTION_REASON"] = $"{mappings[0].Label} ({mappings[0].ValpacColumn} = '{ReadValue(v, ValpacDisplayAlias(0))}') exists in PRODUCTION, but the full record does not match: {diffSummary}.";
                    v["EXCEPTION_CATEGORY"] = categoryKey;
                }
            }

            row.ValidationExplanation = ReadValue(v, "EXCEPTION_REASON") is { Length: > 0 } reason
                ? reason
                : ReadValue(v, "FINAL_RESULT_MESSAGE") ?? "";
        }

        private static List<Rule51ExceptionCategoryViewModel> BuildExceptionCategories(
            IReadOnlyList<Rule51ValidationRowRecord> rows,
            IReadOnlyList<Rule51ColumnMapping> mappings)
        {
            var counts = new Dictionary<string, (string Description, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var cat = ReadValue(row.DisplayValues, "EXCEPTION_CATEGORY") ?? "";
                if (string.IsNullOrEmpty(cat)
                    || string.Equals(cat, "PASS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cat, "PASS_REVIEW", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cat, "NOT_IN_CREG", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cat, "CREG_WITHDRAWN", StringComparison.OrdinalIgnoreCase))
                    continue;

                string desc;
                if (string.Equals(cat, "PASS__FOREIGN_NATIONAL", StringComparison.OrdinalIgnoreCase))
                    desc = "Exempt — foreign national (not SA/PR): ID is all-Z placeholder, PROD ID is blank";
                else if (cat.StartsWith("NOT_FOUND__", StringComparison.OrdinalIgnoreCase))
                    desc = $"{cat["NOT_FOUND__".Length..]} not found in PRODUCTION";
                else if (cat.StartsWith("DIFF__", StringComparison.OrdinalIgnoreCase))
                {
                    var lbl = cat["DIFF__".Length..];
                    desc = lbl == "MULTIPLE" ? "Multiple columns differ"
                         : lbl == "UNKNOWN"  ? "Record found but mismatch (unknown column)"
                         : $"{lbl} differs";
                }
                else desc = cat;

                counts[cat] = counts.TryGetValue(cat, out var existing) ? (existing.Description, existing.Count + 1) : (desc, 1);
            }

            return counts
                .OrderByDescending(kv => kv.Value.Count)
                .Select(kv => new Rule51ExceptionCategoryViewModel { Category = kv.Key, Description = kv.Value.Description, Count = kv.Value.Count })
                .ToList();
        }

        private static List<Rule51ControlSummaryItemViewModel> BuildControlSummaries(int total, int matched, string valpacTable, string prodTable, int mappingCount)
        {
            var fail = Math.Max(total - matched, 0);
            return new List<Rule51ControlSummaryItemViewModel>
            {
                new()
                {
                    ControlType  = "Control_1",
                    ControlLabel = "Control 1",
                    CriteriaText = $"All {valpacTable} records exist in {prodTable} ({mappingCount} mapped column pair{(mappingCount == 1 ? "" : "s")})",
                    TotalCount   = total,
                    PassCount    = matched,
                    FailCount    = fail,
                    Status       = fail == 0 ? "PASS" : "FAIL"
                }
            };
        }

        private static List<string> BuildProcedureSteps(string valpacTable, string prodTable, IReadOnlyList<Rule51ColumnMapping> mappings) => new()
        {
            $"Select all records from {valpacTable} as the population to test.",
            $"For each VALPAC record, attempt to find a matching row in {prodTable} using {mappings.Count} selected column pair{(mappings.Count == 1 ? "" : "s")}.",
            "Mark PASS when a matching row exists in PRODUCTION; FAIL when no match is found.",
            "All VALPAC data is expected to exist in PRODUCTION."
        };

        // ─── SQL generation ────────────────────────────────────────────────────

        public string GenerateSql(Rule51ValidationRequest request)
        {
            var valpacTable = Sanitise(request.ValpacTable);
            var prodTable   = Sanitise(request.ProdTable);
            var mappings    = SanitizeMappings(GetMappings(request));
            if (mappings.Count == 0) mappings = BuildDefaultMappings();

            var mappingNotes = string.Join(Environment.NewLine, mappings.Select(m =>
                $"--   {valpacTable}.{m.ValpacColumn} <> {prodTable}.{m.ProdColumn}"));
            var selectColumns = mappings
                .SelectMany((_, index) => new[] { ValpacDisplayAlias(index), ProdDisplayAlias(index) })
                .ToList();

            return $@"-- ============================================================
-- HEMIS RULE 51 - VALPAC Data in Production
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Check     : ALL data from {valpacTable} must exist in {prodTable}
-- Mapped columns:
{mappingNotes}
-- PASS when all {mappings.Count} mapped column pair{(mappings.Count == 1 ? "" : "s")} match a row in {prodTable}
-- ============================================================

{BuildSourceCtes("{schema}", valpacTable, prodTable, mappings)}
SELECT
    'Control_1' AS Control_Type,
    'CONTROL 1: {valpacTable} data exists in {prodTable}' AS Control_Label,
    {string.Join("," + Environment.NewLine + "    ", selectColumns)},
    CASE WHEN {MatchMarkerAlias} IS NOT NULL THEN 'PASS' ELSE 'FAIL' END AS Validation_Result
FROM ValidationResults
ORDER BY {BuildOrderByClause(mappings.Count, failFirst: false)};

SELECT
    (SELECT COUNT(1) FROM ValpacData) AS Valpac_Total,
    (SELECT COUNT(1) FROM ValidationResults WHERE {MatchMarkerAlias} IS NOT NULL) AS Matched,
    (SELECT COUNT(1) FROM ValidationResults WHERE {MatchMarkerAlias} IS NULL) AS Missing;".Trim();
        }

        // ─── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule51ValidationRequest request, Rule51ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 51);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 51,
                RuleName = "VALPAC Data in Production",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.ValpacTable,
                DeceasedTable = request.ProdTable,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(
                    summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList())),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule51ValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 51);
            if (row == null) return null;
            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        public async Task<Rule51WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 51);
            if (row == null) return null;

            var summary  = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary = ApplyUiSample(summary);
            var mappings = summary != null ? GetMappings(summary) : BuildDefaultMappings();

            var workspace = new Rule51WorkspaceStateViewModel
            {
                ClientId       = row.ClientId,
                RunId          = row.RunId,
                ValpacTable    = summary?.ValpacTable ?? (string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD_VALPAC" : row.StudTable),
                ProdTable      = summary?.ProdTable ?? (string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_STUD_PRODUCTION" : row.DeceasedTable),
                CregTable      = summary?.CregTable ?? "",
                CregIdCol      = summary?.CregIdCol ?? "_007",
                CregCompletionStatusCol    = summary?.CregCompletionStatusCol ?? "_032",
                CregCompletionStatusValues = summary?.CregCompletionStatusValues ?? "W",
                ColumnMappings = CloneMappings(mappings),
                CurrentStatus  = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt   = row.LastEditedAt,
                Summary        = summary
            };

            ApplyMappings(workspace, mappings);
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

        public async Task<Rule51RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 51);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary = ApplyUiSample(summary);
            summary.SavedRunId = runId;

            var viewModel = new Rule51RunReviewViewModel
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

        public async Task<Rule51WorkspaceSaveResult> SaveWorkspaceAsync(Rule51ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule51WorkspaceSaveResult { Success = false, Error = "Run the validation first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule51WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.ValpacTable,
                    DeceasedTable = request.ProdTable
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule51WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule51WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule51WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule51WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule51WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule51WorkspaceSaveResult { Success = false, Error = ex.Message }; }
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

        // ─── Column mapping helpers ─────────────────────────────────────────────

        private static List<Rule51ColumnMapping> BuildDefaultMappings() =>
            DefaultColumnMappings.Select(m => new Rule51ColumnMapping { ValpacColumn = m.ValpacColumn, ProdColumn = m.ProdColumn, Label = m.Label }).ToList();

        private static List<Rule51ColumnMapping> BuildLegacyMappings(string? v007, string? v008, string? v001, string? vYear, string? pSt, string? pId, string? pQual, string? pYear)
        {
            var mappings = new List<Rule51ColumnMapping>();
            AddLegacyMapping(mappings, v007, pSt, "Student No");
            AddLegacyMapping(mappings, v008, pId, "ID No");
            AddLegacyMapping(mappings, v001, pQual, "Qualification");
            AddLegacyMapping(mappings, vYear, pYear, "Year");
            return mappings;
        }

        private static void AddLegacyMapping(List<Rule51ColumnMapping> mappings, string? valpacColumn, string? prodColumn, string label)
        {
            if (string.IsNullOrWhiteSpace(valpacColumn) || string.IsNullOrWhiteSpace(prodColumn)) return;
            mappings.Add(new Rule51ColumnMapping { ValpacColumn = valpacColumn.Trim(), ProdColumn = prodColumn.Trim(), Label = label });
        }

        private static List<Rule51ColumnMapping> NormalizeMappings(IEnumerable<Rule51ColumnMapping>? mappings, IEnumerable<Rule51ColumnMapping>? fallback)
        {
            var normalized = (mappings ?? Enumerable.Empty<Rule51ColumnMapping>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.ValpacColumn) && !string.IsNullOrWhiteSpace(m.ProdColumn))
                .Select(m => new Rule51ColumnMapping { ValpacColumn = m.ValpacColumn.Trim(), ProdColumn = m.ProdColumn.Trim(), Label = string.IsNullOrWhiteSpace(m.Label) ? m.ValpacColumn.Trim() : m.Label.Trim() })
                .ToList();
            if (normalized.Count > 0) return normalized;

            var fallbackMappings = (fallback ?? BuildDefaultMappings())
                .Where(m => !string.IsNullOrWhiteSpace(m.ValpacColumn) && !string.IsNullOrWhiteSpace(m.ProdColumn))
                .Select(m => new Rule51ColumnMapping { ValpacColumn = m.ValpacColumn.Trim(), ProdColumn = m.ProdColumn.Trim(), Label = string.IsNullOrWhiteSpace(m.Label) ? m.ValpacColumn.Trim() : m.Label.Trim() })
                .ToList();
            return fallbackMappings.Count > 0 ? fallbackMappings : BuildDefaultMappings();
        }

        private static List<Rule51ColumnMapping> GetMappings(Rule51ValidationRequest request) =>
            NormalizeMappings(request.ColumnMappings, BuildLegacyMappings(
                request.ValpacCol007, request.ValpacCol008, request.ValpacCol001, request.ValpacColYear,
                request.ProdColStNo, request.ProdColIdNo, request.ProdColQual, request.ProdColYear));

        private static List<Rule51ColumnMapping> GetMappings(Rule51ValidationSummary summary) =>
            NormalizeMappings(summary.ColumnMappings, BuildLegacyMappings(
                summary.ValpacCol007, summary.ValpacCol008, summary.ValpacCol001, summary.ValpacColYear,
                summary.ProdColStNo, summary.ProdColIdNo, summary.ProdColQual, summary.ProdColYear));

        private static List<Rule51ColumnMapping> CloneMappings(IEnumerable<Rule51ColumnMapping>? mappings) =>
            (mappings ?? Enumerable.Empty<Rule51ColumnMapping>())
                .Select(m => new Rule51ColumnMapping { ValpacColumn = m.ValpacColumn, ProdColumn = m.ProdColumn, Label = m.Label })
                .ToList();

        private static List<Rule51ColumnMapping> SanitizeMappings(IEnumerable<Rule51ColumnMapping> mappings) =>
            mappings.Select(m => new Rule51ColumnMapping
            {
                ValpacColumn = Sanitise(m.ValpacColumn),
                ProdColumn = Sanitise(m.ProdColumn),
                Label = string.IsNullOrWhiteSpace(m.Label) ? m.ValpacColumn.Trim() : m.Label.Trim()
            }).ToList();

        private static void ApplyMappings(Rule51ValidationRequest request, IReadOnlyList<Rule51ColumnMapping> mappings)
        {
            request.ColumnMappings = CloneMappings(mappings);
            request.ValpacCol007 = LegacyValpacColumn(mappings, 0, "_007");
            request.ValpacCol008 = LegacyValpacColumn(mappings, 1, "_008");
            request.ValpacCol001 = LegacyValpacColumn(mappings, 2, "_001");
            request.ValpacColYear = LegacyValpacColumn(mappings, 3, "ColYear");
            request.ProdColStNo = LegacyProdColumn(mappings, 0, "IAGSTNO");
            request.ProdColIdNo = LegacyProdColumn(mappings, 1, "IADIDNO");
            request.ProdColQual = LegacyProdColumn(mappings, 2, "IAGQUAL");
            request.ProdColYear = LegacyProdColumn(mappings, 3, "IAGCYR");
        }

        private static void ApplyMappings(Rule51ValidationSummary summary, IReadOnlyList<Rule51ColumnMapping> mappings)
        {
            summary.ProcedureSteps ??= new List<string>();
            summary.ReviewRows ??= new List<Rule51ValidationRowRecord>();
            summary.ColumnMappings = CloneMappings(mappings);
            summary.ValpacCol007 = LegacyValpacColumn(mappings, 0, "_007");
            summary.ValpacCol008 = LegacyValpacColumn(mappings, 1, "_008");
            summary.ValpacCol001 = LegacyValpacColumn(mappings, 2, "_001");
            summary.ValpacColYear = LegacyValpacColumn(mappings, 3, "ColYear");
            summary.ProdColStNo = LegacyProdColumn(mappings, 0, "IAGSTNO");
            summary.ProdColIdNo = LegacyProdColumn(mappings, 1, "IADIDNO");
            summary.ProdColQual = LegacyProdColumn(mappings, 2, "IAGQUAL");
            summary.ProdColYear = LegacyProdColumn(mappings, 3, "IAGCYR");

            if (!string.IsNullOrWhiteSpace(summary.ValpacTable) && !string.IsNullOrWhiteSpace(summary.ProdTable))
                summary.TableLinkageText = BuildTableLinkageText(summary.ValpacTable, summary.ProdTable, mappings);
            if (string.IsNullOrWhiteSpace(summary.RuleModeText) && !string.IsNullOrWhiteSpace(summary.ValpacTable) && !string.IsNullOrWhiteSpace(summary.ProdTable))
                summary.RuleModeText = $"100% population testing of {summary.ValpacTable} against {summary.ProdTable} on {mappings.Count} mapped column pair{(mappings.Count == 1 ? "" : "s")}";
            if (summary.ProcedureSteps.Count == 0 && !string.IsNullOrWhiteSpace(summary.ValpacTable) && !string.IsNullOrWhiteSpace(summary.ProdTable))
                summary.ProcedureSteps = BuildProcedureSteps(summary.ValpacTable, summary.ProdTable, mappings);
        }

        private static void ApplyMappings(Rule51WorkspaceStateViewModel workspace, IReadOnlyList<Rule51ColumnMapping> mappings)
        {
            workspace.ColumnMappings = CloneMappings(mappings);
            workspace.ValpacCol007 = LegacyValpacColumn(mappings, 0, "_007");
            workspace.ValpacCol008 = LegacyValpacColumn(mappings, 1, "_008");
            workspace.ValpacCol001 = LegacyValpacColumn(mappings, 2, "_001");
            workspace.ValpacColYear = LegacyValpacColumn(mappings, 3, "ColYear");
            workspace.ProdColStNo = LegacyProdColumn(mappings, 0, "IAGSTNO");
            workspace.ProdColIdNo = LegacyProdColumn(mappings, 1, "IADIDNO");
            workspace.ProdColQual = LegacyProdColumn(mappings, 2, "IAGQUAL");
            workspace.ProdColYear = LegacyProdColumn(mappings, 3, "IAGCYR");
        }

        private static string LegacyValpacColumn(IReadOnlyList<Rule51ColumnMapping> mappings, int index, string fallback) =>
            mappings.Count > index && !string.IsNullOrWhiteSpace(mappings[index].ValpacColumn) ? mappings[index].ValpacColumn : fallback;

        private static string LegacyProdColumn(IReadOnlyList<Rule51ColumnMapping> mappings, int index, string fallback) =>
            mappings.Count > index && !string.IsNullOrWhiteSpace(mappings[index].ProdColumn) ? mappings[index].ProdColumn : fallback;

        private static string BuildTableLinkageText(string valpacTable, string prodTable, IReadOnlyList<Rule51ColumnMapping> mappings) =>
            string.Join(" | ", mappings.Select(m => $"{valpacTable}.{m.ValpacColumn}<>{prodTable}.{m.ProdColumn}"));

        private static string BuildIndentedList(IEnumerable<string> items, string indent)
        {
            var materialized = items.ToList();
            return materialized.Count == 0 ? indent : indent + string.Join("," + Environment.NewLine + indent, materialized);
        }

        private static string BuildOrderByClause(int mappingCount, bool failFirst, bool hasCregCheck = false, bool hasCregCompletionCheck = false)
        {
            var items = new List<string>();
            if (failFirst)
            {
                var failExpr = hasCregCheck
                    ? (hasCregCompletionCheck
                        ? $"CASE WHEN {MatchMarkerAlias} IS NULL AND SPF_STUDENT_KEY IS NULL AND CREG_STUDENT_KEY IS NOT NULL AND CREG_WD_STUDENT_KEY IS NULL THEN 0 WHEN {MatchMarkerAlias} IS NULL AND SPF_STUDENT_KEY IS NULL AND CREG_STUDENT_KEY IS NULL THEN 1 WHEN {MatchMarkerAlias} IS NULL AND SPF_STUDENT_KEY IS NULL AND CREG_WD_STUDENT_KEY IS NOT NULL THEN 2 WHEN {MatchMarkerAlias} IS NULL THEN 3 ELSE 4 END"
                        : $"CASE WHEN {MatchMarkerAlias} IS NULL AND SPF_STUDENT_KEY IS NULL AND CREG_STUDENT_KEY IS NOT NULL THEN 0 WHEN {MatchMarkerAlias} IS NULL AND SPF_STUDENT_KEY IS NULL AND CREG_STUDENT_KEY IS NULL THEN 1 WHEN {MatchMarkerAlias} IS NULL THEN 2 ELSE 3 END")
                    : $"CASE WHEN {MatchMarkerAlias} IS NULL AND SPF_STUDENT_KEY IS NULL THEN 0 WHEN {MatchMarkerAlias} IS NULL THEN 1 ELSE 2 END";
                items.Add(failExpr);
            }

            for (var i = 0; i < mappingCount; i++)
                items.Add(ValpacDisplayAlias(i));

            IEnumerable<string> orderedItems = items.Count == 0 ? new[] { "1" } : items;
            return string.Join(", ", orderedItems);
        }

        private static string ValpacDisplayAlias(int index) => $"VALPAC_COL_{index + 1}";
        private static string ValpacKeyAlias(int index) => $"VALPAC_KEY_{index + 1}";
        private static string ProdDisplayAlias(int index) => $"PROD_COL_{index + 1}";
        private static string ProdKeyAlias(int index) => $"PROD_KEY_{index + 1}";

        private static string BuildDisplayReference(IReadOnlyDictionary<string, string?> values, IReadOnlyList<Rule51ColumnMapping> mappings, bool useProdValues)
        {
            var parts = new List<string>();
            for (var i = 0; i < mappings.Count; i++)
            {
                var alias = useProdValues ? ProdDisplayAlias(i) : ValpacDisplayAlias(i);
                var value = ReadValue(values, alias);
                if (string.IsNullOrWhiteSpace(value)) continue;

                var mapping = mappings[i];
                var label = !string.IsNullOrWhiteSpace(mapping.Label) ? mapping.Label : useProdValues ? mapping.ProdColumn : mapping.ValpacColumn;
                parts.Add($"{label}: {value}");
            }
            return parts.Count == 0 ? "selected values unavailable" : string.Join(" | ", parts);
        }

        // ─── Utilities ─────────────────────────────────────────────────────────

        private static string Sanitise(string name) => name.Replace("\"", "").Replace("'", "").Replace(";", "").Trim();

        private static IReadOnlyList<string> ParseSaValues(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new[] { "SA", "PR" };
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(v => v.ToUpperInvariant()).Where(v => v.Length > 0).Distinct().ToArray();
        }

        private static IReadOnlyList<string> ParseZPlaceholders(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new[] { "ZZZZZZZZZZZZZ" };
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(v => v.ToUpperInvariant()).Where(v => v.Length > 0).Distinct().ToArray();
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

        private static string? FindFirstContainsAll(IEnumerable<string> values, params string[] fragments) =>
            values.FirstOrDefault(v => fragments.All(f => v.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0));

        private static string? FindStudFallback(IEnumerable<string> values) =>
            FindFirst(values, ["dbo_STUD", "dbo_stud", "STUD"], ["stud"]) is { } candidate
                && candidate.IndexOf("prod", StringComparison.OrdinalIgnoreCase) < 0
                ? candidate
                : null;

        private static string? FindFirstExact(IEnumerable<string> values, string exact) =>
            values.FirstOrDefault(v => string.Equals(v, exact, StringComparison.OrdinalIgnoreCase));

        private static string? FindFirstContains(IEnumerable<string> values, params string[] fragments)
        {
            foreach (var f in fragments)
            {
                var m = values.FirstOrDefault(v => v.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
                if (m != null) return m;
            }
            return null;
        }

        private static Rule51ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                if (string.IsNullOrWhiteSpace(decoded)) return null;
                var summary = JsonConvert.DeserializeObject<Rule51ValidationSummary>(decoded);
                if (summary == null) return null;
                ApplyMappings(summary, GetMappings(summary));
                return summary;
            }
            catch { return null; }
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager",     StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director",    StringComparison.OrdinalIgnoreCase);

        private static string ReadValue(IReadOnlyDictionary<string, string?> values, string key) => values.TryGetValue(key, out var v) ? v ?? "" : "";

        private static int GetInt(System.Data.Common.DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

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
