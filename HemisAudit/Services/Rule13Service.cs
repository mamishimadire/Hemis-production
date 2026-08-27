using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 13: CESM qualification population validation — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. Ported from the Rule12/14
    // pattern. 3 required tables (CESM, QUAL, STUD) plus an optional 4th (PQM) that drives
    // in-memory fuzzy code/name matching (pure C#, unchanged from the original — only the SQL
    // data-loading around it moved to Postgres).
    public class Rule13Service : IRule13Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const string ScopeCode = "ALL";
        private const string ScopeTitle = "Full Population";
        private const string ScopeDescription = "100% of qualifying CESM qualifications where _006 <> 'ZZZZZZ'.";
        private static readonly string[] PartOrder = [ScopeCode];
        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule13Service(
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

        public async Task<Rule13TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule13TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule13TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_CESM", "dbo_cesm", "CESM", "cesm"], ["cesm"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "dbo_qual", "QUAL", "qual"], ["qual"]),
                    AutoCregTable = FindFirst(tables, ["dbo_STUD", "dbo_stud", "STUD", "stud"], ["stud"]),
                    AutoCrseTable = FindFirst(tables, ["dbo_STUD", "dbo_stud", "STUD", "stud"], ["stud"]),
                    AutoPqmTable = tables.FirstOrDefault(t => t.Contains("PQM", StringComparison.OrdinalIgnoreCase))
                };
            }
            catch (Exception ex)
            {
                return new Rule13TableDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<ColumnListResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                return new ColumnListResult { Success = true, Columns = columns, AutoSelected = columns.FirstOrDefault() };
            }
            catch (Exception ex)
            {
                return new ColumnListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule13VerifyResult> VerifyTablesAsync(Rule13VerifyRequest request)
        {
            try
            {
                ValidateRequest(request.StudTable, request.QualTable, request.CregTable);

                var summary = await AnalyseAsync(new Rule13ValidationRequest
                {
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    QualTable = request.QualTable,
                    CregTable = request.CregTable,
                    CrseTable = request.CrseTable,
                    PgTypesText = request.PgTypesText,
                    PqmTable = request.PqmTable,
                    CesmIdCol = request.CesmIdCol,
                    CesmCodeCol = request.CesmCodeCol,
                    QualIdCol = request.QualIdCol,
                    QualNameCol = request.QualNameCol,
                    StudIdCol = request.StudIdCol,
                    PqmNameCol = request.PqmNameCol,
                    PqmCode1Col = request.PqmCode1Col,
                    PqmCode2Col = request.PqmCode2Col
                }, includeAllReviewRows: false);

                return new Rule13VerifyResult
                {
                    Success = true,
                    FoundationStudentCount = summary.FoundationStudentCount,
                    ValidatedRowCount = summary.TotalValidated
                };
            }
            catch (Exception ex)
            {
                return new Rule13VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule13ValidationSummary> RunValidationAsync(Rule13ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.QualTable, request.CregTable);

                var summary = await AnalyseAsync(request, includeAllReviewRows: true);
                if (summary.Success && request.ClientId > 0)
                {
                    try
                    {
                        summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName);
                    }
                    catch (Exception ex)
                    {
                        summary.Success = false;
                        summary.Error = $"Validation completed, but the saved run could not be written to the system database: {ex.Message}";
                        return summary;
                    }
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule13ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule13ValidationSummary> GetExportSummaryAsync(Rule13ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.QualTable, request.CregTable);
            return await AnalyseAsync(request, includeAllReviewRows: true);
        }

        public async Task<int> GetPopulationCountAsync(Rule13ValidationRequest request)
        {
            var summary = await GetExportSummaryAsync(request);
            return summary.TotalValidated;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule13WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 13);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (deserializedSummary != null && includeSummary)
            {
                deserializedSummary = await ExpandAndPersistSavedSummaryIfNeededAsync(row.RunId, deserializedSummary, clientId);
                ApplyBrowserPreview(deserializedSummary);
            }
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule13WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = NormalizeObjectName(row.StudTable, "dbo_CESM"),
                QualTable = deserializedSummary?.QualTable ?? NormalizeObjectName(row.DeceasedTable, "dbo_QUAL"),
                CregTable = deserializedSummary?.CregTable ?? NormalizeObjectName(row.StudColumn, "dbo_STUD"),
                CrseTable = deserializedSummary?.CregTable ?? NormalizeObjectName(row.StudColumn, "dbo_STUD"),
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (summary != null)
                workspace.CurrentStatus = summary.Status;

            if (deserializedSummary != null)
            {
                workspace.PgTypesText = deserializedSummary.PgTypesText ?? "";
                workspace.GoverningPartCodes = NormalizeGoverningPartCodes(deserializedSummary.GoverningPartCodes);
                workspace.PqmTable = deserializedSummary.PqmTable ?? "";
                workspace.CesmIdCol = deserializedSummary.CesmIdCol ?? "_001";
                workspace.CesmCodeCol = deserializedSummary.CesmCodeCol ?? "_006";
                workspace.QualIdCol = deserializedSummary.QualIdCol ?? "_001";
                workspace.QualNameCol = deserializedSummary.QualNameCol ?? "_003";
                workspace.StudIdCol = deserializedSummary.StudIdCol ?? "_001";
                workspace.PqmNameCol = deserializedSummary.PqmNameCol ?? "Authorised_Qualification_Name";
                workspace.PqmCode1Col = deserializedSummary.PqmCode1Col ?? "CESM_Code";
                workspace.PqmCode2Col = deserializedSummary.PqmCode2Col ?? "CESM_Code2";
            }

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

            if (workspace.Summary != null)
                workspace.Summary.SavedRunId = workspace.RunId;

            return workspace;
        }

        public async Task<Rule13RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 13);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            summary.ClientId = row.ClientId;
            if (summary.SavedRunId.GetValueOrDefault() <= 0)
                summary.SavedRunId = runId;

            summary = await ExpandAndPersistSavedSummaryIfNeededAsync(runId, summary, row.ClientId);

            if (includeFullResults)
            {
                summary.DisplayedCount = summary.ReviewRows.Count;
                summary.IsPreviewOnly = false;
                summary.PreviewLimit = 0;
            }
            else
            {
                ApplyBrowserPreview(summary);
            }

            var review = new Rule13RunReviewViewModel
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

        public async Task<Rule13WorkspaceSaveResult> SaveWorkspaceAsync(Rule13ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.QualTable, request.CregTable);

                if (request.RunId is null || request.RunId <= 0)
                    return new Rule13WorkspaceSaveResult { Success = false, Error = "Run the validation first so the workspace can be saved." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule13WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.QualTable,
                    StudColumn = request.CregTable,
                    DeceasedColumn = ""
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule13WorkspaceSaveResult
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
                return new Rule13WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule13WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule13WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule13WorkspaceSaveResult
                {
                    Success = true,
                    Message = clearedSignoffs > 0
                        ? "Editing has begun. Existing signoffs were removed."
                        : "Editing has begun. Save the workspace when you are ready.",
                    SignoffsCleared = clearedSignoffs > 0,
                    ClearedSignoffCount = clearedSignoffs,
                    Workspace = workspace
                };
            }
            catch (Exception ex)
            {
                return new Rule13WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 13 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 13 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 13 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule13ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.QualTable, request.CregTable);

            var cesmIdCol = string.IsNullOrWhiteSpace(request.CesmIdCol) ? "_001" : request.CesmIdCol;
            var cesmCodeCol = string.IsNullOrWhiteSpace(request.CesmCodeCol) ? "_006" : request.CesmCodeCol;
            var qualIdCol = string.IsNullOrWhiteSpace(request.QualIdCol) ? "_001" : request.QualIdCol;
            var qualNameCol = string.IsNullOrWhiteSpace(request.QualNameCol) ? "_003" : request.QualNameCol;
            var studIdCol = string.IsNullOrWhiteSpace(request.StudIdCol) ? "_001" : request.StudIdCol;

            var sql = $@"-- HEMIS RULE 13: CESM QUALIFICATION POPULATION VALIDATION
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Rule:
--   Extract 100% of {request.StudTable} where {cesmCodeCol} NOT IN ('', 'ZZZZZZ')
--   Match {request.StudTable}.{cesmIdCol} to {request.QualTable}.{qualIdCol}
--   Match qualification code to {request.CregTable}.{studIdCol}; PASS if >= 1 linked student
--   Optional: PQM table drives an additional in-memory code/name match (see application code)

