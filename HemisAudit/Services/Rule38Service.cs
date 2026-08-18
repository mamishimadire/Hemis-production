using System.Globalization;
using System.Text.RegularExpressions;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 38: Enhanced QUAL -> PQM Validation (controls 5.1.2-5.1.6) — validates against the
    // engagement's own uploaded Supabase data instead of a live SQL Server connection. The
    // name+type PQM matching and the six-control comparison logic (FindPqmMatch, ValidateQualification,
    // ClassifyPopulationType) is pure C# with no SQL dependency and is ported unchanged from the
    // original; only the QUAL/PQM row retrieval was translated to Postgres. The original SQL-Server
    // design loaded every approved QUAL row into memory with no cap before applying its own
    // browser-preview trim — RowLimit is introduced here from the start, matching the house style
    // established for every rule this session.
    public class Rule38Service : IRule38Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int RowLimit = 5000;
        private static readonly string[] DefaultPostgraduateTypeCodes = ["07", "27", "28", "49", "72", "73", "08", "30", "50", "74", "75"];

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule38Service(
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

        // ── Normalisation helpers (unchanged from the original) ────────────────────────────

        private static string Norm(string? v) =>
            string.IsNullOrWhiteSpace(v) ? "" : v.Trim().ToUpperInvariant();

        private static string NormName(string? v)
        {
            if (v == null) return "";
            return Regex.Replace(v.Trim().ToUpperInvariant(), @"\s+", " ");
        }

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

        private static HashSet<string> ParsePostgraduateTypeCodes(string? csv)
        {
            var codes = (csv ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Norm)
                .Where(code => code.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            if (codes.Count == 0)
            {
                foreach (var code in DefaultPostgraduateTypeCodes)
                    codes.Add(code);
            }

            return codes;
        }

        private static bool ResolveUseMPrefixPopulationSplit(bool useMPrefixPopulationSplit, bool legacyExcludeMPrefixPattern) =>
            useMPrefixPopulationSplit || legacyExcludeMPrefixPattern;

        private static bool IsMPrefixQualificationCode(string? qualCode) =>
            Regex.IsMatch(Norm(qualCode), @"^M.{5}$", RegexOptions.CultureInvariant);

        private static (string PopulationType, string PopulationClassificationNote) ClassifyPopulationType(
            string qualCode,
            string? qualType,
            ISet<string> postgraduateTypeCodes,
            bool useMPrefixPopulationSplit)
        {
            var notes = new List<string>();
            if (useMPrefixPopulationSplit && IsMPrefixQualificationCode(qualCode))
                notes.Add("qualification code matched M_____");

            if (postgraduateTypeCodes.Contains(Norm(qualType)))
                notes.Add($"QUAL type {qualType} is in the configured postgraduate _005 list");

            if (notes.Count > 0)
                return ("Postgraduate", string.Join("; ", notes));

            return ("Undergraduate", "qualification did not match the configured postgraduate rules");
        }

        private static (string PopulationType, string PopulationClassificationNote) ClassifyPopulationType(
            QualRecord qual,
            ISet<string> postgraduateTypeCodes,
            bool useMPrefixPopulationSplit) =>
            ClassifyPopulationType(qual.QualCode, qual.QualType, postgraduateTypeCodes, useMPrefixPopulationSplit);

        // ── Per-qualification validation (unchanged from the original) ─────────────────────

        private record QualRecord(
            string QualCode,
            string QualName,
            string ApprovalStatus,
            string? QualType,
            string? MinTimeTotal,
            string? MinTimeWIL,
            string? HeqfIndicator,
            string? TotalSubsidy,
            string? CesmCode);

        private record PqmRow(
            string? Name,
            string? QualType,
            string? CesmCode,
            string? CesmCode1,
            string? MinTimeTotal,
            string? WIL,
            string? Accreditation,
            string? TotalSubsidy);

        private record PqmMatchResult(
            bool HasMatch,
            PqmRow? Row,
            bool NeedsReview,
            string MatchNote,
            string FailureLabel);

        private static PqmMatchResult FindPqmMatch(QualRecord qual, IReadOnlyList<PqmRow> pqmRows)
        {
            var nameRows = pqmRows
                .Where(p => string.Equals(NormName(p.Name), NormName(qual.QualName), StringComparison.Ordinal))
                .ToList();

            if (nameRows.Count == 0)
            {
                return new PqmMatchResult(
                    false,
                    null,
                    false,
                    "Qualification name was not found in PQM Authorised Qualification Name.",
                    "No PQM name match");
            }

            var typeRows = nameRows
                .Where(p => string.Equals(Norm(p.QualType), Norm(qual.QualType), StringComparison.Ordinal))
                .ToList();

            if (typeRows.Count == 0)
            {
                return new PqmMatchResult(
                    false,
                    nameRows[0],
                    false,
                    "Qualification name matched in PQM, but the qualification type did not match on the same PQM row.",
                    "No PQM type match");
            }

            return new PqmMatchResult(
                true,
                typeRows[0],
                false,
                "Matched PQM on qualification name and qualification type. Rule 38 no longer depends on PQM CESM columns.",
                "");
        }

        private static Rule38ValidationRow ValidateQualification(
            int rowNo,
            QualRecord qual,
            PqmMatchResult match,
            string populationType,
            string populationClassificationNote,
            IReadOnlyList<string> heqfCodes)
        {
            var failed = new List<string>();

            if (!match.HasMatch || match.Row == null)
            {
                return new Rule38ValidationRow
                {
                    ValidationNumber = rowNo,
                    QualCode = qual.QualCode,
                    QualName = qual.QualName,
                    ApprovalStatus = qual.ApprovalStatus,
                    QualType = qual.QualType,
                    MinTimeTotal = qual.MinTimeTotal,
                    MinTimeWIL = qual.MinTimeWIL,
                    HeqfIndicator = qual.HeqfIndicator,
                    TotalSubsidy = qual.TotalSubsidy,
                    CesmCode = qual.CesmCode,
                    PopulationType = populationType,
                    PopulationClassificationNote = populationClassificationNote,
                    HasPqmMatch = false,
                    PqmName = match.Row?.Name,
                    PqmQualType = match.Row?.QualType,
                    PqmCesmCode = match.Row?.CesmCode,
                    PqmCesmCode1 = match.Row?.CesmCode1,
                    PqmMinTimeTotal = match.Row?.MinTimeTotal,
                    PqmWIL = match.Row?.WIL,
                    PqmAccreditation = match.Row?.Accreditation,
                    PqmTotalSubsidy = match.Row?.TotalSubsidy,
                    MatchNote = match.MatchNote,
                    ValidationResult = "FAIL",
                    FailedControls = new List<string> { match.FailureLabel, "C2", "C3", "C4", "C5", "C6" }
                };
            }

            var pqm = match.Row;
            var c2 = string.Equals(Norm(qual.QualType), Norm(pqm.QualType), StringComparison.OrdinalIgnoreCase);
            if (!c2) failed.Add("C2");

            var c3 = NumericMatch(qual.MinTimeTotal, pqm.MinTimeTotal);
            if (!c3) failed.Add("C3");

            var c4 = NumericMatch(qual.MinTimeWIL, pqm.WIL);
            if (!c4) failed.Add("C4");

            var expectedHeqf = IsHeqfIndicated(pqm.Accreditation, heqfCodes) ? "Y" : "N";
            var c5 = string.Equals(Norm(qual.HeqfIndicator), expectedHeqf, StringComparison.OrdinalIgnoreCase);
            if (!c5) failed.Add("C5");

            var c6 = NumericMatch(qual.TotalSubsidy, pqm.TotalSubsidy);
            if (!c6) failed.Add("C6");

            return new Rule38ValidationRow
            {
                ValidationNumber = rowNo,
                QualCode = qual.QualCode,
                QualName = qual.QualName,
                ApprovalStatus = qual.ApprovalStatus,
                QualType = qual.QualType,
                MinTimeTotal = qual.MinTimeTotal,
                MinTimeWIL = qual.MinTimeWIL,
                HeqfIndicator = qual.HeqfIndicator,
                TotalSubsidy = qual.TotalSubsidy,
                CesmCode = qual.CesmCode,
                PopulationType = populationType,
                PopulationClassificationNote = populationClassificationNote,
                HasPqmMatch = true,
                PqmName = pqm.Name,
                PqmQualType = pqm.QualType,
                PqmCesmCode = pqm.CesmCode,
                PqmCesmCode1 = pqm.CesmCode1,
                PqmMinTimeTotal = pqm.MinTimeTotal,
                PqmWIL = pqm.WIL,
                PqmAccreditation = pqm.Accreditation,
                PqmTotalSubsidy = pqm.TotalSubsidy,
                NeedsReview = match.NeedsReview,
                MatchNote = match.MatchNote,
                C2_TypeMatch = c2,
                C3_MinTimeMatch = c3,
                C4_WILMatch = c4,
                C5_HeqfMatch = c5,
                C5_ExpectedHeqf = expectedHeqf,
                C6_SubsidyMatch = c6,
                ValidationResult = failed.Count == 0 ? "PASS" : "FAIL",
                FailedControls = failed
            };
        }

        private static List<Rule38ControlSummary> BuildControlSummaries(
            List<Rule38ValidationRow> rows,
            string qualTable, string qualApprovalCol, string qualApprovalValue,
            string qualTypeCol, string pqmQualTypeCol,
            string qualMinTimeTotalCol, string pqmMinTimeTotalCol,
            string qualMinTimeWilCol, string pqmWilCol,
            string qualHeqfCol, string pqmAccreditationCol,
            string qualTotalSubsidyCol, string pqmTotalSubsidyCol)
        {
            var matched = rows.Where(r => r.HasPqmMatch).ToList();
            return new List<Rule38ControlSummary>
            {
                new() {
                    ControlId    = "C2",
                    ControlLabel = "Control 2 (5.1.2) — Qualification Type",
                    CriteriaText = $"{qualTable}.{qualTypeCol} = PQM.{pqmQualTypeCol}",
                    PassCount    = matched.Count(r => r.C2_TypeMatch),
                    FailCount    = matched.Count(r => !r.C2_TypeMatch) + rows.Count(r => !r.HasPqmMatch),
                    Status       = matched.All(r => r.C2_TypeMatch) && rows.All(r => r.HasPqmMatch) ? "PASS" : "FAIL"
                },
                new() {
                    ControlId    = "C3",
                    ControlLabel = "Control 3 (5.1.3) — Minimum Time: Total",
                    CriteriaText = $"{qualTable}.{qualMinTimeTotalCol} = PQM.{pqmMinTimeTotalCol}",
                    PassCount    = matched.Count(r => r.C3_MinTimeMatch),
                    FailCount    = matched.Count(r => !r.C3_MinTimeMatch) + rows.Count(r => !r.HasPqmMatch),
                    Status       = matched.All(r => r.C3_MinTimeMatch) && rows.All(r => r.HasPqmMatch) ? "PASS" : "FAIL"
                },
                new() {
                    ControlId    = "C4",
                    ControlLabel = "Control 4 (5.1.4) — Minimum Time: WIL/Experiential",
                    CriteriaText = $"{qualTable}.{qualMinTimeWilCol} = PQM.{pqmWilCol}",
                    PassCount    = matched.Count(r => r.C4_WILMatch),
                    FailCount    = matched.Count(r => !r.C4_WILMatch) + rows.Count(r => !r.HasPqmMatch),
                    Status       = matched.All(r => r.C4_WILMatch) && rows.All(r => r.HasPqmMatch) ? "PASS" : "FAIL"
                },
                new() {
                    ControlId    = "C5",
                    ControlLabel = "Control 5 (5.1.5) — HEQF/HEQSF Indicator",
                    CriteriaText = $"{qualTable}.{qualHeqfCol} (Y/N) agrees with PQM.{pqmAccreditationCol} indicator codes",
                    PassCount    = matched.Count(r => r.C5_HeqfMatch),
                    FailCount    = matched.Count(r => !r.C5_HeqfMatch) + rows.Count(r => !r.HasPqmMatch),
                    Status       = matched.All(r => r.C5_HeqfMatch) && rows.All(r => r.HasPqmMatch) ? "PASS" : "FAIL"
                },
                new() {
                    ControlId    = "C6",
                    ControlLabel = "Control 6 (5.1.6) — Total Subsidy Units",
                    CriteriaText = $"{qualTable}.{qualTotalSubsidyCol} = PQM.{pqmTotalSubsidyCol}",
                    PassCount    = matched.Count(r => r.C6_SubsidyMatch),
                    FailCount    = matched.Count(r => !r.C6_SubsidyMatch) + rows.Count(r => !r.HasPqmMatch),
                    Status       = matched.All(r => r.C6_SubsidyMatch) && rows.All(r => r.HasPqmMatch) ? "PASS" : "FAIL"
                }
            };
        }

        private static void ApplyBrowserPreview(Rule38ValidationSummary summary)
        {
            if (summary.ValidationRows.Count > BrowserPreviewRowLimit)
            {
                var failRows = summary.ValidationRows
                    .Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var passRows = summary.ValidationRows
                    .Where(r => string.Equals(r.ValidationResult, "PASS", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var failTake = Math.Min(failRows.Count, Math.Max(BrowserPreviewRowLimit / 2, 1));
                var passTake = Math.Min(passRows.Count, BrowserPreviewRowLimit - failTake);
                if (failTake == 0) passTake = Math.Min(passRows.Count, BrowserPreviewRowLimit);
                else if (passTake == 0) failTake = Math.Min(failRows.Count, BrowserPreviewRowLimit);

                summary.ValidationRows = failRows.Take(failTake)
                    .Concat(passRows.Take(passTake))
                    .Take(BrowserPreviewRowLimit)
                    .ToList();
                summary.IsPreviewOnly = true;
                summary.PreviewLimit = BrowserPreviewRowLimit;
            }
        }

        private static void NormaliseLoadedSummary(Rule38ValidationSummary? summary)
        {
            if (summary == null)
                return;

            if (!summary.UseMPrefixPopulationSplit && summary.ExcludeMPrefixPattern)
                summary.UseMPrefixPopulationSplit = true;

            if (string.IsNullOrWhiteSpace(summary.PostgraduateTypesCsv))
                summary.PostgraduateTypesCsv = string.Join(",", DefaultPostgraduateTypeCodes);

            var postgraduateTypeCodes = ParsePostgraduateTypeCodes(summary.PostgraduateTypesCsv);
            summary.ValidationRows ??= new List<Rule38ValidationRow>();
            foreach (var row in summary.ValidationRows)
            {
                if (string.IsNullOrWhiteSpace(row.PopulationType))
                {
                    var population = ClassifyPopulationType(
                        row.QualCode,
                        row.QualType,
                        postgraduateTypeCodes,
                        summary.UseMPrefixPopulationSplit);
                    row.PopulationType = population.PopulationType;
                    row.PopulationClassificationNote ??= population.PopulationClassificationNote;
                }
            }

            summary.PostgraduateCount = summary.ValidationRows.Count(r =>
                string.Equals(r.PopulationType, "Postgraduate", StringComparison.OrdinalIgnoreCase));
            summary.UndergraduateCount = summary.ValidationRows.Count - summary.PostgraduateCount;
        }

        // ── Engagement data source (uploaded tables, not a live SQL Server) ────────────────

        public async Task<Rule38TableListResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule38TableListResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule38TableListResult
                {
                    Success = true,
                    Tables = tables,
                    AutoQualTable = tables.FirstOrDefault(t => t.Equals("dbo_QUAL", StringComparison.OrdinalIgnoreCase)),
                    AutoPqmTable = tables.FirstOrDefault(t => t.Equals("PQM", StringComparison.OrdinalIgnoreCase))
                        ?? tables.FirstOrDefault(t => t.Contains("PQM", StringComparison.OrdinalIgnoreCase))
                };
            }
            catch (Exception ex)
            {
                return new Rule38TableListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<ColumnListResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);

                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "qual_id" => columns.FirstOrDefault(c => c.Equals("_001", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_name" => columns.FirstOrDefault(c => c.Equals("_003", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_approval" => columns.FirstOrDefault(c => c.Equals("_004", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_type" => columns.FirstOrDefault(c => c.Equals("_005", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_053" => columns.FirstOrDefault(c => c.Equals("_053", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_054" => columns.FirstOrDefault(c => c.Equals("_054", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_084" => columns.FirstOrDefault(c => c.Equals("_084", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_090" => columns.FirstOrDefault(c => c.Equals("_090", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_cesm" => columns.FirstOrDefault(c => c.Equals("_006", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_name" => columns.FirstOrDefault(c => c.Contains("Authorised", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_type" => columns.FirstOrDefault(c => c.Contains("HEQF_Qual", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_cesm" => columns.FirstOrDefault(c => c.Equals("CESM_CODE", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_cesm1" => columns.FirstOrDefault(c => c.Equals("CESM_CODE1", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_total" => columns.FirstOrDefault(c => c.Equals("Total2", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_wil" => columns.FirstOrDefault(c => c.Equals("WIL_EL2", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_accred" => columns.FirstOrDefault(c => c.Contains("CHE_HEQC", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    _ => columns.FirstOrDefault()
                };

                return new ColumnListResult { Success = true, Columns = columns, AutoSelected = auto };
            }
            catch (Exception ex)
            {
                return new ColumnListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule38VerifyResult> VerifyDataAsync(Rule38VerifyRequest request)
        {
            try
            {
                ValidateNames(request.QualTable, request.PqmTable, request.QualIdCol, request.QualApprovalCol);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var qualTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.QualTable}\";");
                var pqmTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.PqmTable}\";");

                var av = (request.QualApprovalValue ?? "A").Trim().ToUpperInvariant();
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"SELECT COUNT(*) FROM ""{schema}"".""{request.QualTable}"" Q WHERE UPPER(TRIM(CAST(Q.""{request.QualApprovalCol}"" AS text))) = @approvalValue;";
                cmd.Parameters.AddWithValue("approvalValue", av);
                var approvedCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                return new Rule38VerifyResult
                {
                    Success = true,
                    QualTotal = qualTotal,
                    ApprovedCount = approvedCount,
                    PqmTotal = pqmTotal
                };
            }
            catch (Exception ex)
            {
                return new Rule38VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule38ValidationSummary> RunValidationAsync(Rule38ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateNames(
                    request.QualTable, request.PqmTable, request.QualIdCol, request.QualNameCol, request.QualApprovalCol,
                    request.QualTypeCol, request.QualMinTimeTotalCol, request.QualMinTimeWilCol, request.QualHeqfCol,
                    request.QualTotalSubsidyCol, request.PqmNameCol, request.PqmQualTypeCol, request.PqmMinTimeTotalCol,
                    request.PqmWilCol, request.PqmAccreditationCol, request.PqmTotalSubsidyCol);

                var summary = await AnalyseAsync(request);
                if (summary.Success && request.ClientId > 0)
                {
                    try
                    {
                        summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName);
                    }
                    catch (Exception ex)
                    {
                        summary.Warning = $"Analysis completed, but the workspace could not be saved automatically: {ex.Message}";
                    }
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule38ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        // ── Analysis ─────────────────────────────────────────────────────────────────────

        private async Task<Rule38ValidationSummary> AnalyseAsync(Rule38ValidationRequest request)
        {
            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var av = (request.QualApprovalValue ?? "A").Trim().ToUpperInvariant();

            var qualTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.QualTable}\";");

            int approvedCount;
            await using (var approvedCmd = connection.CreateCommand())
            {
                approvedCmd.CommandText = $@"SELECT COUNT(*) FROM ""{schema}"".""{request.QualTable}"" WHERE UPPER(TRIM(CAST(""{request.QualApprovalCol}"" AS text))) = @approvalValue;";
                approvedCmd.Parameters.AddWithValue("approvalValue", av);
                approvedCount = Convert.ToInt32(await approvedCmd.ExecuteScalarAsync());
            }

            var useMPrefixPopulationSplit = ResolveUseMPrefixPopulationSplit(request.UseMPrefixPopulationSplit, request.ExcludeMPrefixPattern);
            var postgraduateTypeCodes = ParsePostgraduateTypeCodes(request.PostgraduateTypesCsv);
            var heqfCodes = ParseHeqfCodes(request.HeqfIndicatorCodesCsv);

            var qualRows = new List<QualRecord>();
            var rowsTruncated = false;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT
    UPPER(TRIM(CAST(Q.""{request.QualIdCol}"" AS text))) AS qual_code,
    TRIM(CAST(Q.""{request.QualNameCol}"" AS text)) AS qual_name,
    UPPER(TRIM(CAST(Q.""{request.QualApprovalCol}"" AS text))) AS approval_status,
    TRIM(CAST(Q.""{request.QualTypeCol}"" AS text)) AS qual_type,
    TRIM(CAST(Q.""{request.QualMinTimeTotalCol}"" AS text)) AS min_time_total,
    TRIM(CAST(Q.""{request.QualMinTimeWilCol}"" AS text)) AS min_time_wil,
    UPPER(TRIM(CAST(Q.""{request.QualHeqfCol}"" AS text))) AS heqf_indicator,
    TRIM(CAST(Q.""{request.QualTotalSubsidyCol}"" AS text)) AS total_subsidy,
    '' AS cesm_code
FROM ""{schema}"".""{request.QualTable}"" Q
WHERE UPPER(TRIM(CAST(Q.""{request.QualApprovalCol}"" AS text))) = @approvalValue
ORDER BY Q.""{request.QualIdCol}""
LIMIT @limit;";
                cmd.Parameters.AddWithValue("approvalValue", av);
                cmd.Parameters.AddWithValue("limit", RowLimit + 1);

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (qualRows.Count >= RowLimit)
                    {
                        rowsTruncated = true;
                        break;
                    }

                    string Read(int i) => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                    string? ReadNullable(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);

                    qualRows.Add(new QualRecord(
                        Read(0), Read(1), Read(2), ReadNullable(3), ReadNullable(4),
                        ReadNullable(5), ReadNullable(6), ReadNullable(7), ReadNullable(8)));
                }
            }

            var pqmRows = new List<PqmRow>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT
    TRIM(CAST(P.""{request.PqmNameCol}"" AS text)) AS pqm_name,
    TRIM(CAST(P.""{request.PqmQualTypeCol}"" AS text)) AS pqm_qual_type,
    TRIM(CAST(P.""{request.PqmMinTimeTotalCol}"" AS text)) AS pqm_min_time_total,
    TRIM(CAST(P.""{request.PqmWilCol}"" AS text)) AS pqm_wil,
    TRIM(CAST(P.""{request.PqmAccreditationCol}"" AS text)) AS pqm_accreditation,
    TRIM(CAST(P.""{request.PqmTotalSubsidyCol}"" AS text)) AS pqm_total_subsidy
FROM ""{schema}"".""{request.PqmTable}"" P;";

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string? ReadNullable(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);

                    pqmRows.Add(new PqmRow(
                        ReadNullable(0), ReadNullable(1), null, null,
                        ReadNullable(2), ReadNullable(3), ReadNullable(4), ReadNullable(5)));
                }
            }

            var rows = new List<Rule38ValidationRow>();
            var rowNo = 1;
            foreach (var qual in qualRows)
            {
                var population = ClassifyPopulationType(qual, postgraduateTypeCodes, useMPrefixPopulationSplit);
                rows.Add(ValidateQualification(
                    rowNo++, qual, FindPqmMatch(qual, pqmRows),
                    population.PopulationType, population.PopulationClassificationNote, heqfCodes));
            }

            var pqmMatchCount = rows.Count(r => r.HasPqmMatch);
            var pqmNoMatchCount = rows.Count(r => !r.HasPqmMatch);
            var reviewRequiredCount = rows.Count(r => r.NeedsReview);
            var postgraduateCount = rows.Count(r => string.Equals(r.PopulationType, "Postgraduate", StringComparison.OrdinalIgnoreCase));
            var undergraduateCount = rows.Count - postgraduateCount;
            var overallPass = rows.Count(r => r.ValidationResult == "PASS");
            var overallFail = rows.Count(r => r.ValidationResult == "FAIL");
            var total = rows.Count;
            var rate = total > 0 ? Math.Round((decimal)overallFail / total * 100, 2) : 0m;

            var controlSummaries = BuildControlSummaries(rows,
                request.QualTable, request.QualApprovalCol, request.QualApprovalValue ?? "A",
                request.QualTypeCol, request.PqmQualTypeCol,
                request.QualMinTimeTotalCol, request.PqmMinTimeTotalCol,
                request.QualMinTimeWilCol, request.PqmWilCol,
                request.QualHeqfCol, request.PqmAccreditationCol,
                request.QualTotalSubsidyCol, request.PqmTotalSubsidyCol);

            return new Rule38ValidationSummary
            {
                Success = true,
                TotalQualRecords = qualTotal,
                ApprovedCount = approvedCount,
                PqmMatchCount = pqmMatchCount,
                PqmNoMatchCount = pqmNoMatchCount,
                UndergraduateCount = undergraduateCount,
                PostgraduateCount = postgraduateCount,
                OverallPassCount = overallPass,
                OverallFailCount = overallFail,
                ExceptionRate = rate,
                Status = overallFail == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                QualTable = request.QualTable,
                QualIdCol = request.QualIdCol,
                QualNameCol = request.QualNameCol,
                QualApprovalCol = request.QualApprovalCol,
                QualApprovalValue = request.QualApprovalValue ?? "A",
                QualTypeCol = request.QualTypeCol,
                QualMinTimeTotalCol = request.QualMinTimeTotalCol,
                QualMinTimeWilCol = request.QualMinTimeWilCol,
                QualHeqfCol = request.QualHeqfCol,
                QualTotalSubsidyCol = request.QualTotalSubsidyCol,
                QualCesmCodeCol = "",
                PqmTable = request.PqmTable,
                PqmNameCol = request.PqmNameCol,
                PqmQualTypeCol = request.PqmQualTypeCol,
                PqmCesmCodeCol = request.PqmCesmCodeCol,
                PqmCesmCode1Col = request.PqmCesmCode1Col,
                PqmMinTimeTotalCol = request.PqmMinTimeTotalCol,
                PqmWilCol = request.PqmWilCol,
                PqmAccreditationCol = request.PqmAccreditationCol,
                PqmTotalSubsidyCol = request.PqmTotalSubsidyCol,
                ReviewRequiredCount = reviewRequiredCount,
                HeqfIndicatorCodesCsv = request.HeqfIndicatorCodesCsv,
                UseMPrefixPopulationSplit = useMPrefixPopulationSplit,
                ExcludeMPrefixPattern = useMPrefixPopulationSplit,
                PostgraduateTypesCsv = string.Join(",", postgraduateTypeCodes),
                ClientId = request.ClientId,
                RowsTruncated = rowsTruncated,
                ControlSummaries = controlSummaries,
                ValidationRows = rows,
                Warning = rowsTruncated
                    ? $"Only the first {RowLimit:N0} approved QUAL rows were analysed for browser review and export performance."
                    : null
            };
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule38WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 38);
            if (row == null) return null;

            Rule38ValidationSummary? summary = null;
            if (!string.IsNullOrWhiteSpace(row.ResultsJSON))
            {
                try
                {
                    var decoded = ValidationPayloadCodec.Decode(row.ResultsJSON);
                    summary = JsonConvert.DeserializeObject<Rule38ValidationSummary>(decoded);
                    NormaliseLoadedSummary(summary);
                }
                catch { }
            }
            if (summary != null)
                ApplyBrowserPreview(summary);

            var workspace = new Rule38WorkspaceStateViewModel
            {
                ClientId = clientId,
                RunId = row.RunId,
                QualTable = summary?.QualTable ?? "dbo_QUAL",
                QualIdCol = summary?.QualIdCol ?? "_001",
                QualNameCol = summary?.QualNameCol ?? "_003",
                QualApprovalCol = summary?.QualApprovalCol ?? "_004",
                QualApprovalValue = summary?.QualApprovalValue ?? "A",
                QualTypeCol = summary?.QualTypeCol ?? "_005",
                QualMinTimeTotalCol = summary?.QualMinTimeTotalCol ?? "_053",
                QualMinTimeWilCol = summary?.QualMinTimeWilCol ?? "_054",
                QualHeqfCol = summary?.QualHeqfCol ?? "_084",
                QualTotalSubsidyCol = summary?.QualTotalSubsidyCol ?? "_090",
                QualCesmCodeCol = summary?.QualCesmCodeCol ?? "_006",
                PqmTable = summary?.PqmTable ?? "PQM",
                PqmNameCol = summary?.PqmNameCol ?? "Authorised_Qualification_Name",
                PqmQualTypeCol = summary?.PqmQualTypeCol ?? "HEQF_Qual_Type",
                PqmCesmCodeCol = summary?.PqmCesmCodeCol ?? "CESM_CODE",
                PqmCesmCode1Col = summary?.PqmCesmCode1Col ?? "CESM_CODE1",
                PqmMinTimeTotalCol = summary?.PqmMinTimeTotalCol ?? "Total2",
                PqmWilCol = summary?.PqmWilCol ?? "WIL_EL2",
                PqmAccreditationCol = summary?.PqmAccreditationCol ?? "CHE_HEQC_Accreditation_Approval_Ref_Nr",
                PqmTotalSubsidyCol = summary?.PqmTotalSubsidyCol ?? "Total2",
                HeqfIndicatorCodesCsv = summary?.HeqfIndicatorCodesCsv ?? "H/,HEQF,HEQSF",
                UseMPrefixPopulationSplit = summary?.UseMPrefixPopulationSplit ?? summary?.ExcludeMPrefixPattern ?? false,
                ExcludeMPrefixPattern = summary?.ExcludeMPrefixPattern ?? false,
                PostgraduateTypesCsv = summary?.PostgraduateTypesCsv ?? "07,27,28,49,72,73,08,30,50,74,75",
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                IsWorkspaceSaved = await _systemDb.IsWorkspaceSavedAsync(row.RunId),
                Summary = summary
            };

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            workspace.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(clientId, currentUser.Id) ?? ""
                : "";

            var signoffs = await _systemDb.GetRuleRunSignoffsAsync(row.RunId, currentUser?.Id);
            workspace.HasDataAnalystSignoff = signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            var currentRoleSignoff = signoffs.FirstOrDefault(s =>
                ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, workspace.CurrentUserEngagementRole));
            workspace.CurrentUserHasSignedOff = currentRoleSignoff != null;
            workspace.CurrentUserSignoffComment = currentRoleSignoff?.Comment ?? "";

            return workspace;
        }

        public async Task<Rule38RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 38);
            if (row == null) return null;

            var summary = new Rule38ValidationSummary();
            if (!string.IsNullOrWhiteSpace(row.ResultsJSON))
            {
                try
                {
                    var decoded = ValidationPayloadCodec.Decode(row.ResultsJSON);
                    summary = JsonConvert.DeserializeObject<Rule38ValidationSummary>(decoded) ?? summary;
                    NormaliseLoadedSummary(summary);
                }
                catch { }
            }

            var review = new Rule38RunReviewViewModel
            {
                RunId = row.RunId,
                ClientId = row.ClientId,
                IsCurrentRun = row.IsCurrent,
                EngagementName = row.EngagementName,
                MaconomyNumber = row.MaconomyNumber,
                GeneratedSql = GenerateSql(new Rule38ValidationRequest
                {
                    ClientId = row.ClientId,
                    QualTable = summary.QualTable,
                    QualIdCol = summary.QualIdCol,
                    QualNameCol = summary.QualNameCol,
                    QualApprovalCol = summary.QualApprovalCol,
                    QualApprovalValue = summary.QualApprovalValue,
                    QualTypeCol = summary.QualTypeCol,
                    QualMinTimeTotalCol = summary.QualMinTimeTotalCol,
                    QualMinTimeWilCol = summary.QualMinTimeWilCol,
                    QualHeqfCol = summary.QualHeqfCol,
                    QualTotalSubsidyCol = summary.QualTotalSubsidyCol,
                    QualCesmCodeCol = summary.QualCesmCodeCol,
                    PqmTable = summary.PqmTable,
                    PqmNameCol = summary.PqmNameCol,
                    PqmQualTypeCol = summary.PqmQualTypeCol,
                    PqmCesmCodeCol = summary.PqmCesmCodeCol,
                    PqmCesmCode1Col = summary.PqmCesmCode1Col,
                    PqmMinTimeTotalCol = summary.PqmMinTimeTotalCol,
                    PqmWilCol = summary.PqmWilCol,
                    PqmAccreditationCol = summary.PqmAccreditationCol,
                    PqmTotalSubsidyCol = summary.PqmTotalSubsidyCol,
                    HeqfIndicatorCodesCsv = summary.HeqfIndicatorCodesCsv,
                    UseMPrefixPopulationSplit = summary.UseMPrefixPopulationSplit || summary.ExcludeMPrefixPattern,
                    ExcludeMPrefixPattern = summary.UseMPrefixPopulationSplit || summary.ExcludeMPrefixPattern,
                    PostgraduateTypesCsv = summary.PostgraduateTypesCsv
                }),
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

        public async Task<Rule38WorkspaceSaveResult> SaveWorkspaceAsync(Rule38ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule38WorkspaceSaveResult { Success = false, Error = "Run validation before saving the workspace." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule38WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.QualTable,
                    DeceasedTable = request.PqmTable,
                    StudColumn = request.HeqfIndicatorCodesCsv,
                    DeceasedColumn = ""
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule38WorkspaceSaveResult
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
                return new Rule38WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule38WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule38WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule38WorkspaceSaveResult
                {
                    Success = true,
                    Message = clearedSignoffs > 0
                        ? "Editing has begun. Existing signoffs were removed so the workspace must be reviewed again."
                        : "Editing has begun.",
                    SignoffsCleared = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace = workspace
                };
            }
            catch (Exception ex)
            {
                return new Rule38WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 38 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 38 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 38 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public string GenerateSql(Rule38ValidationRequest request)
        {
            var schema = $"engagement_{request.ClientId}";
            var qt = request.QualTable;
            var pt = request.PqmTable;
            var qi = request.QualIdCol;
            var qn = request.QualNameCol;
            var qa = request.QualApprovalCol;
            var qt5 = request.QualTypeCol;
            var q53 = request.QualMinTimeTotalCol;
            var q54 = request.QualMinTimeWilCol;
            var q84 = request.QualHeqfCol;
            var q90 = request.QualTotalSubsidyCol;
            var pn = request.PqmNameCol;
            var pt5 = request.PqmQualTypeCol;
            var p53 = request.PqmMinTimeTotalCol;
            var p54 = request.PqmWilCol;
            var p84 = request.PqmAccreditationCol;
            var p90 = request.PqmTotalSubsidyCol;
            var av = (request.QualApprovalValue ?? "A").Replace("'", "''").Trim().ToUpperInvariant();
            var useMPrefixPopulationSplit = ResolveUseMPrefixPopulationSplit(request.UseMPrefixPopulationSplit, request.ExcludeMPrefixPattern);
            var postgraduateTypeCodes = ParsePostgraduateTypeCodes(request.PostgraduateTypesCsv);
            var postgraduateTypeCodeSql = string.Join(", ", postgraduateTypeCodes.Select(code => $"'{code.Replace("'", "''")}'"));
            if (string.IsNullOrWhiteSpace(postgraduateTypeCodeSql))
                postgraduateTypeCodeSql = "'__NO_POSTGRADUATE_CODES__'";

            var populationTypeSql = useMPrefixPopulationSplit
                ? $"CASE WHEN UPPER(TRIM(CAST(Q.\"{qi}\" AS text))) LIKE 'M_____' OR UPPER(TRIM(CAST(Q.\"{qt5}\" AS text))) IN ({postgraduateTypeCodeSql}) THEN 'Postgraduate' ELSE 'Undergraduate' END"
                : $"CASE WHEN UPPER(TRIM(CAST(Q.\"{qt5}\" AS text))) IN ({postgraduateTypeCodeSql}) THEN 'Postgraduate' ELSE 'Undergraduate' END";

            return $@"-- HEMIS Rule 38: QUAL -> PQM Validation
-- Source: this engagement's own uploaded tables (schema ""{schema}""), not a live SQL Server.
-- Rule 38 matches approved QUAL rows to PQM on qualification name and qualification type.
-- Population split: approved QUAL rows remain in scope. Rows are tagged Postgraduate when QUAL.""{qt5}"" is in the configured postgraduate list{(useMPrefixPopulationSplit ? " or QUAL code matches M_____" : "")}; all other approved rows are tagged Undergraduate.
-- 5.1.2  Qualification type: ""{qt5}"" vs PQM.""{pt5}""
-- 5.1.3  Minimum Time Total: ""{q53}"" vs PQM.""{p53}""
-- 5.1.4  Minimum Time WIL: ""{q54}"" vs PQM.""{p54}""
-- 5.1.5  HEQF/HEQSF Indicator: ""{q84}"" (Y/N) vs PQM.""{p84}"" using codes: {request.HeqfIndicatorCodesCsv}
-- 5.1.6  Total Subsidy Units: ""{q90}"" vs PQM.""{p90}""

WITH approved_qual AS (
    SELECT
        UPPER(TRIM(CAST(Q.""{qi}"" AS text))) AS qual_code,
        TRIM(CAST(Q.""{qn}"" AS text)) AS qual_name,
        UPPER(TRIM(CAST(Q.""{qa}"" AS text))) AS approval_status,
        TRIM(CAST(Q.""{qt5}"" AS text)) AS qual_type,
        {populationTypeSql} AS population_type,
        TRIM(CAST(Q.""{q53}"" AS text)) AS min_time_total,
        TRIM(CAST(Q.""{q54}"" AS text)) AS min_time_wil,
        UPPER(TRIM(CAST(Q.""{q84}"" AS text))) AS heqf_indicator,
        TRIM(CAST(Q.""{q90}"" AS text)) AS total_subsidy
    FROM ""{schema}"".""{qt}"" Q
    WHERE UPPER(TRIM(CAST(Q.""{qa}"" AS text))) = '{av}'
)
SELECT
    AQ.qual_code,
    AQ.qual_name,
    AQ.approval_status,
    AQ.qual_type,
    AQ.population_type,
    PQM.""{pt5}"" AS pqm_qual_type,
    PQM.""{p53}"" AS pqm_min_time_total,
    PQM.""{p54}"" AS pqm_wil,
    PQM.""{p84}"" AS pqm_accreditation,
    PQM.""{p90}"" AS pqm_total_subsidy,
    CASE WHEN PQM.""{pn}"" IS NULL THEN 'FAIL' ELSE 'PASS' END AS pqm_match_status,
    CASE WHEN UPPER(TRIM(CAST(AQ.qual_type AS text))) = UPPER(TRIM(CAST(PQM.""{pt5}"" AS text))) THEN 'PASS' ELSE 'FAIL' END AS c2_type_match,
    CASE WHEN NULLIF(AQ.min_time_total, '')::numeric = NULLIF(TRIM(CAST(PQM.""{p53}"" AS text)), '')::numeric THEN 'PASS' ELSE 'FAIL' END AS c3_min_time_match,
    CASE WHEN NULLIF(AQ.min_time_wil, '')::numeric = NULLIF(TRIM(CAST(PQM.""{p54}"" AS text)), '')::numeric THEN 'PASS' ELSE 'FAIL' END AS c4_wil_match,
    AQ.heqf_indicator,
    AQ.total_subsidy
FROM approved_qual AQ
LEFT JOIN LATERAL (
    SELECT P.*
    FROM ""{schema}"".""{pt}"" P
    WHERE UPPER(TRIM(CAST(P.""{pn}"" AS text))) = UPPER(TRIM(CAST(AQ.qual_name AS text)))
      AND UPPER(TRIM(CAST(P.""{pt5}"" AS text))) = UPPER(TRIM(CAST(AQ.qual_type AS text)))
    ORDER BY P.""{pn}""
    LIMIT 1
) PQM ON true
ORDER BY AQ.qual_code;";
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule38ValidationRequest request, Rule38ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 38);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 38,
                RuleName = "Enhanced QUAL -> PQM Validation",
                Status = summary.Status,
                TotalRecords = summary.ApprovedCount,
                PassCount = summary.OverallPassCount,
                FailCount = summary.OverallFailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.QualTable,
                DeceasedTable = request.PqmTable,
                StudColumn = request.HeqfIndicatorCodesCsv,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.ValidationRows.Where(r => r.ValidationResult == "FAIL"))),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Validation / helpers ─────────────────────────────────────────────────────────

        private static void ValidateNames(params string[] values)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException("Table and column selections are required.");

                foreach (var bad in new[] { ";", "'", "\"", "--", "/*", "*/" })
                {
                    if (value.Contains(bad, StringComparison.Ordinal))
                        throw new InvalidOperationException("Unsafe table or column name was provided.");
                }
            }
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase);

        private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

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
