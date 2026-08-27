using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    // Rule 11: validates against the engagement's own uploaded Supabase data instead of a live
    // SQL Server connection, and saves through the shared Postgres-native persistence layer.
    // Ported from the Rule17 pilot pattern. Data source is 3 uploaded tables (QUAL, CESM, PQM) —
    // QUAL is LEFT JOINed to CESM in SQL, then matched against PQM row-by-row in memory (the
    // matching logic itself is dialect-agnostic C# and is preserved unchanged from the original).
    public class Rule11Service : IRule11Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private static readonly string[] DefaultQualTypeCodes = ["07", "27", "28", "49", "72", "73", "08", "30", "50", "74", "75"];
        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule11Service(
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

        // ── Normalisation / matching helpers (unchanged — dialect-agnostic C#) ────────────

        private static string NormName(string? v)
        {
            if (v == null) return "";
            return Regex.Replace(v.Trim().ToUpperInvariant(), @"\s+", " ");
        }

        private static string NormValue(string? v) =>
            v == null ? "" : v.Trim().ToUpperInvariant();

        private static string DigitsOnly(string? v) =>
            Regex.Replace(NormValue(v), @"\D", "");

        private static string TrimLeadingZeros(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var trimmed = value.TrimStart('0');
            return trimmed.Length == 0 ? "0" : trimmed;
        }

        private static bool HasSameLeadingDigits(string left, string right, int digits) =>
            left.Length >= digits &&
            right.Length >= digits &&
            string.Equals(left[..digits], right[..digits], StringComparison.Ordinal);

        private static string? GetCesmReviewMatchReason(string? qualCode, string? pqmCode)
        {
            var rawQualCode = DigitsOnly(qualCode);
            var rawPqmCode = DigitsOnly(pqmCode);
            if (rawQualCode.Length == 0 || rawPqmCode.Length == 0) return null;

            if (HasSameLeadingDigits(rawQualCode, rawPqmCode, 4))
                return "first 4 digits matched";

            var trimmedQualCode = TrimLeadingZeros(rawQualCode);
            var trimmedPqmCode = TrimLeadingZeros(rawPqmCode);

            if (HasSameLeadingDigits(trimmedQualCode, trimmedPqmCode, 4))
                return "first 4 digits matched after removing leading zeros";

            if (HasSameLeadingDigits(rawQualCode, rawPqmCode, 3))
                return "first 3 digits matched";

            if (HasSameLeadingDigits(trimmedQualCode, trimmedPqmCode, 3))
                return "first 3 digits matched after removing leading zeros";

            return null;
        }

        private static string ClassifyPopulationType(string? qualHeqfType, ISet<string> postgraduateTypeCodes) =>
            postgraduateTypeCodes.Contains(NormValue(qualHeqfType)) ? "Postgraduate" : "Undergraduate";

        private static bool NumericMatch(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return true;
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            if (decimal.TryParse(a.Trim(), out var da) && decimal.TryParse(b.Trim(), out var db))
                return da == db;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHeqfIndicated(string? accreditationRef, IReadOnlyList<string> codes)
        {
            if (string.IsNullOrWhiteSpace(accreditationRef)) return false;
            var upper = accreditationRef.Trim().ToUpperInvariant();
            return codes.Any(code => upper.Contains(code.Trim().ToUpperInvariant()));
        }

        private static List<string> ParseHeqfCodes(string csv) =>
            (csv ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(c => c.Length > 0)
                .ToList();

        private static bool IsMPrefixQualificationCode(string? qualCode) =>
            Regex.IsMatch(NormValue(qualCode), @"^M.{5}$", RegexOptions.CultureInvariant);

        private static (string PopulationType, string Note) ClassifyPopulationTypeFull(
            string? qualCode, string? qualHeqfType, ISet<string> postgraduateTypeCodes, bool useMPrefixPopulationSplit)
        {
            var notes = new List<string>();
            if (useMPrefixPopulationSplit && IsMPrefixQualificationCode(qualCode))
                notes.Add("qualification code matched M_____");
            if (postgraduateTypeCodes.Contains(NormValue(qualHeqfType)))
                notes.Add($"QUAL type {qualHeqfType} is in the configured postgraduate _005 list");
            if (notes.Count > 0)
                return ("Postgraduate", string.Join("; ", notes));
            return ("Undergraduate", "qualification did not match the configured postgraduate rules");
        }

        // ── Internal record types ─────────────────────────────

        private record QualRecord(
            string QualId,
            string QualName,
            string QualApproval,
            string QualHeqfType,
            string Qual002,
            string Qual053,
            string Qual054,
            string Qual081,
            string Qual082,
            string Qual083,
            string Qual084,
            string Qual085,
            string Qual086,
            string Qual087,
            string Qual088,
            string Qual089,
            string Qual090,
            string? CesmCode);
        private record PqmRow(string? Name, string? HeqfType, string? Code, string? Code1, string? MinTimeTotal, string? WIL, string? Accreditation, string? TotalSubsidy);

        // ── Core validation (unchanged — pure in-memory C#) ───────────────────────────────

        private static Rule11ValidationRow ValidateRecord(int rowNo, QualRecord q, List<PqmRow> pqm, ISet<string> postgraduateTypeCodes, IReadOnlyList<string> heqfCodes, bool useMPrefixPopulationSplit)
        {
            var hNorm    = NormName(q.QualName);
            var heqfNorm = NormValue(q.QualHeqfType);
            var codeNorm = NormValue(q.CesmCode);
            var (populationType, populationClassificationNote) = ClassifyPopulationTypeFull(q.QualId, q.QualHeqfType, postgraduateTypeCodes, useMPrefixPopulationSplit);

            Rule11ValidationRow BuildRow(PqmRow? bestPqm, bool nameMatch, bool heqfTypeMatch, bool cesmCodeMatch, bool needsReview, string baseResult, string baseReason)
            {
                var failed = new List<string>();
                bool c2 = heqfTypeMatch;
                bool c3 = false, c4 = false, c5 = false, c6 = false;
                string? expectedHeqf = null;

                if (bestPqm != null)
                {
                    c3 = NumericMatch(q.Qual053, bestPqm.MinTimeTotal);
                    c4 = NumericMatch(q.Qual054, bestPqm.WIL);
                    expectedHeqf = IsHeqfIndicated(bestPqm.Accreditation, heqfCodes) ? "Y" : "N";
                    c5 = string.Equals(NormValue(q.Qual084), expectedHeqf, StringComparison.OrdinalIgnoreCase);
                    c6 = NumericMatch(q.Qual090, bestPqm.TotalSubsidy);
                    if (!c2) failed.Add("C2");
                    if (!c3) failed.Add("C3");
                    if (!c4) failed.Add("C4");
                    if (!c5) failed.Add("C5");
                    if (!c6) failed.Add("C6");
                }

                var finalResult = baseResult == "FAIL" || failed.Count > 0 ? "FAIL" : baseResult;
                var finalReason = failed.Count > 0
                    ? $"{baseReason}. Additional controls failed: {string.Join(", ", failed)}"
                    : baseReason;

                return new Rule11ValidationRow
                {
                    ValidationNumber = rowNo,
                    QualId           = q.QualId,
                    QualName         = q.QualName,
                    QualApproval     = q.QualApproval,
                    QualHeqfType     = q.QualHeqfType,
                    Qual002          = q.Qual002,
                    Qual053          = q.Qual053,
                    Qual054          = q.Qual054,
                    Qual081          = q.Qual081,
                    Qual082          = q.Qual082,
                    Qual083          = q.Qual083,
                    Qual084          = q.Qual084,
                    Qual085          = q.Qual085,
                    Qual086          = q.Qual086,
                    Qual087          = q.Qual087,
                    Qual088          = q.Qual088,
                    Qual089          = q.Qual089,
                    Qual090          = q.Qual090,
                    PopulationType   = populationType,
                    PopulationClassificationNote = populationClassificationNote,
                    CesmCode         = q.CesmCode,
                    PqmName          = bestPqm?.Name?.Trim(),
                    PqmHeqfType      = bestPqm?.HeqfType?.Trim(),
                    PqmCode          = bestPqm?.Code?.Trim(),
                    PqmCesmCode1     = bestPqm?.Code1?.Trim(),
                    PqmMinTimeTotal  = bestPqm?.MinTimeTotal?.Trim(),
                    PqmWIL           = bestPqm?.WIL?.Trim(),
                    PqmAccreditation = bestPqm?.Accreditation?.Trim(),
                    PqmTotalSubsidy  = bestPqm?.TotalSubsidy?.Trim(),
                    NameMatch        = nameMatch,
                    HeqfTypeMatch    = heqfTypeMatch,
                    CesmCodeMatch    = cesmCodeMatch,
                    NeedsReview      = needsReview && finalResult == "PASS",
                    C2_TypeMatch     = c2,
                    C3_MinTimeMatch  = c3,
                    C4_WILMatch      = c4,
                    C5_HeqfMatch     = c5,
                    C5_ExpectedHeqf  = expectedHeqf,
                    C6_SubsidyMatch  = c6,
                    FailedControls   = failed,
                    ValidationResult = finalResult,
                    ExceptionReason  = finalReason
                };
            }

            var nameRows = pqm
                .Where(p => string.Equals(NormName(p.Name), hNorm, StringComparison.Ordinal))
                .ToList();

            if (nameRows.Count == 0)
            {
                return BuildRow(null, false, false, false, false, "FAIL",
                    "Qualification name not found in PQM (Authorised_Qualification_Name)");
            }

            var tripleMatch = nameRows
                .Where(p => string.Equals(NormValue(p.HeqfType), heqfNorm, StringComparison.Ordinal)
                         && string.Equals(NormValue(p.Code), codeNorm, StringComparison.Ordinal))
                .ToList();

            if (tripleMatch.Count > 0)
            {
                return BuildRow(tripleMatch[0], true, true, true, false, "PASS",
                    "All three criteria matched on the same PQM row: qualification name (_003), HEQF type (_005), and CESM code (_006)");
            }

            var heqfMatch = nameRows
                .Where(p => string.Equals(NormValue(p.HeqfType), heqfNorm, StringComparison.Ordinal))
                .ToList();

            if (heqfMatch.Count > 0)
            {
                var reviewMatch = heqfMatch
                    .Select(p => new { Row = p, Reason = GetCesmReviewMatchReason(q.CesmCode, p.Code) })
                    .FirstOrDefault(m => m.Reason != null);

                if (reviewMatch != null)
                {
                    return BuildRow(reviewMatch.Row, true, true, true, true, "PASS",
                        $"Pass - review required: Name and HEQF matched, and the CESM leading digits also matched ({reviewMatch.Reason}) even though the full code differs. CESM._006: '{q.CesmCode}' | PQM CESM_Code: '{reviewMatch.Row.Code?.Trim()}'");
                }

                var best = heqfMatch[0];
                var pqmCodeValues = string.Join(" | ", heqfMatch.Take(3).Select(p => p.Code?.Trim()).Where(v => v != null).Distinct());
                return BuildRow(best, true, true, false, false, "FAIL",
                    $"Name and HEQF matched but CESM code mismatch — CESM._006: '{q.CesmCode}' | PQM CESM_Code: '{pqmCodeValues}'");
            }

            var bestName = nameRows[0];
            var pqmHeqfValues = string.Join(" | ", nameRows.Take(3).Select(p => p.HeqfType?.Trim()).Where(v => v != null).Distinct());
            return BuildRow(bestName, true, false, false, false, "FAIL",
                $"Name matched but HEQF_Qual_Type mismatch — QUAL._005: '{q.QualHeqfType}' | PQM HEQF_Qual_Type: '{pqmHeqfValues}'");
        }

        private static List<Rule11ControlSummary> BuildRule11ControlSummaries(
            List<Rule11ValidationRow> rows,
            string qualTable, string qualMinTimeTotalCol, string pqmMinTimeTotalCol,
            string qualMinTimeWilCol, string pqmWilCol,
            string qualHeqfCol, string pqmAccreditationCol,
            string qualTotalSubsidyCol, string pqmTotalSubsidyCol)
        {
            var matched = rows.Where(r => r.NameMatch && r.HeqfTypeMatch).ToList();
            return new List<Rule11ControlSummary>
            {
                new()
                {
                    ControlId = "C1", ControlLabel = "Name + HEQF + CESM (base match)",
                    CriteriaText = $"QUAL.Name = PQM.Name AND QUAL.HEQF = PQM.HEQF AND QUAL.CESM = PQM.CESM ({qualTable})",
                    PassCount = rows.Count(r => r.NameMatch && r.HeqfTypeMatch && r.CesmCodeMatch),
                    FailCount = rows.Count(r => !(r.NameMatch && r.HeqfTypeMatch && r.CesmCodeMatch)),
                    Status = rows.All(r => r.NameMatch && r.HeqfTypeMatch && r.CesmCodeMatch) ? "PASS" : "FAIL"
                },
                new()
                {
                    ControlId = "C2", ControlLabel = "HEQF type match",
                    CriteriaText = $"{qualTable}.HEQF_Type matches PQM row",
                    PassCount = matched.Count(r => r.C2_TypeMatch), FailCount = matched.Count(r => !r.C2_TypeMatch),
                    Status = matched.All(r => r.C2_TypeMatch) ? "PASS" : "FAIL"
                },
                new()
                {
                    ControlId = "C3", ControlLabel = "Minimum time total",
                    CriteriaText = $"{qualTable}.{qualMinTimeTotalCol} = PQM.{pqmMinTimeTotalCol}",
                    PassCount = matched.Count(r => r.C3_MinTimeMatch), FailCount = matched.Count(r => !r.C3_MinTimeMatch),
                    Status = matched.All(r => r.C3_MinTimeMatch) ? "PASS" : "FAIL"
                },
                new()
                {
                    ControlId = "C4", ControlLabel = "Work-integrated learning",
                    CriteriaText = $"{qualTable}.{qualMinTimeWilCol} = PQM.{pqmWilCol}",
                    PassCount = matched.Count(r => r.C4_WILMatch), FailCount = matched.Count(r => !r.C4_WILMatch),
                    Status = matched.All(r => r.C4_WILMatch) ? "PASS" : "FAIL"
                },
                new()
                {
                    ControlId = "C5", ControlLabel = "HEQF accreditation indicator",
                    CriteriaText = $"{qualTable}.{qualHeqfCol} matches whether PQM.{pqmAccreditationCol} indicates HEQF",
                    PassCount = matched.Count(r => r.C5_HeqfMatch), FailCount = matched.Count(r => !r.C5_HeqfMatch),
                    Status = matched.All(r => r.C5_HeqfMatch) ? "PASS" : "FAIL"
                },
                new()
                {
                    ControlId = "C6", ControlLabel = "Total subsidy units",
                    CriteriaText = $"{qualTable}.{qualTotalSubsidyCol} = PQM.{pqmTotalSubsidyCol}",
                    PassCount = matched.Count(r => r.C6_SubsidyMatch), FailCount = matched.Count(r => !r.C6_SubsidyMatch),
                    Status = matched.All(r => r.C6_SubsidyMatch) ? "PASS" : "FAIL"
                }
            };
        }

        // ── Engagement data source (uploaded tables, not a live SQL Server) ────────────────

        public async Task<Rule11TableListResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule11TableListResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule11TableListResult
                {
                    Success = true,
                    Tables = tables,
                    AutoQualTable = tables.FirstOrDefault(t => t.Equals("dbo_QUAL", StringComparison.OrdinalIgnoreCase) || t.Equals("dbo_qual", StringComparison.OrdinalIgnoreCase)),
                    AutoCesmTable = tables.FirstOrDefault(t => t.Equals("dbo_CESM", StringComparison.OrdinalIgnoreCase) || t.Equals("dbo_cesm", StringComparison.OrdinalIgnoreCase)),
                    AutoPqmTable = tables.FirstOrDefault(t => t.Contains("PQM", StringComparison.OrdinalIgnoreCase))
                };
            }
            catch (Exception ex)
            {
                return new Rule11TableListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<ColumnListResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);

                string? autoSelected = tableRole?.ToLowerInvariant() switch
                {
                    "qual_id"        => columns.FirstOrDefault(c => c.Equals("_001", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_name"      => columns.FirstOrDefault(c => c.Equals("_003", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_approval"  => columns.FirstOrDefault(c => c.Equals("_004", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_heqf_type" => columns.FirstOrDefault(c => c.Equals("_005", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "cesm_id"        => columns.FirstOrDefault(c => c.Equals("_001", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "cesm_code"      => columns.FirstOrDefault(c => c.Equals("CESM_Code", StringComparison.OrdinalIgnoreCase) || c.Equals("CESM_Code1", StringComparison.OrdinalIgnoreCase) || c.Equals("_006", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_name"       => columns.FirstOrDefault(c => c.Contains("Authorised", StringComparison.OrdinalIgnoreCase) || c.Contains("Qualification_Name", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_heqf_type"  => columns.FirstOrDefault(c => c.Contains("HEQF", StringComparison.OrdinalIgnoreCase) || c.Contains("Qual_Type", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_code"       => columns.FirstOrDefault(c => c.Equals("CESM_Code", StringComparison.OrdinalIgnoreCase) || c.Equals("CESM_Code1", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_min_time_total" => columns.FirstOrDefault(c => c.Equals("_053", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_min_time_wil"   => columns.FirstOrDefault(c => c.Equals("_054", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_heqf"           => columns.FirstOrDefault(c => c.Equals("_084", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_total_subsidy"  => columns.FirstOrDefault(c => c.Equals("_090", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_cesm_code1"     => columns.FirstOrDefault(c => c.Equals("CESM_CODE1", StringComparison.OrdinalIgnoreCase) || c.Equals("CESM_Code1", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_min_time_total" => columns.FirstOrDefault(c => c.Equals("Total2", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_wil"            => columns.FirstOrDefault(c => c.Equals("WIL_EL2", StringComparison.OrdinalIgnoreCase) || c.Contains("WIL", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_accreditation"  => columns.FirstOrDefault(c => c.Contains("Accreditation", StringComparison.OrdinalIgnoreCase) || c.Contains("CHE_HEQC", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_total_subsidy"  => columns.FirstOrDefault(c => c.Equals("Total2", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    _                => columns.FirstOrDefault()
                };

                return new ColumnListResult { Success = true, Columns = columns, AutoSelected = autoSelected };
            }
            catch (Exception ex)
            {
                return new ColumnListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule11FilterValueResult> GetFilterValuesAsync(int clientId, string qualTable, string approvalColumn)
        {
            try
            {
                var values = await _datasets.GetDistinctColumnValuesAsync(clientId, qualTable, approvalColumn, take: 50);
                var options = values.Select(v => new Rule11FilterValueOption
                {
                    Value = v.Value,
                    Count = (int)Math.Min(v.Count, int.MaxValue),
                    Label = $"{v.Value} ({v.Count:N0} records)"
                }).ToList();

                return new Rule11FilterValueResult
                {
                    Success = true,
                    Options = options,
                    DefaultValue = options.FirstOrDefault()?.Value
                };
            }
            catch (Exception ex)
            {
                return new Rule11FilterValueResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule11VerifyResult> VerifyDataAsync(Rule11VerifyRequest request)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.QualTable, request.QualIdCol, request.QualApprovalCol);
                await _datasets.GetValidatedColumnsAsync(request.ClientId, request.CesmTable);
                if (!(await _datasets.GetValidatedColumnsAsync(request.ClientId, request.CesmTable)).Contains(request.CesmIdCol, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Column '{request.CesmIdCol}' was not found in table '{request.CesmTable}'.");
                await _datasets.GetValidatedColumnsAsync(request.ClientId, request.PqmTable);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var approvalValue = NormalizeFilterValue(request.QualApprovalFilterValue, "A");

                await using var command = connection.CreateCommand();
                command.CommandText = $@"
SELECT
    (SELECT COUNT(*) FROM ""{schema}"".""{request.QualTable}"") AS qual_total,
    (SELECT COUNT(*) FROM ""{schema}"".""{request.CesmTable}"") AS cesm_total,
    (SELECT COUNT(*) FROM ""{schema}"".""{request.PqmTable}"") AS pqm_total,
    (SELECT COUNT(*) FROM ""{schema}"".""{request.QualTable}"" q
       LEFT JOIN ""{schema}"".""{request.CesmTable}"" c ON q.""{request.QualIdCol}"" = c.""{request.CesmIdCol}""
       WHERE UPPER(TRIM(q.""{request.QualApprovalCol}""::text)) = @approvalValue
    ) AS merged_total;";
                command.Parameters.AddWithValue("approvalValue", approvalValue);

                await using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new Rule11VerifyResult
                    {
                        Success     = true,
                        QualTotal   = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0)),
                        CesmTotal   = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1)),
                        PqmTotal    = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2)),
                        MergedTotal = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3))
                    };
                }
                return new Rule11VerifyResult { Success = false, Error = "No data returned" };
            }
            catch (Exception ex)
            {
                return new Rule11VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule11ValidationSummary> RunValidationAsync(
            Rule11ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                await ValidateColumnsExistAsync(request.ClientId, request.QualTable, request.QualIdCol, request.QualNameCol, request.QualApprovalCol, request.QualHeqfTypeCol);
                var cesmColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.CesmTable);
                foreach (var col in new[] { request.CesmIdCol, request.CesmCodeCol })
                    if (!cesmColumns.Contains(col, StringComparer.Ordinal))
                        throw new InvalidOperationException($"Column '{col}' was not found in table '{request.CesmTable}'.");
                var pqmColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.PqmTable);
                foreach (var col in new[] { request.PqmNameCol, request.PqmHeqfTypeCol, request.PqmCodeCol })
                    if (!pqmColumns.Contains(col, StringComparer.Ordinal))
                        throw new InvalidOperationException($"Column '{col}' was not found in table '{request.PqmTable}'.");

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var approvalValue = NormalizeFilterValue(request.QualApprovalFilterValue, "A");
                var typeCodes = ParseQualTypeCodes(request.QualTypeCodesText);
                var postgraduateTypeCodes = new HashSet<string>(typeCodes, StringComparer.OrdinalIgnoreCase);
                var qmt  = string.IsNullOrWhiteSpace(request.QualMinTimeTotalCol) ? "_053" : request.QualMinTimeTotalCol;
                var qwil = string.IsNullOrWhiteSpace(request.QualMinTimeWilCol)   ? "_054" : request.QualMinTimeWilCol;
                var qhq  = string.IsNullOrWhiteSpace(request.QualHeqfCol)         ? "_084" : request.QualHeqfCol;
                var qsub = string.IsNullOrWhiteSpace(request.QualTotalSubsidyCol) ? "_090" : request.QualTotalSubsidyCol;
                var pc1  = string.IsNullOrWhiteSpace(request.PqmCesmCode1Col)     ? "CESM_CODE1" : request.PqmCesmCode1Col;
                var pmt  = string.IsNullOrWhiteSpace(request.PqmMinTimeTotalCol)  ? "Total2" : request.PqmMinTimeTotalCol;
                var pwil = string.IsNullOrWhiteSpace(request.PqmWilCol)           ? "WIL_EL2" : request.PqmWilCol;
                var pacc = string.IsNullOrWhiteSpace(request.PqmAccreditationCol) ? "CHE_HEQC_Accreditation_Approval_Ref_Nr" : request.PqmAccreditationCol;
                var psub = string.IsNullOrWhiteSpace(request.PqmTotalSubsidyCol)  ? "Total2" : request.PqmTotalSubsidyCol;
                var heqfCodes = ParseHeqfCodes(string.IsNullOrWhiteSpace(request.HeqfIndicatorCodesCsv) ? "H/,HEQF,HEQSF" : request.HeqfIndicatorCodesCsv);
                var useMPrefixPopulationSplit = request.UseMPrefixPopulationSplit || request.ExcludeMPrefixPattern;

                // Load QUAL LEFT JOIN CESM
                var qualRecords = new List<QualRecord>();
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $@"
SELECT q.""{request.QualIdCol}"", q.""{request.QualNameCol}"", q.""{request.QualApprovalCol}"", q.""{request.QualHeqfTypeCol}"",
       q.""_002"", q.""{qmt}"", q.""{qwil}"", q.""_081"", q.""_082"", q.""_083"",
       q.""{qhq}"", q.""_085"", q.""_086"", q.""_087"", q.""_088"", q.""_089"", q.""{qsub}"",
       c.""{request.CesmCodeCol}""
FROM ""{schema}"".""{request.QualTable}"" q
LEFT JOIN ""{schema}"".""{request.CesmTable}"" c ON q.""{request.QualIdCol}"" = c.""{request.CesmIdCol}""
WHERE UPPER(TRIM(q.""{request.QualApprovalCol}""::text)) = @approvalValue;";
                    cmd.Parameters.AddWithValue("approvalValue", approvalValue);

                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                    {
                        string S(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                        qualRecords.Add(new QualRecord(
                            QualId:    S(0),
                            QualName:  S(1),
                            QualApproval:  S(2),
                            QualHeqfType:  S(3),
                            Qual002:   S(4),
                            Qual053:   S(5),
                            Qual054:   S(6),
                            Qual081:   S(7),
                            Qual082:   S(8),
                            Qual083:   S(9),
                            Qual084:   S(10),
                            Qual085:   S(11),
                            Qual086:   S(12),
                            Qual087:   S(13),
                            Qual088:   S(14),
                            Qual089:   S(15),
                            Qual090:   S(16),
                            CesmCode:  r.IsDBNull(17) ? null : Convert.ToString(r.GetValue(17), CultureInfo.InvariantCulture)));
                    }
                }

                // Load PQM
                var pqm = new List<PqmRow>();
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $@"SELECT ""{request.PqmNameCol}"", ""{request.PqmHeqfTypeCol}"", ""{request.PqmCodeCol}"", ""{pc1}"", ""{pmt}"", ""{pwil}"", ""{pacc}"", ""{psub}"" FROM ""{schema}"".""{request.PqmTable}"";";
                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                    {
                        string? S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
                        pqm.Add(new PqmRow(S(0), S(1), S(2), S(3), S(4), S(5), S(6), S(7)));
                    }
                }

                // Validate in memory
                var validationRows = qualRecords
                    .Select((q, idx) => ValidateRecord(idx + 1, q, pqm, postgraduateTypeCodes, heqfCodes, useMPrefixPopulationSplit))
                    .ToList();

                var total       = validationRows.Count;
                var passCount   = validationRows.Count(row => row.ValidationResult == "PASS");
                var failCount   = validationRows.Count(row => row.ValidationResult == "FAIL");
                var reviewCount = validationRows.Count(row => row.NeedsReview);
                var rate        = total > 0 ? Math.Round((decimal)failCount / total * 100, 2) : 0;

                var undergraduateCount = validationRows.Count(r => r.PopulationType == "Undergraduate");
                var postgraduateCount  = validationRows.Count(r => r.PopulationType == "Postgraduate");

                var controlSummaries = BuildRule11ControlSummaries(validationRows,
                    request.QualTable, qmt, pmt, qwil, pwil, qhq, pacc, qsub, psub);

                var exceptions = validationRows
                    .Where(row => row.ValidationResult == "FAIL")
                    .Select(row => new Rule11ExceptionRecord
                    {
                        ValidationNumber = row.ValidationNumber,
                        QualId           = row.QualId,
                        QualName         = row.QualName,
                        QualApproval     = row.QualApproval,
                        QualHeqfType     = row.QualHeqfType,
                        Qual002          = row.Qual002,
                        Qual053          = row.Qual053,
                        Qual054          = row.Qual054,
                        Qual081          = row.Qual081,
                        Qual082          = row.Qual082,
                        Qual083          = row.Qual083,
                        Qual084          = row.Qual084,
                        Qual085          = row.Qual085,
                        Qual086          = row.Qual086,
                        Qual087          = row.Qual087,
                        Qual088          = row.Qual088,
                        Qual089          = row.Qual089,
                        Qual090          = row.Qual090,
                        PopulationType   = row.PopulationType,
                        CesmCode         = row.CesmCode,
                        PqmName          = row.PqmName,
                        PqmHeqfType      = row.PqmHeqfType,
                        PqmCode          = row.PqmCode,
                        PqmCesmCode1     = row.PqmCesmCode1,
                        PqmMinTimeTotal  = row.PqmMinTimeTotal,
                        PqmWIL           = row.PqmWIL,
                        PqmAccreditation = row.PqmAccreditation,
                        PqmTotalSubsidy  = row.PqmTotalSubsidy,
                        NameMatch        = row.NameMatch,
                        HeqfTypeMatch    = row.HeqfTypeMatch,
                        CesmCodeMatch    = row.CesmCodeMatch,
                        NeedsReview      = row.NeedsReview,
                        C2_TypeMatch     = row.C2_TypeMatch,
                        C3_MinTimeMatch  = row.C3_MinTimeMatch,
                        C4_WILMatch      = row.C4_WILMatch,
                        C5_HeqfMatch     = row.C5_HeqfMatch,
                        C5_ExpectedHeqf  = row.C5_ExpectedHeqf,
                        C6_SubsidyMatch  = row.C6_SubsidyMatch,
                        FailedControls   = row.FailedControls,
                        PopulationClassificationNote = row.PopulationClassificationNote,
                        ValidationResult = row.ValidationResult,
                        ExceptionReason  = row.ExceptionReason ?? ""
                    })
                    .ToList();

                var summary = new Rule11ValidationSummary
                {
                    Success          = true,
                    TotalValidated   = total,
                    PassCount        = passCount,
                    FailCount        = failCount,
                    ReviewCount      = reviewCount,
                    ExceptionRate    = rate,
                    Status           = failCount == 0 ? "PASS" : "FAIL",
                    Timestamp        = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    QualTable        = request.QualTable,
                    QualIdCol        = request.QualIdCol,
                    QualNameCol      = request.QualNameCol,
                    QualApprovalCol  = request.QualApprovalCol,
                    QualHeqfTypeCol  = request.QualHeqfTypeCol,
                    QualApprovalFilterValue = approvalValue,
                    QualTypeCodesText = string.Join(", ", typeCodes),
                    CesmTable        = request.CesmTable,
                    CesmIdCol        = request.CesmIdCol,
                    CesmCodeCol      = request.CesmCodeCol,
                    PqmTable         = request.PqmTable,
                    PqmNameCol       = request.PqmNameCol,
                    PqmHeqfTypeCol   = request.PqmHeqfTypeCol,
                    PqmCodeCol       = request.PqmCodeCol,
                    PqmCesmCode1Col  = pc1,
                    PqmMinTimeTotalCol = pmt,
                    PqmWilCol        = pwil,
                    PqmAccreditationCol = pacc,
                    PqmTotalSubsidyCol = psub,
                    QualMinTimeTotalCol = qmt,
                    QualMinTimeWilCol   = qwil,
                    QualHeqfCol         = qhq,
                    QualTotalSubsidyCol = qsub,
                    HeqfIndicatorCodesCsv = request.HeqfIndicatorCodesCsv ?? "H/,HEQF,HEQSF",
                    UseMPrefixPopulationSplit = useMPrefixPopulationSplit,
                    ExcludeMPrefixPattern = request.ExcludeMPrefixPattern,
                    UndergraduateCount = undergraduateCount,
                    PostgraduateCount  = postgraduateCount,
                    ControlSummaries   = controlSummaries,
                    ClientId         = request.ClientId,
                    ValidationRows   = validationRows,
                    Exceptions       = exceptions
                };

                if (request.ClientId > 0)
                {
                    await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 11);
                    var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
                    {
                        ClientId = request.ClientId,
                        RuleNumber = 11,
                        RuleName = "QUAL vs CESM vs PQM Validation",
                        Status = summary.Status,
                        TotalRecords = summary.TotalValidated,
                        PassCount = summary.PassCount,
                        FailCount = summary.FailCount,
                        ExceptionRate = summary.ExceptionRate,
                        StudTable = request.QualTable,
                        DeceasedTable = request.CesmTable,
                        StudColumn = request.PqmTable,
                        DeceasedColumn = "",
                        ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.Exceptions)),
                        ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
                    }, userEmail, userName);

                    summary.SavedRunId = runId;
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule11ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule11WorkspaceSaveResult> SaveWorkspaceAsync(
            Rule11ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule11WorkspaceSaveResult { Success = false, Error = "Run validation before saving the workspace." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule11WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.QualTable,
                    DeceasedTable = request.CesmTable,
                    StudColumn = request.PqmTable,
                    DeceasedColumn = ""
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule11WorkspaceSaveResult
                {
                    Success = true,
                    Message = clearedSignoffs > 0
                        ? "Workspace saved. Existing signoffs were removed and the run must be reviewed again."
                        : "Workspace saved and marked for review again.",
                    SignoffsCleared     = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace           = workspace
                };
            }
            catch (Exception ex)
            {
                return new Rule11WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule11WorkspaceSaveResult> BeginWorkspaceEditAsync(
            int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule11WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule11WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = clearedSignoffs > 0
                        ? "Editing has begun. Existing signoffs were removed so the workspace must be reviewed again."
                        : "Editing has begun.",
                    SignoffsCleared     = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace           = workspace
                };
            }
            catch (Exception ex)
            {
                return new Rule11WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule11WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(
            int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 11);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null)
                ApplyBrowserPreview(summary);

            var workspace = new Rule11WorkspaceStateViewModel
            {
                RunId         = row.RunId,
                ClientId      = row.ClientId,
                QualTable     = row.StudTable,
                CesmTable     = row.DeceasedTable,
                PqmTable      = row.StudColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt         = row.LastEditedAt,
                Summary              = summary
            };

            if (summary != null)
            {
                workspace.QualTable       = summary.QualTable;
                workspace.CesmTable       = summary.CesmTable;
                workspace.PqmTable        = summary.PqmTable;
                workspace.QualIdCol       = summary.QualIdCol;
                workspace.QualNameCol     = summary.QualNameCol;
                workspace.QualApprovalCol = summary.QualApprovalCol;
                workspace.QualHeqfTypeCol = summary.QualHeqfTypeCol;
                workspace.QualApprovalFilterValue = summary.QualApprovalFilterValue;
                workspace.QualTypeCodesText = summary.QualTypeCodesText;
                workspace.CesmIdCol       = summary.CesmIdCol;
                workspace.CesmCodeCol     = summary.CesmCodeCol;
                workspace.PqmNameCol      = summary.PqmNameCol;
                workspace.PqmHeqfTypeCol  = summary.PqmHeqfTypeCol;
                workspace.PqmCodeCol      = summary.PqmCodeCol;
                workspace.PqmCesmCode1Col   = summary.PqmCesmCode1Col;
                workspace.PqmMinTimeTotalCol = summary.PqmMinTimeTotalCol;
                workspace.PqmWilCol         = summary.PqmWilCol;
                workspace.PqmAccreditationCol = summary.PqmAccreditationCol;
                workspace.PqmTotalSubsidyCol = summary.PqmTotalSubsidyCol;
                workspace.QualMinTimeTotalCol = summary.QualMinTimeTotalCol;
                workspace.QualMinTimeWilCol   = summary.QualMinTimeWilCol;
                workspace.QualHeqfCol         = summary.QualHeqfCol;
                workspace.QualTotalSubsidyCol = summary.QualTotalSubsidyCol;
                workspace.HeqfIndicatorCodesCsv = summary.HeqfIndicatorCodesCsv;
                workspace.UseMPrefixPopulationSplit = summary.UseMPrefixPopulationSplit;
                workspace.ExcludeMPrefixPattern = summary.ExcludeMPrefixPattern;
            }

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            workspace.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(clientId, currentUser.Id) ?? ""
                : "";

            var signoffs = await _systemDb.GetRuleRunSignoffsAsync(workspace.RunId!.Value, currentUser?.Id);
            workspace.HasDataAnalystSignoff = signoffs.Any(s =>
                string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));

            var currentRoleSignoff = signoffs.FirstOrDefault(s =>
                ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, workspace.CurrentUserEngagementRole));
            workspace.CurrentUserHasSignedOff   = currentRoleSignoff != null;
            workspace.CurrentUserSignoffComment = currentRoleSignoff?.Comment ?? "";
            workspace.IsWorkspaceSaved          = await _systemDb.IsWorkspaceSavedAsync(workspace.RunId!.Value);

            if (workspace.Summary != null)
                workspace.Summary.SavedRunId = workspace.RunId;

            if (string.IsNullOrWhiteSpace(workspace.CurrentStatus))
                workspace.CurrentStatus = workspace.Summary?.Status ?? "";

            return workspace;
        }

        // Rule 11's RunValidationAsync always auto-saves a new run as a side effect (see
        // SaveValidationRunAsync above), and there is no separate non-saving full-analysis path to
        // reuse for a population check the way Rule26/Rule34/Rule38 do (calling their analysis
        // method again would silently create a duplicate saved run every time a user opens the
        // download panel). The saved/current summary's TotalValidated is already the full,
        // untruncated population count (GetSavedRunAsync deserializes ResultsJSON verbatim and only
        // ApplyBrowserPreview — never called here — trims the Exceptions/ValidationRows lists), so
        // this reads that value directly instead of re-running the query.
        public Task<int> GetPopulationCountAsync(Rule11ValidationSummary summary) =>
            Task.FromResult(summary?.TotalValidated ?? 0);

        public async Task<Rule11RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 11);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            EnsurePopulationTypes(summary);

            var viewModel = new Rule11RunReviewViewModel
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

            viewModel.Signoffs = await _systemDb.GetRuleRunSignoffsAsync(runId, currentUser?.Id);
            viewModel.HasDataAnalystSignoff = viewModel.Signoffs.Any(s =>
                string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));

            return viewModel;
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Validation run was not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(engagementRole))
                throw new InvalidOperationException("Only assigned data analysts, managers, and directors can sign off a validation run.");

            if (!string.Equals(engagementRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase) &&
                !await _systemDb.HasRuleSignoffRoleAsync(runId, "DataAnalyst"))
            {
                throw new InvalidOperationException("The assigned data analyst must sign off before this review can be completed.");
            }

            await _systemDb.AddOrUpdateRuleSignoffAsync(runId, clientId, reviewer.Id, engagementRole!, comment);
        }

        public async Task RemoveSignoffAsync(int runId, string reviewerEmail)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Validation run was not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public string GenerateSql(Rule11ValidationRequest request)
        {
            var qt  = request.QualTable;
            var ct  = request.CesmTable;
            var pt  = request.PqmTable;
            var qi  = request.QualIdCol;
            var qn  = request.QualNameCol;
            var qa  = request.QualApprovalCol;
            var qht = request.QualHeqfTypeCol;
            var ci  = request.CesmIdCol;
            var cc  = request.CesmCodeCol;
            var pn  = request.PqmNameCol;
            var pht = request.PqmHeqfTypeCol;
            var pc  = request.PqmCodeCol;
            var approvalValue = NormalizeFilterValue(request.QualApprovalFilterValue, "A");
            var typeCodes = ParseQualTypeCodes(request.QualTypeCodesText);
            var typeCodeSql = string.Join(", ", typeCodes.Select(code => $"'{EscapeSqlString(code)}'"));
            var populationTypeSql = $"CASE WHEN UPPER(TRIM(q.\"{qht}\"::text)) IN ({typeCodeSql}) THEN 'Postgraduate' ELSE 'Undergraduate' END";

            return $@"-- ============================================================================
-- HEMIS RULE 11: QUAL vs CESM vs PQM VALIDATION
-- 100% POPULATION WITH PROPER EXTRACTION
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- ============================================================================

-- ============================================================================
-- STEP 1: EXTRACT 100% APPROVED QUALIFICATION POPULATION
-- ============================================================================

CREATE TEMP TABLE r11_extracted_population AS
SELECT
    ROW_NUMBER() OVER (ORDER BY q.""{qi}"") AS extract_number,
    q.""{qi}""  AS ""Qualification_Code"",
    q.""_002""  AS ""Previous_Years_Qualification_Code"",
    q.""{qn}""  AS ""Qualification_Name_Designator"",
    q.""{qa}""  AS ""Approval_Status"",
    q.""{qht}"" AS ""Qualification_Type_Descriptor"",
    q.""_053""  AS ""Minimum_Time_Total"",
    q.""_054""  AS ""Minimum_Time_Experiential"",
    q.""_081""  AS ""Institution_Programme_Name"",
    q.""_082""  AS ""Qualifier"",
    q.""_083""  AS ""Abbreviation"",
    q.""_084""  AS ""Legacy_Indicator"",
    q.""_085""  AS ""NQF_Exit_Level"",
    q.""_086""  AS ""Minimum_Total_Credits"",
    q.""_087""  AS ""Minimum_Credits_At_Level"",
    q.""_088""  AS ""Maximum_Credits_At_Level"",
    q.""_089""  AS ""Mode_Of_Delivery"",
    q.""_090""  AS ""Total_Subsidy_Units"",
    c.""{cc}""  AS ""CESM_Code"",
    {populationTypeSql} AS ""Population_Type""
FROM ""{{schema}}"".""{qt}"" q
LEFT JOIN ""{{schema}}"".""{ct}"" c ON q.""{qi}"" = c.""{ci}""
WHERE UPPER(TRIM(q.""{qa}""::text)) = '{approvalValue}';

-- ============================================================================
-- STEP 2: VALIDATE EXTRACTED POPULATION AGAINST PQM
-- ============================================================================

CREATE TEMP TABLE r11_validation AS
SELECT
    e.extract_number,
    e.""Qualification_Code"",
    e.""Previous_Years_Qualification_Code"",
    e.""Qualification_Name_Designator"",
    e.""Approval_Status"",
    e.""Qualification_Type_Descriptor"",
    e.""Population_Type"",
    e.""CESM_Code"",
    p.""{pn}""  AS ""PQM_Qualification_Name"",
    p.""{pht}"" AS ""PQM_HEQF_Qual_Type"",
    p.""{pc}""  AS ""PQM_CESM_Code"",
    CASE WHEN p.""{pn}""  IS NOT NULL THEN 'YES' ELSE 'NO' END AS ""Qualification_Name_Match"",
    CASE WHEN p.""{pht}"" IS NOT NULL THEN 'YES' ELSE 'NO' END AS ""HEQF_Type_Match"",
    CASE WHEN p.""{pc}""  IS NOT NULL THEN 'YES' ELSE 'NO' END AS ""CESM_Code_Match"",
    CASE
        WHEN p.""{pn}"" IS NOT NULL AND p.""{pht}"" IS NOT NULL AND p.""{pc}"" IS NOT NULL THEN 'PASS'
        ELSE 'FAIL'
    END AS ""Validation_Result"",
    CASE
        WHEN p.""{pn}"" IS NULL
            THEN 'Qualification name not found in {pt}.'
        WHEN p.""{pht}"" IS NULL
            THEN 'Qualification name found, but HEQF qualification type does not match {pt}.'
        WHEN p.""{pc}"" IS NULL
            THEN 'Qualification name and HEQF type found, but CESM code does not match {pt}.'
        ELSE 'Qualification name, HEQF type and CESM code agree to {pt}.'
    END AS ""Validation_Reason""
FROM r11_extracted_population e
LEFT JOIN ""{{schema}}"".""{pt}"" p
    ON UPPER(TRIM(e.""Qualification_Name_Designator""::text)) = UPPER(TRIM(p.""{pn}""::text))
   AND UPPER(TRIM(e.""Qualification_Type_Descriptor""::text)) = UPPER(TRIM(p.""{pht}""::text))
   AND UPPER(TRIM(e.""CESM_Code""::text)) = UPPER(TRIM(p.""{pc}""::text));

-- ============================================================================
-- STEP 3: FULL EXTRACTED POPULATION
-- ============================================================================

SELECT * FROM r11_extracted_population ORDER BY extract_number;

-- ============================================================================
-- STEP 4: FULL VALIDATION RESULT
-- ============================================================================

SELECT * FROM r11_validation ORDER BY extract_number;

-- ============================================================================
-- STEP 5: EXCEPTIONS ONLY
-- ============================================================================

SELECT * FROM r11_validation WHERE ""Validation_Result"" = 'FAIL' ORDER BY extract_number;

-- ============================================================================
-- STEP 6: SUMMARY
-- ============================================================================

SELECT
    COUNT(*) AS ""Total_Approved_Qualifications"",
    SUM(CASE WHEN ""Validation_Result"" = 'PASS' THEN 1 ELSE 0 END) AS ""PASS_Count"",
    SUM(CASE WHEN ""Validation_Result"" = 'FAIL' THEN 1 ELSE 0 END) AS ""FAIL_Count"",
    ROUND(SUM(CASE WHEN ""Validation_Result"" = 'FAIL' THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0), 2) AS ""Exception_Rate_Pct""
FROM r11_validation;

DROP TABLE r11_extracted_population;
DROP TABLE r11_validation;
-- ============================================================================
-- END RULE 11
-- ============================================================================
";
        }

        // ── Engagement connection / validation helpers ─────────────────────────────────────

        private async Task<(NpgsqlConnection Connection, string Schema)> OpenEngagementConnectionAsync(int clientId)
        {
            var database = await _datasets.GetDatabaseAsync(clientId)
                ?? throw new InvalidOperationException("Create a database for this engagement before running this rule.");

            // Unlimited command timeout: validation queries build temp tables and run multi-way
            // joins over the engagement's full dataset, which can legitimately take longer than
            // the app's normal 60s query timeout on large uploads.
            var connectionString = HemisAudit.Data.PostgresConnectionStringHelper.WithResiliencyDefaults(
                _configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."),
                commandTimeoutSeconds: 0);

            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var setTimeout = connection.CreateCommand())
            {
                // Belt-and-braces: also clear any server/role-level statement_timeout default
                // for this session, in case one is set independently of Npgsql's own timeout.
                setTimeout.CommandText = "SET statement_timeout = 0;";
                await setTimeout.ExecuteNonQueryAsync();
            }
            var schema = string.IsNullOrWhiteSpace(database.SchemaName) ? $"engagement_{clientId}" : database.SchemaName;
            return (connection, schema);
        }

        private async Task ValidateColumnsExistAsync(int clientId, string tableName, params string[] columns)
        {
            var validColumns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
            foreach (var col in columns)
            {
                if (string.IsNullOrWhiteSpace(col))
                    throw new InvalidOperationException("Table or column name is required.");
                if (!validColumns.Contains(col, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Column '{col}' was not found in table '{tableName}'.");
            }
        }

        private static void ApplyBrowserPreview(Rule11ValidationSummary summary)
        {
            summary.ValidationRows = summary.ValidationRows.Take(BrowserPreviewRowLimit).ToList();
            summary.Exceptions     = summary.Exceptions.Take(BrowserPreviewRowLimit).ToList();
        }

        private static void EnsurePopulationTypes(Rule11ValidationSummary summary)
        {
            var postgraduateTypeCodes = new HashSet<string>(
                ParseQualTypeCodes(summary.QualTypeCodesText),
                StringComparer.OrdinalIgnoreCase);

            foreach (var row in summary.ValidationRows)
            {
                if (string.IsNullOrWhiteSpace(row.PopulationType))
                    row.PopulationType = ClassifyPopulationType(row.QualHeqfType, postgraduateTypeCodes);
            }

            foreach (var ex in summary.Exceptions)
            {
                if (string.IsNullOrWhiteSpace(ex.PopulationType))
                    ex.PopulationType = ClassifyPopulationType(ex.QualHeqfType, postgraduateTypeCodes);
            }
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager",     StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director",    StringComparison.OrdinalIgnoreCase);

        private static string NormalizeFilterValue(string? value, string defaultValue) =>
            string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim().ToUpperInvariant();

        private static List<string> ParseQualTypeCodes(string? text)
        {
            var values = Regex.Split(text ?? "", @"[,\r\n;]+")
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return values.Count > 0 ? values : DefaultQualTypeCodes.ToList();
        }

        private static string EscapeSqlString(string value) =>
            (value ?? "").Replace("'", "''");

        private static Rule11ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                if (string.IsNullOrWhiteSpace(decoded)) return null;
                return JsonConvert.DeserializeObject<Rule11ValidationSummary>(decoded);
            }
            catch { return null; }
        }
    }
}