{BuildRule13PrepSql("{schema}", request.StudTable, request.QualTable, request.CregTable, cesmIdCol, cesmCodeCol, qualIdCol, qualNameCol, studIdCol, true, true, true, true, true)}

-- Full extracted population + validation result
SELECT * FROM rule13_validation ORDER BY ""Extract_Number"";

-- Summary
SELECT
    COUNT(*) AS total_cesm_qualifications,
    SUM(CASE WHEN ""Validation_Result"" = 'PASS' THEN 1 ELSE 0 END) AS pass_count,
    SUM(CASE WHEN ""Validation_Result"" = 'FAIL' THEN 1 ELSE 0 END) AS fail_count,
    ROUND(SUM(CASE WHEN ""Validation_Result"" = 'FAIL' THEN 1 ELSE 0 END) * 100.0
         / NULLIF(COUNT(*), 0), 2) AS exception_rate_pct
FROM rule13_validation;";

            return sql.Trim();
        }

        // ── PQM helper record and matching methods (unchanged pure-C# logic) ───────────────

        private record PqmRow(string? Code1, string? Code2, string? Name);

        private static string Digits(string? v) => v == null ? "" : string.Concat(v.Where(char.IsDigit));

        private static string TrimLeadingZeros(string s) => s.TrimStart('0') is { Length: > 0 } t ? t : s;

        private static string NormName(string? v)
        {
            if (v == null) return "";
            return System.Text.RegularExpressions.Regex.Replace(v.Trim().ToUpperInvariant(), @"\s+", " ");
        }

        private static bool HasSameLeadingDigits(string left, string right, int digits) =>
            left.Length >= digits && right.Length >= digits &&
            string.Equals(left[..digits], right[..digits], StringComparison.Ordinal);

        private static int CesmReviewPriority(string reason) => reason switch
        {
            "first 4 digits matched" => 0,
            "first 4 digits matched after removing leading zeros" => 1,
            "first 3 digits matched" => 2,
            "first 3 digits matched after removing leading zeros" => 3,
            _ => 99
        };

        private record CesmReviewMatch(string Reason, string? PqmCode, string? PqmName);

        private static CesmReviewMatch? GetCesmReviewMatch(string? cesmCode, List<PqmRow> pqm)
        {
            if (string.IsNullOrWhiteSpace(cesmCode)) return null;
            var rawCode = Digits(cesmCode);
            if (rawCode.Length < 3) return null;
            var trimmedCode = TrimLeadingZeros(rawCode);
            CesmReviewMatch? best = null;
            foreach (var p in pqm)
            {
                foreach (var pqmCode in new[] { p.Code1, p.Code2 }.Where(c => !string.IsNullOrWhiteSpace(c)))
                {
                    var pqmRaw = Digits(pqmCode);
                    if (string.IsNullOrEmpty(pqmRaw)) continue;
                    var pqmTrimmed = TrimLeadingZeros(pqmRaw);
                    string? reason = null;
                    if (HasSameLeadingDigits(rawCode, pqmRaw, 4)) reason = "first 4 digits matched";
                    else if (HasSameLeadingDigits(trimmedCode, pqmTrimmed, 4)) reason = "first 4 digits matched after removing leading zeros";
                    else if (HasSameLeadingDigits(rawCode, pqmRaw, 3)) reason = "first 3 digits matched";
                    else if (HasSameLeadingDigits(trimmedCode, pqmTrimmed, 3)) reason = "first 3 digits matched after removing leading zeros";
                    if (reason != null)
                    {
                        if (best == null || CesmReviewPriority(reason) < CesmReviewPriority(best.Reason))
                            best = new CesmReviewMatch(reason, pqmCode?.Trim(), p.Name);
                        if (best.Reason == "first 4 digits matched") return best;
                    }
                }
            }
            return best;
        }

        private static bool ExactCodeMatch(string hDigits, string? pqmCode)
        {
            var p = Digits(pqmCode);
            return !string.IsNullOrEmpty(hDigits) && !string.IsNullOrEmpty(p) &&
                   string.Equals(hDigits, p, StringComparison.Ordinal);
        }

        private static bool CodeMatches(string hDigits, string? pqmCode, int n = 4)
        {
            var p = Digits(pqmCode);
            if (string.IsNullOrEmpty(hDigits) || string.IsNullOrEmpty(p)) return false;
            var use = Math.Min(n, Math.Min(hDigits.Length, p.Length));
            return use >= 2 && string.Equals(hDigits[..use], p[..use], StringComparison.Ordinal);
        }

        private static string? ResolveMatchedPqmCode(string hemisDigits, PqmRow pqm)
        {
            if (CodeMatches(hemisDigits, pqm.Code1)) return pqm.Code1?.Trim();
            if (CodeMatches(hemisDigits, pqm.Code2)) return pqm.Code2?.Trim();
            return pqm.Code1?.Trim() ?? pqm.Code2?.Trim();
        }

        private static void ApplyPqmMatch(Rule13ReviewRowViewModel row, string? cesmCode, string? qualName, List<PqmRow> pqm)
        {
            var hDigits = Digits(cesmCode);
            var hNorm = NormName(qualName);

            var codeRows = pqm.Where(p => CodeMatches(hDigits, p.Code1) || CodeMatches(hDigits, p.Code2)).ToList();
            var combined = codeRows.Where(p => string.Equals(NormName(p.Name), hNorm, StringComparison.Ordinal)).ToList();

            if (combined.Count > 0)
            {
                var best = combined[0];
                var resolvedPqmCode = ResolveMatchedPqmCode(hDigits, best);
                bool isExact = ExactCodeMatch(hDigits, best.Code1) || ExactCodeMatch(hDigits, best.Code2);
                row.PqmCode = resolvedPqmCode ?? "";
                row.PqmName = best.Name ?? "";
                row.PqmCodeMatch = true;
                row.PqmNameMatch = true;
                row.PqmNeedsReview = !isExact;
                row.PqmResult = "PASS";
                row.ValidationResult = "PASS";
                row.PqmExceptionReason = isExact ? "" :
                    $"Pass - CESM review required because CESM leading digits matched against PQM.CESM_CODE. " +
                    $"Qualification Name ({row.QualificationDescription003}): '{qualName}' = Authorised_Qualification_Name: '{best.Name}' | " +
                    $"CESM._006: '{cesmCode}' | PQM CESM_Code: '{resolvedPqmCode}'";
                row.ValidationExplanation = isExact
                    ? $"PASS — PQM code '{resolvedPqmCode}' (exact) and name '{qualName}' confirmed against PQM register."
                    : $"PASS (review) — CESM leading digits matched PQM code '{resolvedPqmCode}'. Name '{qualName}' confirmed. Review CESM code precision.";
                return;
            }

            if (codeRows.Count == 0)
            {
                var review = GetCesmReviewMatch(cesmCode, pqm);
                if (review != null)
                {
                    var reviewNameMatches = string.Equals(NormName(review.PqmName), hNorm, StringComparison.Ordinal);
                    row.PqmCode = review.PqmCode ?? "";
                    row.PqmName = review.PqmName ?? "";
                    row.PqmCodeMatch = false;
                    row.PqmNameMatch = reviewNameMatches;
                    row.PqmNeedsReview = reviewNameMatches;
                    row.PqmResult = reviewNameMatches ? "PASS" : "FAIL";
                    row.ValidationResult = row.PqmResult;
                    row.PqmExceptionReason = reviewNameMatches
                        ? $"Pass - CESM review required ({review.Reason}). " +
                          $"Qualification Name: '{qualName}' = Authorised_Qualification_Name: '{review.PqmName}' | " +
                          $"CESM._006: '{cesmCode}' | PQM CESM_Code: '{review.PqmCode}' (CESM leading digits matched for review)"
                        : $"Fail - qualification name did not align. " +
                          $"Qualification Name: '{qualName}' ≠ Authorised_Qualification_Name: '{review.PqmName}' | " +
                          $"CESM._006: '{cesmCode}' | PQM CESM_Code: '{review.PqmCode}' (CESM leading digits matched for review)";
                    row.ValidationExplanation = reviewNameMatches
                        ? $"PASS (review) — {review.Reason}. Name '{qualName}' confirmed. CESM '{cesmCode}' → PQM '{review.PqmCode}'."
                        : $"FAIL — Name mismatch after leading-digit CESM match. Expected '{qualName}', PQM has '{review.PqmName}'. CESM '{cesmCode}' → PQM '{review.PqmCode}'.";
                    return;
                }
                row.PqmCode = "";
                row.PqmName = "";
                row.PqmCodeMatch = false;
                row.PqmNameMatch = false;
                row.PqmNeedsReview = false;
                row.PqmResult = "FAIL";
                row.ValidationResult = "FAIL";
                row.PqmExceptionReason = $"Fail - no PQM match found. CESM._006: '{cesmCode}' not found in PQM (no 4-digit prefix match in CESM_Code or CESM_Code2)";
                row.ValidationExplanation = $"FAIL — No PQM match. CESM code '{cesmCode}' not found in PQM register (CESM_Code / CESM_Code2).";
                return;
            }

            var bestCode = codeRows[0];
            var resolvedCode = ResolveMatchedPqmCode(hDigits, bestCode);
            var pqmNames = string.Join(" | ", codeRows.Take(3).Select(p => p.Name?.Trim()).Where(n => n != null).Distinct());
            row.PqmCode = resolvedCode ?? "";
            row.PqmName = bestCode.Name ?? "";
            row.PqmCodeMatch = true;
            row.PqmNameMatch = false;
            row.PqmNeedsReview = false;
            row.PqmResult = "FAIL";
            row.ValidationResult = "FAIL";
            row.PqmExceptionReason = $"Fail - qualification name did not align. " +
                $"Qualification Name: '{qualName}' ≠ Authorised_Qualification_Name: '{pqmNames}' | " +
                $"CESM._006: '{cesmCode}' | PQM CESM_Code: '{resolvedCode}'";
            row.ValidationExplanation = $"FAIL — Name mismatch. CESM code '{cesmCode}' → PQM '{resolvedCode}'. PQM name(s): '{pqmNames}'. Expected: '{qualName}'.";
        }

        // ── Analysis ────────────────────────────────────────────────────────────────────

        private async Task<Rule13ValidationSummary> AnalyseAsync(Rule13ValidationRequest request, bool includeAllReviewRows)
        {
            var cesmIdCol = string.IsNullOrWhiteSpace(request.CesmIdCol) ? "_001" : request.CesmIdCol;
            var cesmCodeCol = string.IsNullOrWhiteSpace(request.CesmCodeCol) ? "_006" : request.CesmCodeCol;
            var qualIdCol = string.IsNullOrWhiteSpace(request.QualIdCol) ? "_001" : request.QualIdCol;
            var qualNameCol = string.IsNullOrWhiteSpace(request.QualNameCol) ? "_003" : request.QualNameCol;
            var studIdCol = string.IsNullOrWhiteSpace(request.StudIdCol) ? "_001" : request.StudIdCol;
            var pqmTable = (request.PqmTable ?? "").Trim();
            var pqmNameCol = string.IsNullOrWhiteSpace(request.PqmNameCol) ? "Authorised_Qualification_Name" : request.PqmNameCol;
            var pqmCode1Col = string.IsNullOrWhiteSpace(request.PqmCode1Col) ? "CESM_Code" : request.PqmCode1Col;
            var pqmCode2Col = string.IsNullOrWhiteSpace(request.PqmCode2Col) ? "CESM_Code2" : request.PqmCode2Col;
            var hasPqm = !string.IsNullOrWhiteSpace(pqmTable);

            await ValidateColumnsExistAsync(request.ClientId, request.StudTable, cesmIdCol, cesmCodeCol);
            await ValidateColumnsExistAsync(request.ClientId, request.QualTable, qualIdCol, qualNameCol);
            await ValidateColumnsExistAsync(request.ClientId, request.CregTable, studIdCol, "_007");
            if (hasPqm)
                await ValidateColumnsExistAsync(request.ClientId, pqmTable, pqmNameCol, pqmCode1Col);

            await EnsureRule13IndexesAsync(request.ClientId, request.CregTable, studIdCol);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            // These QUAL display columns don't always exist on an analyst's uploaded table —
            // degrade gracefully to blank rather than failing the whole validation.
            var qualColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(request.ClientId, request.QualTable), StringComparer.OrdinalIgnoreCase);
            var hasApproval = qualColumns.Contains("_004");
            var hasType = qualColumns.Contains("_005");
            var hasLegacy = qualColumns.Contains("_084");
            var hasNqf = qualColumns.Contains("_085");
            var hasCredits = qualColumns.Contains("_086");

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule13PrepSql(schema, request.StudTable, request.QualTable, request.CregTable,
                    cesmIdCol, cesmCodeCol, qualIdCol, qualNameCol, studIdCol,
                    hasApproval, hasType, hasLegacy, hasNqf, hasCredits);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var reviewRows = new List<Rule13ReviewRowViewModel>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT * FROM rule13_validation ORDER BY ""Extract_Number"";";
                await using var reader = await command.ExecuteReaderAsync();
                var validationNumber = 0;
                while (await reader.ReadAsync())
                {
                    validationNumber++;
                    string ReadCol(string col) { var o = reader.GetOrdinal(col); return reader.IsDBNull(o) ? "" : Convert.ToString(reader.GetValue(o), CultureInfo.InvariantCulture) ?? ""; }
                    int ReadIntCol(string col) { var o = reader.GetOrdinal(col); return reader.IsDBNull(o) ? 0 : Convert.ToInt32(reader.GetValue(o)); }

                    var qualificationCode = ReadCol("Qualification_Code");
                    var majorField = ReadCol("Major_Field_CESM");
                    var qualDesc = ReadCol("Qualification_Name_Designator");
                    var approvalStatus = ReadCol("Qualification_Approval_Status");
                    var qualType = ReadCol("Qualification_Type_Descriptor");
                    var legacyIndicator = ReadCol("Legacy_Indicator");
                    var nqfExitLevel = ReadCol("NQF_Exit_Level");
                    var minCredits = ReadCol("Minimum_Total_Credits");
                    var studentCount = ReadIntCol("Linked_Student_Count");
                    var validationResult = ReadCol("Validation_Result");
                    var validationReason = ReadCol("Validation_Reason");

                    var studLinkResult = studentCount >= 1 ? "PASS" : "FAIL";
                    reviewRows.Add(new Rule13ReviewRowViewModel
                    {
                        ValidationNumber = validationNumber,
                        PartCode = ScopeCode,
                        PartTitle = ScopeTitle,
                        PartDescription = ScopeDescription,
                        StudentNumber007 = qualificationCode,
                        QualificationCode001 = qualificationCode,
                        QualificationDescription003 = qualDesc,
                        QualificationType005 = qualType,
                        FoundationFlag106 = approvalStatus,
                        BridgeQualificationCode001 = legacyIndicator,
                        CourseCode030 = majorField,
                        CrseCourseCode030 = nqfExitLevel,
                        FoundationCourse091 = minCredits,
                        StudentType = studentCount.ToString(),
                        StudLinkCount = studentCount,
                        StudLinkResult = studLinkResult,
                        NotebookStatus = validationResult == "PASS" ? "VALID" : "INVALID",
                        ValidationResult = validationResult,
                        ValidationExplanation = validationReason
                    });
                }
            }

            List<PqmRow> pqmRows = new();
            if (hasPqm)
            {
                await using var pqmCommand = connection.CreateCommand();
                pqmCommand.CommandText = string.IsNullOrWhiteSpace(pqmCode2Col)
                    ? $@"SELECT CAST(""{pqmNameCol}"" AS text), CAST(""{pqmCode1Col}"" AS text), NULL FROM ""{schema}"".""{pqmTable}"";"
                    : $@"SELECT CAST(""{pqmNameCol}"" AS text), CAST(""{pqmCode1Col}"" AS text), CAST(""{pqmCode2Col}"" AS text) FROM ""{schema}"".""{pqmTable}"";";
                await using var pqmReader = await pqmCommand.ExecuteReaderAsync();
                while (await pqmReader.ReadAsync())
                {
                    pqmRows.Add(new PqmRow(
                        pqmReader.IsDBNull(1) ? null : pqmReader.GetValue(1)?.ToString(),
                        pqmReader.IsDBNull(2) ? null : pqmReader.GetValue(2)?.ToString(),
                        pqmReader.IsDBNull(0) ? null : pqmReader.GetValue(0)?.ToString()));
                }
            }

            if (hasPqm && pqmRows.Count > 0)
            {
                foreach (var row in reviewRows)
                {
                    ApplyPqmMatch(row, row.CourseCode030, row.QualificationDescription003, pqmRows);
                    if (row.StudLinkResult == "FAIL")
                    {
                        row.ValidationResult = "FAIL";
                        row.ValidationExplanation = string.IsNullOrWhiteSpace(row.PqmExceptionReason)
                            ? $"FAIL — No linked STUD record (Linked_Student_Count = 0). PQM result: {row.PqmResult}."
                            : $"FAIL — No linked STUD record (Linked_Student_Count = 0). PQM: {row.PqmResult}. {row.PqmExceptionReason}";
                    }
                }
            }
            else
            {
                foreach (var row in reviewRows)
                {
                    if (row.StudLinkResult == "FAIL")
                        row.ValidationResult = "FAIL";
                }
            }

            reviewRows = NormalizeReviewRows(reviewRows);

            var totalValidated = reviewRows.Count;
            var passCount = reviewRows.Count(x => string.Equals(x.ValidationResult, "PASS", StringComparison.OrdinalIgnoreCase));
            var failCount = reviewRows.Count(x => string.Equals(x.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase));
            var reviewCount = reviewRows.Count(x => x.PqmNeedsReview);
            var partSummaries = new List<Rule13PartSummaryItemViewModel>
            {
                new Rule13PartSummaryItemViewModel
                {
                    PartCode = ScopeCode,
                    PartTitle = ScopeTitle,
                    PartDescription = ScopeDescription,
                    TotalCount = totalValidated,
                    PassCount = passCount,
                    FailCount = failCount,
                    Status = failCount == 0 ? "PASS" : "FAIL"
                }
            };
            var displayedRows = includeAllReviewRows ? reviewRows : reviewRows.Take(BrowserPreviewRowLimit).ToList();
            var isPreviewOnly = !includeAllReviewRows && totalValidated > displayedRows.Count;
            var overallStatusRuleText = hasPqm
                ? "Overall PASS requires every qualifying CESM qualification to have a matching PQM row (CESM code and qualification name) and link to at least one STUD row."
                : "Overall PASS requires every qualifying CESM qualification to link to at least one STUD row.";
            var runStatus = failCount == 0 ? (reviewCount > 0 ? "PASS WITH REVIEW" : "PASS") : "FAIL";

            return new Rule13ValidationSummary
            {
                Success = true,
                FoundationStudentCount = totalValidated,
                TotalValidated = totalValidated,
                DisplayedCount = displayedRows.Count,
                IsPreviewOnly = isPreviewOnly,
                PreviewLimit = isPreviewOnly ? BrowserPreviewRowLimit : 0,
                PassCount = passCount,
                FailCount = failCount,
                ReviewCount = reviewCount,
                ExceptionRate = totalValidated == 0 ? 0m : Math.Round(failCount * 100m / totalValidated, 2),
                Status = runStatus,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable = request.StudTable,
                QualTable = request.QualTable,
                CregTable = request.CregTable,
                CrseTable = request.CregTable,
                PgTypesText = "",
                PgTypes = new List<string>(),
                GoverningPartCodes = [ScopeCode],
                GoverningPartCodesText = "100% population",
                OverallStatusRuleText = overallStatusRuleText,
                TableLinkageText = $"{request.StudTable}.{cesmIdCol} -> {request.QualTable}.{qualIdCol} -> {request.CregTable}.{studIdCol}",
                ProcedureSteps = new List<string>
                {
                    $"Step 1: Extract 100% of {request.StudTable} where {cesmCodeCol} <> 'ZZZZZZ'.",
                    $"Step 2: Match {request.StudTable}.{cesmIdCol} to {request.QualTable}.{qualIdCol}.",
                    $"Step 3: Match {request.QualTable}.{qualIdCol} to {request.CregTable}.{studIdCol}.",
                    "Step 4: PASS when the qualifying CESM qualification links to at least one STUD row.",
                    hasPqm
                        ? $"Step 5 (PQM): CESM.{cesmCodeCol} 4-digit prefix matches PQM.{pqmCode1Col} or PQM.{pqmCode2Col}, AND QUAL.{qualNameCol} (case-insensitive) matches PQM.{pqmNameCol} — both on the same PQM row."
                        : "Overall PASS: every qualifying CESM qualification must link to at least one STUD row."
                },
                ClientId = request.ClientId,
                PartSummaries = partSummaries,
                ReviewRows = displayedRows,
                PqmTable = request.PqmTable ?? "",
                CesmIdCol = cesmIdCol,
                CesmCodeCol = cesmCodeCol,
                QualIdCol = qualIdCol,
                QualNameCol = qualNameCol,
                StudIdCol = studIdCol,
                PqmNameCol = pqmNameCol,
                PqmCode1Col = pqmCode1Col,
                PqmCode2Col = pqmCode2Col,
                Warning = includeAllReviewRows
                    ? "Rule 13 completed with the full CESM qualification population."
                    : "Counts reflect the full CESM qualification population. Browser review rows are limited for performance."
            };
        }

        private async Task<int> SaveValidationRunAsync(Rule13ValidationRequest request, Rule13ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 13);

            var failRows = summary.ReviewRows.Where(row => string.Equals(row.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList();
            var persistedSummary = CreateBrowserPreview(summary);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 13,
                RuleName = "CESM Qualification Population Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.QualTable,
                StudColumn = request.CregTable,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(failRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(persistedSummary))
            }, userEmail, userName);

            summary.SavedRunId = runId;
            return runId;
        }

        // ── SQL builder (Postgres) ──────────────────────────────────────────────────────

        // Uploaded engagement tables have no indexes beyond their primary key. The student-linkage
        // join scans the full STUD-equivalent table on every run — building the expression index
        // once, up front, makes every run after the first fast.
        private async Task EnsureRule13IndexesAsync(int clientId, string studTable, string studIdCol)
        {
            await _datasets.EnsureJoinIndexAsync(clientId, studTable, studIdCol);
        }

        private static string BuildRule13PrepSql(
            string schema, string cesmTable, string qualTable, string studTable,
            string cesmIdCol, string cesmCodeCol, string qualIdCol, string qualNameCol, string studIdCol,
            bool hasApproval, bool hasType, bool hasLegacy, bool hasNqf, bool hasCredits)
        {
            string qcol(bool has, string name) => has ? $@"CAST(q.""{name}"" AS text)" : "NULL::text";

            return $@"
DROP TABLE IF EXISTS rule13_qual_dedup;
DROP TABLE IF EXISTS rule13_extracted;
DROP TABLE IF EXISTS rule13_validation;

-- Dedup QUAL by join key: if the same qualification code has multiple QUAL rows
-- (data-quality issue), keep just one so the join below can't fan out.
CREATE TEMP TABLE rule13_qual_dedup AS
SELECT DISTINCT ON (qual_id)
    qual_id, qual_name, approval_status, type_descriptor, legacy_indicator, nqf_exit_level, min_credits
FROM (
    SELECT
        UPPER(TRIM(CAST(q.""{qualIdCol}"" AS text))) AS qual_id,
        CAST(q.""{qualNameCol}"" AS text) AS qual_name,
        {qcol(hasApproval, "_004")} AS approval_status,
        {qcol(hasType, "_005")} AS type_descriptor,
        {qcol(hasLegacy, "_084")} AS legacy_indicator,
        {qcol(hasNqf, "_085")} AS nqf_exit_level,
        {qcol(hasCredits, "_086")} AS min_credits
    FROM ""{schema}"".""{qualTable}"" q
    WHERE q.""{qualIdCol}"" IS NOT NULL
) x
ORDER BY qual_id;

ANALYZE rule13_qual_dedup;

CREATE TEMP TABLE rule13_extracted AS
SELECT DISTINCT
    ROW_NUMBER() OVER (ORDER BY c.cesm_id, c.cesm_code) AS ""Extract_Number"",
    c.cesm_id AS ""Qualification_Code"",
    c.cesm_code AS ""Major_Field_CESM"",
    COALESCE(qd.qual_name, '') AS ""Qualification_Name_Designator"",
    COALESCE(qd.approval_status, '') AS ""Qualification_Approval_Status"",
    COALESCE(qd.type_descriptor, '') AS ""Qualification_Type_Descriptor"",
    COALESCE(qd.legacy_indicator, '') AS ""Legacy_Indicator"",
    COALESCE(qd.nqf_exit_level, '') AS ""NQF_Exit_Level"",
    COALESCE(qd.min_credits, '') AS ""Minimum_Total_Credits""
FROM (
    SELECT
        CAST(cesm.""{cesmIdCol}"" AS text) AS cesm_id,
        CAST(cesm.""{cesmCodeCol}"" AS text) AS cesm_code
    FROM ""{schema}"".""{cesmTable}"" cesm
    WHERE cesm.""{cesmIdCol}"" IS NOT NULL
      AND TRIM(COALESCE(CAST(cesm.""{cesmCodeCol}"" AS text), '')) NOT IN ('', 'ZZZZZZ')
) c
LEFT JOIN rule13_qual_dedup qd ON qd.qual_id = UPPER(TRIM(c.cesm_id));

ANALYZE rule13_extracted;

CREATE TEMP TABLE rule13_validation AS
SELECT
    e.""Extract_Number"", e.""Qualification_Code"", e.""Major_Field_CESM"",
    e.""Qualification_Name_Designator"", e.""Qualification_Approval_Status"",
    e.""Qualification_Type_Descriptor"", e.""Legacy_Indicator"", e.""NQF_Exit_Level"", e.""Minimum_Total_Credits"",
    COUNT(DISTINCT s.stud_number) AS ""Linked_Student_Count"",
    CASE WHEN COUNT(DISTINCT s.stud_number) >= 1 THEN 'PASS' ELSE 'FAIL' END AS ""Validation_Result"",
    CASE
        WHEN COUNT(DISTINCT s.stud_number) >= 1 THEN 'CESM qualification has at least one linked student record.'
        ELSE 'CESM qualification has no linked student record in {studTable}.'
    END AS ""Validation_Reason""
FROM rule13_extracted e
LEFT JOIN (
    SELECT
        UPPER(TRIM(CAST(stud.""{studIdCol}"" AS text))) AS join_key,
        CAST(stud.""_007"" AS text) AS stud_number
    FROM ""{schema}"".""{studTable}"" stud
    WHERE stud.""{studIdCol}"" IS NOT NULL
) s ON s.join_key = UPPER(TRIM(e.""Qualification_Code""))
GROUP BY e.""Extract_Number"", e.""Qualification_Code"", e.""Major_Field_CESM"",
    e.""Qualification_Name_Designator"", e.""Qualification_Approval_Status"",
    e.""Qualification_Type_Descriptor"", e.""Legacy_Indicator"", e.""NQF_Exit_Level"", e.""Minimum_Total_Credits"";

ANALYZE rule13_validation;";
        }

        // ── Persistence / summary helpers ──────────────────────────────────────────────

        private async Task<Rule13ValidationSummary> ExpandAndPersistSavedSummaryIfNeededAsync(int runId, Rule13ValidationSummary summary, int clientId)
        {
            var looksLikeStoredPreviewSample =
                summary.ReviewRows.Count > 0 &&
                summary.ReviewRows.Count <= BrowserPreviewRowLimit &&
                summary.TotalValidated > 0;

            if (!summary.IsPreviewOnly && summary.ReviewRows.Count >= summary.TotalValidated && !looksLikeStoredPreviewSample)
                return summary;

            if (string.IsNullOrWhiteSpace(summary.StudTable) || string.IsNullOrWhiteSpace(summary.QualTable) || string.IsNullOrWhiteSpace(summary.CregTable))
                return summary;

            try
            {
                var expanded = await AnalyseAsync(new Rule13ValidationRequest
                {
                    ClientId = clientId,
                    RunId = summary.SavedRunId,
                    StudTable = summary.StudTable,
                    QualTable = summary.QualTable,
                    CregTable = summary.CregTable,
                    CrseTable = summary.CrseTable,
                    PgTypesText = summary.PgTypesText,
                    GoverningPartCodes = summary.GoverningPartCodes?.ToList() ?? new List<string>(),
                    CesmIdCol = string.IsNullOrWhiteSpace(summary.CesmIdCol) ? "_001" : summary.CesmIdCol,
                    CesmCodeCol = string.IsNullOrWhiteSpace(summary.CesmCodeCol) ? "_006" : summary.CesmCodeCol,
                    QualIdCol = string.IsNullOrWhiteSpace(summary.QualIdCol) ? "_001" : summary.QualIdCol,
                    QualNameCol = string.IsNullOrWhiteSpace(summary.QualNameCol) ? "_003" : summary.QualNameCol,
                    StudIdCol = string.IsNullOrWhiteSpace(summary.StudIdCol) ? "_001" : summary.StudIdCol,
                    PqmTable = summary.PqmTable ?? "",
                    PqmNameCol = string.IsNullOrWhiteSpace(summary.PqmNameCol) ? "Authorised_Qualification_Name" : summary.PqmNameCol,
                    PqmCode1Col = string.IsNullOrWhiteSpace(summary.PqmCode1Col) ? "CESM_Code" : summary.PqmCode1Col,
                    PqmCode2Col = string.IsNullOrWhiteSpace(summary.PqmCode2Col) ? "CESM_Code2" : summary.PqmCode2Col
                }, includeAllReviewRows: true);

                expanded.Timestamp = string.IsNullOrWhiteSpace(summary.Timestamp) ? expanded.Timestamp : summary.Timestamp;
                expanded.ClientId = summary.ClientId;
                expanded.SavedRunId = summary.SavedRunId;
                expanded.Warning = string.IsNullOrWhiteSpace(summary.Warning)
                    ? "Saved Rule 13 results were expanded from the stored preview to the full result set."
                    : $"{summary.Warning} Full saved results were reloaded from the saved Rule 13 configuration.";

                if (!ReferenceEquals(expanded, summary))
                {
                    expanded.SavedRunId = runId;
                    await UpdateStoredSummaryAsync(runId, expanded);
                }

                return expanded;
            }
            catch
            {
                return summary;
            }
        }

        private async Task UpdateStoredSummaryAsync(int runId, Rule13ValidationSummary summary)
        {
            var failRows = summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList();
            var persistedSummary = CreateBrowserPreview(summary);

            await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = summary.ClientId,
                RuleNumber = 13,
                RuleName = "CESM Qualification Population Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = summary.StudTable,
                DeceasedTable = summary.QualTable,
                StudColumn = summary.CregTable,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(failRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(persistedSummary))
            }, null, null);
        }

        private static Rule13ValidationSummary CreateBrowserPreview(Rule13ValidationSummary summary)
        {
            var normalizedRows = NormalizeReviewRows(summary.ReviewRows);
            var previewRows = BuildBrowserPreviewRows(normalizedRows);

            return new Rule13ValidationSummary
            {
                Success = summary.Success,
                FoundationStudentCount = summary.FoundationStudentCount,
                TotalValidated = summary.TotalValidated,
                DisplayedCount = previewRows.Count,
                IsPreviewOnly = summary.TotalValidated > previewRows.Count,
                PreviewLimit = BrowserPreviewRowLimit,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ReviewCount = summary.ReviewCount,
                ExceptionRate = summary.ExceptionRate,
                Status = summary.Status,
                Timestamp = summary.Timestamp,
                StudTable = summary.StudTable,
                QualTable = summary.QualTable,
                CregTable = summary.CregTable,
                CrseTable = summary.CrseTable,
                PgTypesText = summary.PgTypesText,
                PgTypes = summary.PgTypes.ToList(),
                GoverningPartCodes = summary.GoverningPartCodes.ToList(),
                GoverningPartCodesText = summary.GoverningPartCodesText,
                OverallStatusRuleText = summary.OverallStatusRuleText,
                TableLinkageText = summary.TableLinkageText,
                ProcedureSteps = summary.ProcedureSteps.ToList(),
                ClientId = summary.ClientId,
                SavedRunId = summary.SavedRunId,
                PartSummaries = (summary.PartSummaries ?? new List<Rule13PartSummaryItemViewModel>())
                    .Select(item => new Rule13PartSummaryItemViewModel
                    {
                        PartCode = item.PartCode,
                        PartTitle = item.PartTitle,
                        PartDescription = item.PartDescription,
                        TotalCount = item.TotalCount,
                        PassCount = item.PassCount,
                        FailCount = item.FailCount,
                        Status = item.Status
                    })
                    .ToList(),
                ReviewRows = previewRows,
                Warning = summary.Warning,
                Error = summary.Error,
                PqmTable = summary.PqmTable,
                CesmIdCol = summary.CesmIdCol,
                CesmCodeCol = summary.CesmCodeCol,
                QualIdCol = summary.QualIdCol,
                QualNameCol = summary.QualNameCol,
                StudIdCol = summary.StudIdCol,
                PqmNameCol = summary.PqmNameCol,
                PqmCode1Col = summary.PqmCode1Col,
                PqmCode2Col = summary.PqmCode2Col
            };
        }

        private static List<Rule13ReviewRowViewModel> BuildBrowserPreviewRows(IEnumerable<Rule13ReviewRowViewModel>? rows)
        {
            var normalizedRows = NormalizeReviewRows(rows);
            return NormalizeReviewRows(
                normalizedRows
                    .OrderBy(row => GetPartSortOrder(row.PartCode))
                    .ThenBy(row => TryParseLong(row.StudentNumber007).HasValue ? 0 : 1)
                    .ThenBy(row => TryParseLong(row.StudentNumber007))
                    .ThenBy(row => row.StudentNumber007)
                    .ThenBy(row => row.QualificationCode001)
                    .ThenBy(row => row.CourseCode030)
                    .Take(BrowserPreviewRowLimit));
        }

        private static void ApplyBrowserPreview(Rule13ValidationSummary summary)
        {
            var preview = CreateBrowserPreview(summary);
            summary.DisplayedCount = preview.DisplayedCount;
            summary.IsPreviewOnly = preview.IsPreviewOnly;
            summary.PreviewLimit = preview.PreviewLimit;
            summary.ReviewRows = preview.ReviewRows;
        }

        private static List<Rule13ReviewRowViewModel> NormalizeReviewRows(IEnumerable<Rule13ReviewRowViewModel>? rows)
        {
            var normalizedRows = (rows ?? Enumerable.Empty<Rule13ReviewRowViewModel>()).ToList();

            for (var i = 0; i < normalizedRows.Count; i++)
            {
                normalizedRows[i].StudentNumber007 = NormalizeRowText(normalizedRows[i].StudentNumber007);
                normalizedRows[i].QualificationCode001 = NormalizeRowText(normalizedRows[i].QualificationCode001);
                normalizedRows[i].FoundationFlag106 = NormalizeRowText(normalizedRows[i].FoundationFlag106);
                normalizedRows[i].QualificationDescription003 = NormalizeRowText(normalizedRows[i].QualificationDescription003);
                normalizedRows[i].QualificationType005 = NormalizeRowText(normalizedRows[i].QualificationType005);
                normalizedRows[i].BridgeQualificationCode001 = NormalizeRowText(normalizedRows[i].BridgeQualificationCode001);
                normalizedRows[i].CourseCode030 = NormalizeRowText(normalizedRows[i].CourseCode030);
                normalizedRows[i].CrseCourseCode030 = NormalizeRowText(normalizedRows[i].CrseCourseCode030);
                normalizedRows[i].FoundationCourse091 = NormalizeRowText(normalizedRows[i].FoundationCourse091);
                normalizedRows[i].StudentType = NormalizeRowText(normalizedRows[i].StudentType);
                normalizedRows[i].NotebookStatus = NormalizeRowText(normalizedRows[i].NotebookStatus);
                normalizedRows[i].ValidationResult = NormalizeRowText(normalizedRows[i].ValidationResult);
                normalizedRows[i].ValidationExplanation = NormalizeRowText(normalizedRows[i].ValidationExplanation);
                normalizedRows[i].PartCode = ScopeCode;
                normalizedRows[i].PartTitle = ScopeTitle;
                normalizedRows[i].PartDescription = ScopeDescription;
            }

            normalizedRows = normalizedRows
                .OrderBy(r => TryParseLong(r.StudentNumber007).HasValue ? 0 : 1)
                .ThenBy(r => TryParseLong(r.StudentNumber007))
                .ThenBy(r => r.StudentNumber007)
                .ThenBy(r => r.QualificationCode001)
                .ThenBy(r => r.CourseCode030)
                .ToList();

            for (var i = 0; i < normalizedRows.Count; i++)
            {
                normalizedRows[i].ValidationNumber = i + 1;
                var hasPqmFail = !string.IsNullOrEmpty(normalizedRows[i].PqmResult) &&
                    string.Equals(normalizedRows[i].PqmResult, "FAIL", StringComparison.OrdinalIgnoreCase);
                var isPass = !hasPqmFail && (
                    IsRule13RowPass(normalizedRows[i]) ||
                    string.Equals(normalizedRows[i].ValidationResult, "PASS", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedRows[i].NotebookStatus, "VALID", StringComparison.OrdinalIgnoreCase));
                normalizedRows[i].NotebookStatus = isPass ? "VALID" : "INVALID";
                normalizedRows[i].ValidationResult = isPass ? "PASS" : "FAIL";
                normalizedRows[i].ValidationExplanation = string.IsNullOrWhiteSpace(normalizedRows[i].ValidationExplanation)
                    ? BuildValidationExplanation(
                        normalizedRows[i].FoundationFlag106,
                        normalizedRows[i].QualificationCode001,
                        normalizedRows[i].FoundationCourse091)
                    : normalizedRows[i].ValidationExplanation;
            }

            return normalizedRows;
        }

        private static string BuildValidationExplanation(string step1Status, string qualificationCode001, string linkedStud001)
        {
            var failedChecks = new List<string>();
            step1Status = NormalizeRowText(step1Status);
            qualificationCode001 = NormalizeRowText(qualificationCode001);
            linkedStud001 = NormalizeRowText(linkedStud001);

            if (!string.Equals(step1Status, "PASS", StringComparison.OrdinalIgnoreCase))
                failedChecks.Add("CESM._006 is blank or equals 'ZZZZZZ'");
            if (string.IsNullOrWhiteSpace(qualificationCode001))
                failedChecks.Add("qualification code _001 is blank");
            if (string.IsNullOrWhiteSpace(linkedStud001))
                failedChecks.Add("no linked STUD row was found through QUAL._001 = STUD._001");

            return failedChecks.Count == 0
                ? $"PASS: the CESM qualification is part of the 100% qualifying population and links to STUD._001 '{linkedStud001}'."
                : $"FAIL: {string.Join("; ", failedChecks)}.";
        }

        private static int GetPartSortOrder(string? partCode) =>
            Array.IndexOf(PartOrder, (partCode ?? "").Trim().ToUpperInvariant()) switch
            {
                >= 0 and var idx => idx,
                _ => PartOrder.Length
            };

        private static long? TryParseLong(string? value) => long.TryParse(value, out var parsed) ? parsed : null;

        private static string NormalizeRowText(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

        private static bool IsRule13RowPass(Rule13ReviewRowViewModel row) =>
            string.Equals(NormalizeRowText(row.FoundationFlag106), "PASS", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(NormalizeRowText(row.StudentType), out var studentCount) &&
            studentCount >= 1 &&
            !string.IsNullOrWhiteSpace(NormalizeRowText(row.FoundationCourse091));

        private static List<string> NormalizeGoverningPartCodes(IEnumerable<string>? governingPartCodes) => [ScopeCode];

        // ── Misc helpers ─────────────────────────────────────────────────────────────────

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

        private static void ValidateRequest(string studTable, string qualTable, string cregTable)
        {
            if (string.IsNullOrWhiteSpace(studTable) || string.IsNullOrWhiteSpace(qualTable) || string.IsNullOrWhiteSpace(cregTable))
                throw new InvalidOperationException("CESM, QUAL, and STUD tables are required.");
        }

        private static string NormalizeObjectName(string? name, string fallback = "") =>
            string.IsNullOrWhiteSpace(name) ? fallback : name;

        private static string? FindFirst(IEnumerable<string> values, string[] exactMatches, string[] containsMatches)
        {
            foreach (var exact in exactMatches)
            {
                var match = values.FirstOrDefault(c => c.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
            foreach (var fragment in containsMatches)
            {
                var match = values.FirstOrDefault(c => c.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
            return values.FirstOrDefault();
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase);

        private static Rule13ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                if (string.IsNullOrWhiteSpace(decoded)) return null;
                return JsonConvert.DeserializeObject<Rule13ValidationSummary>(decoded);
            }
            catch { return null; }
        }
    }
}
