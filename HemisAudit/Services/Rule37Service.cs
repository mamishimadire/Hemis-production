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
    // Rule 37: CESM vs PQM Validation — validates against the engagement's own uploaded Supabase
    // data instead of a live SQL Server connection. The digit-prefix/name-normalization matching
    // logic (ValidateRecord and its helpers) is pure C# with no SQL dependency and is ported
    // unchanged from the original; only the CESM/QUAL/PQM row retrieval was translated to Postgres.
    // The original SQL-Server design loaded every CESM/QUAL row (already merged) into memory with
    // no cap — RowLimit is introduced here from the start, matching the house style established for
    // every rule this session.
    public class Rule37Service : IRule37Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const int RowLimit = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule37Service(
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

        // ── Digit extraction & normalisation (unchanged from the original) ────────────────

        private static string Digits(string? v) =>
            v == null ? "" : string.Concat(v.Where(char.IsDigit));

        private static string TrimLeadingZeros(string s) =>
            s.TrimStart('0') is { Length: > 0 } t ? t : s;

        private static string NormName(string? v)
        {
            if (v == null) return "";
            return Regex.Replace(v.Trim().ToUpperInvariant(), @"\s+", " ");
        }

        private static bool CodeMatches(string hDigits, string? pqmCode, int n = 4)
        {
            var p = Digits(pqmCode);
            if (string.IsNullOrEmpty(hDigits) || string.IsNullOrEmpty(p)) return false;
            var use = Math.Min(n, Math.Min(hDigits.Length, p.Length));
            return use >= 2 && string.Equals(hDigits[..use], p[..use], StringComparison.Ordinal);
        }

        private static bool ExactCodeMatch(string hDigits, string? pqmCode)
        {
            var p = Digits(pqmCode);
            return !string.IsNullOrEmpty(hDigits) && !string.IsNullOrEmpty(p) &&
                   string.Equals(hDigits, p, StringComparison.Ordinal);
        }

        private static bool HasSameLeadingDigits(string left, string right, int digits) =>
            left.Length >= digits && right.Length >= digits &&
            string.Equals(left[..digits], right[..digits], StringComparison.Ordinal);

        private record CesmReviewMatch(string Reason, string? PqmCode, string? PqmName);

        private static int CesmReviewPriority(string reason) => reason switch
        {
            "first 4 digits matched" => 0,
            "first 4 digits matched after removing leading zeros" => 1,
            "first 3 digits matched" => 2,
            "first 3 digits matched after removing leading zeros" => 3,
            _ => 99
        };

        private static CesmReviewMatch? GetCesmReviewMatch(string? cesmCode, List<PqmRow> pqm)
        {
            if (string.IsNullOrWhiteSpace(cesmCode)) return null;
            var rawCode = Digits(cesmCode);
            if (rawCode.Length < 3) return null;
            var trimmedCode = TrimLeadingZeros(rawCode);

            CesmReviewMatch? best = null;
            foreach (var p in pqm)
            {
                foreach (var pqmCode in new[] { p.Code1, p.Code2 }
                    .Where(c => !string.IsNullOrWhiteSpace(c)))
                {
                    var pqmRaw = Digits(pqmCode);
                    if (string.IsNullOrEmpty(pqmRaw)) continue;
                    var pqmTrimmed = TrimLeadingZeros(pqmRaw);
                    string? reason = null;
                    if (HasSameLeadingDigits(rawCode, pqmRaw, 4))
                        reason = "first 4 digits matched";
                    else if (HasSameLeadingDigits(trimmedCode, pqmTrimmed, 4))
                        reason = "first 4 digits matched after removing leading zeros";
                    else if (HasSameLeadingDigits(rawCode, pqmRaw, 3))
                        reason = "first 3 digits matched";
                    else if (HasSameLeadingDigits(trimmedCode, pqmTrimmed, 3))
                        reason = "first 3 digits matched after removing leading zeros";

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

        // ── Internal record types ─────────────────────────────────────────────

        private static string? ResolveMatchedPqmCode(string hemisDigits, PqmRow pqm)
        {
            if (CodeMatches(hemisDigits, pqm.Code1)) return pqm.Code1?.Trim();
            if (CodeMatches(hemisDigits, pqm.Code2)) return pqm.Code2?.Trim();
            return pqm.Code1?.Trim() ?? pqm.Code2?.Trim();
        }

        private record HemisRecord(string RecordId, string CesmCode, string QualName);
        private record PqmRow(string? Code1, string? Code2, string? Name);

        // ── Core validation (unchanged from the original) ──────────────────────

        private static Rule37ValidationRow ValidateRecord(
            int rowNo, HemisRecord h, List<PqmRow> pqm)
        {
            var hDigits = Digits(h.CesmCode);
            var hNorm = NormName(h.QualName);

            var codeRows = pqm
                .Where(p => CodeMatches(hDigits, p.Code1) || CodeMatches(hDigits, p.Code2))
                .ToList();

            var combined = codeRows
                .Where(p => string.Equals(NormName(p.Name), hNorm, StringComparison.Ordinal))
                .ToList();

            if (combined.Count > 0)
            {
                var best = combined[0];
                var resolvedPqmCode = ResolveMatchedPqmCode(hDigits, best);
                bool isExact = ExactCodeMatch(hDigits, best.Code1) || ExactCodeMatch(hDigits, best.Code2);

                if (isExact)
                {
                    return new Rule37ValidationRow
                    {
                        ValidationNumber = rowNo,
                        RecordId = h.RecordId,
                        HemisCesmCode = h.CesmCode,
                        HemisQualName = h.QualName,
                        PqmCode = resolvedPqmCode,
                        PqmName = best.Name,
                        CodeMatch = true,
                        NameMatch = true,
                        ValidationResult = "PASS",
                        ExceptionReason = null
                    };
                }

                // 4-digit prefix matched but codes are not identical — CESM review required
                return new Rule37ValidationRow
                {
                    ValidationNumber = rowNo,
                    RecordId = h.RecordId,
                    HemisCesmCode = h.CesmCode,
                    HemisQualName = h.QualName,
                    PqmCode = resolvedPqmCode,
                    PqmName = best.Name,
                    CodeMatch = true,
                    NameMatch = true,
                    NeedsReview = true,
                    ValidationResult = "PASS",
                    ExceptionReason = $"Pass - CESM review required because first 4 digits matched against PQM.CESM_CODE. " +
                                      $"Qualification Name (_003): '{h.QualName}' = Authorised_Qualification_Name: '{best.Name}' | " +
                                      $"CESM._006: '{h.CesmCode}' | PQM CESM_Code: '{resolvedPqmCode}'"
                };
            }

            if (codeRows.Count == 0)
            {
                // No 4-digit prefix match — check for 3 or 4 leading-digit CESM review match
                var review = GetCesmReviewMatch(h.CesmCode, pqm);
                if (review != null)
                {
                    var reviewNameMatches = string.Equals(
                        NormName(review.PqmName),
                        hNorm,
                        StringComparison.Ordinal);

                    return new Rule37ValidationRow
                    {
                        ValidationNumber = rowNo,
                        RecordId = h.RecordId,
                        HemisCesmCode = h.CesmCode,
                        HemisQualName = h.QualName,
                        PqmCode = review.PqmCode,
                        PqmName = review.PqmName,
                        CodeMatch = false,
                        NameMatch = reviewNameMatches,
                        NeedsReview = reviewNameMatches,
                        ValidationResult = reviewNameMatches ? "PASS" : "FAIL",
                        ExceptionReason = reviewNameMatches
                            ? $"Pass - CESM review required ({review.Reason}). " +
                              $"Qualification Name (_003): '{h.QualName}' = Authorised_Qualification_Name: '{review.PqmName}' | " +
                              $"CESM._006: '{h.CesmCode}' | PQM CESM_Code: '{review.PqmCode}' (CESM leading digits matched for review)"
                            : $"Fail - qualification name did not align. " +
                              $"Qualification Name (_003): '{h.QualName}' ≠ Authorised_Qualification_Name: '{review.PqmName}' | " +
                              $"CESM._006: '{h.CesmCode}' | PQM CESM_Code: '{review.PqmCode}' (CESM leading digits matched for review)"
                    };
                }

                return new Rule37ValidationRow
                {
                    ValidationNumber = rowNo,
                    RecordId = h.RecordId,
                    HemisCesmCode = h.CesmCode,
                    HemisQualName = h.QualName,
                    PqmCode = null,
                    PqmName = null,
                    CodeMatch = false,
                    NameMatch = false,
                    ValidationResult = "FAIL",
                    ExceptionReason = $"Fail - qualification name did not align. " +
                                      $"Qualification Name (_003): '{h.QualName}' | Authorised_Qualification_Name: not found in PQM | " +
                                      $"CESM._006: '{h.CesmCode}' not found in PQM (no 4-digit prefix match in CESM_Code or CESM_Code2)"
                };
            }

            // Code matched (4-digit prefix), name did not — CESM review required
            var bestCode = codeRows[0];
            var reviewMatchedPqmCode = ResolveMatchedPqmCode(hDigits, bestCode);
            var pqmNames = string.Join(" | ",
                codeRows.Take(3)
                        .Select(p => p.Name?.Trim())
                        .Where(n => n != null)
                        .Distinct());

            return new Rule37ValidationRow
            {
                ValidationNumber = rowNo,
                RecordId = h.RecordId,
                HemisCesmCode = h.CesmCode,
                HemisQualName = h.QualName,
                PqmCode = reviewMatchedPqmCode,
                PqmName = bestCode.Name,
                CodeMatch = true,
                NameMatch = false,
                NeedsReview = false,
                ValidationResult = "FAIL",
                ExceptionReason = $"Fail - qualification name did not align. " +
                                  $"Qualification Name (_003): '{h.QualName}' ≠ Authorised_Qualification_Name: '{pqmNames}' | " +
                                  $"CESM._006: '{h.CesmCode}' | PQM CESM_Code: '{reviewMatchedPqmCode}'"
            };
        }

        // ── Engagement data source (uploaded tables, not a live SQL Server) ────────────────

        public async Task<Rule37TableListResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule37TableListResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule37TableListResult
                {
                    Success = true,
                    Tables = tables,
                    AutoCesmTable = tables.FirstOrDefault(t => t.Equals("dbo_CESM", StringComparison.OrdinalIgnoreCase)),
                    AutoQualTable = tables.FirstOrDefault(t => t.Equals("dbo_QUAL", StringComparison.OrdinalIgnoreCase)),
                    AutoPqmTable = tables.FirstOrDefault(t => t.Equals("PQM", StringComparison.OrdinalIgnoreCase))
                        ?? tables.FirstOrDefault(t => t.Contains("PQM", StringComparison.OrdinalIgnoreCase))
                };
            }
            catch (Exception ex)
            {
                return new Rule37TableListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<ColumnListResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);

                string? autoSelected = tableRole?.ToLowerInvariant() switch
                {
                    "cesm_id" => columns.FirstOrDefault(c => c.Equals("_001", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "cesm_code" => columns.FirstOrDefault(c => c.Equals("_006", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_id" => columns.FirstOrDefault(c => c.Equals("_001", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "qual_name" => columns.FirstOrDefault(c => c.Equals("_003", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_name" => columns.FirstOrDefault(c => c.Contains("Authorised", StringComparison.OrdinalIgnoreCase) || c.Contains("Qualification_Name", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_code1" => columns.FirstOrDefault(c => c.Equals("CESM_Code", StringComparison.OrdinalIgnoreCase) || c.Equals("CESM_Code1", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    "pqm_code2" => columns.FirstOrDefault(c => c.Equals("CESM_Code2", StringComparison.OrdinalIgnoreCase)) ?? columns.FirstOrDefault(),
                    _ => columns.FirstOrDefault()
                };

                return new ColumnListResult
                {
                    Success = true,
                    Columns = columns,
                    AutoSelected = autoSelected
                };
            }
            catch (Exception ex)
            {
                return new ColumnListResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule37VerifyResult> VerifyDataAsync(Rule37VerifyRequest request)
        {
            try
            {
                ValidateNames(request.CesmTable, request.QualTable, request.PqmTable, request.CesmIdCol, request.QualIdCol);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var cesmTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CesmTable}\";");
                var qualTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.QualTable}\";");
                var pqmTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.PqmTable}\";");

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
SELECT COUNT(*)
FROM ""{schema}"".""{request.CesmTable}"" c
INNER JOIN ""{schema}"".""{request.QualTable}"" q
    ON TRIM(CAST(c.""{request.CesmIdCol}"" AS text)) = TRIM(CAST(q.""{request.QualIdCol}"" AS text));";
                var mergedTotal = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                return new Rule37VerifyResult
                {
                    Success = true,
                    CesmTotal = cesmTotal,
                    QualTotal = qualTotal,
                    PqmTotal = pqmTotal,
                    MergedTotal = mergedTotal
                };
            }
            catch (Exception ex)
            {
                return new Rule37VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule37ValidationSummary> RunValidationAsync(Rule37ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateNames(
                    request.CesmTable, request.QualTable, request.PqmTable,
                    request.CesmIdCol, request.CesmCodeCol, request.QualIdCol, request.QualNameCol,
                    request.PqmNameCol, request.PqmCode1Col);
                if (!string.IsNullOrWhiteSpace(request.PqmCode2Col))
                    ValidateNames(request.PqmCode2Col);

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
                return new Rule37ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule37WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 37);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null)
                ApplyBrowserPreview(summary);

            var workspace = new Rule37WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                CesmTable = string.IsNullOrWhiteSpace(row.StudTable) ? "" : row.StudTable,
                QualTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "" : row.DeceasedTable,
                PqmTable = string.IsNullOrWhiteSpace(row.StudColumn) ? "" : row.StudColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (summary != null)
            {
                workspace.CesmIdCol = summary.CesmIdCol;
                workspace.CesmCodeCol = summary.CesmCodeCol;
                workspace.QualIdCol = summary.QualIdCol;
                workspace.QualNameCol = summary.QualNameCol;
                workspace.PqmNameCol = summary.PqmNameCol;
                workspace.PqmCode1Col = summary.PqmCode1Col;
                workspace.PqmCode2Col = summary.PqmCode2Col;
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

            if (string.IsNullOrWhiteSpace(workspace.CurrentStatus))
                workspace.CurrentStatus = workspace.Summary?.Status ?? "";

            return workspace;
        }

        public async Task<Rule37RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 37);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule37RunReviewViewModel
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

        public async Task<Rule37WorkspaceSaveResult> SaveWorkspaceAsync(Rule37ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (request.RunId is null || request.RunId <= 0)
                    return new Rule37WorkspaceSaveResult { Success = false, Error = "Run validation before saving the workspace." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule37WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.CesmTable,
                    DeceasedTable = request.QualTable,
                    StudColumn = request.PqmTable,
                    DeceasedColumn = ""
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule37WorkspaceSaveResult
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
                return new Rule37WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule37WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule37WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule37WorkspaceSaveResult
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
                return new Rule37WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 37 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 37 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 37 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public string GenerateSql(Rule37ValidationRequest request)
        {
            var schema = $"engagement_{request.ClientId}";
            var ct = request.CesmTable;
            var qt = request.QualTable;
            var pt = request.PqmTable;
            var ci = request.CesmIdCol;
            var cc = request.CesmCodeCol;
            var qi = request.QualIdCol;
            var qn = request.QualNameCol;
            var pn = request.PqmNameCol;
            var pc1 = request.PqmCode1Col;
            var pc2 = string.IsNullOrWhiteSpace(request.PqmCode2Col) ? "CESM_Code2" : request.PqmCode2Col;

            return $@"-- ============================================================================
-- HEMIS 2025 - RULE 37: CESM vs PQM VALIDATION
-- Source: this engagement's own uploaded tables (schema ""{schema}""), not a live SQL Server.
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- ============================================================================
-- TABLES
--   {ct}  : ""{ci}"" (record ID), ""{cc}"" (CESM code)
--   {qt}  : ""{qi}"" (record ID), ""{qn}"" (qualification name)
--   {pt}  : ""{pn}"", ""{pc1}"", ""{pc2}""
--
-- MATCHING RULES (both on the SAME PQM row):
--   1. LEFT(digits of ""{cc}"", 4) = LEFT(digits of ""{pc1}"" / ""{pc2}"", 4)
--   2. UPPER(TRIM(""{qn}"")) = UPPER(TRIM(""{pn}""))
-- ============================================================================

DROP TABLE IF EXISTS rule37_base;
DROP TABLE IF EXISTS rule37_val;

-- Step 1: CESM join QUAL -> (record_id, code, name) per row
SELECT
    c.""{ci}""  AS record_id,
    c.""{cc}""  AS hemis_cesm_code,
    q.""{qn}""  AS hemis_qual_name
INTO TEMP TABLE rule37_base
FROM ""{schema}"".""{ct}"" c
INNER JOIN ""{schema}"".""{qt}"" q ON TRIM(CAST(c.""{ci}"" AS text)) = TRIM(CAST(q.""{qi}"" AS text));

-- Step 2: Validate each HEMIS record against PQM
SELECT
    ROW_NUMBER() OVER (ORDER BY b.record_id)   AS validation_number,
    b.record_id,
    b.hemis_cesm_code,
    b.hemis_qual_name,

    (SELECT p.""{pc1}""::text
     FROM ""{schema}"".""{pt}"" p
     WHERE LEFT(TRIM(CAST(b.hemis_cesm_code AS text)), 4) = LEFT(TRIM(CAST(p.""{pc1}"" AS text)), 4)
        OR LEFT(TRIM(CAST(b.hemis_cesm_code AS text)), 4) = LEFT(TRIM(CAST(p.""{pc2}"" AS text)), 4)
     LIMIT 1)                                   AS pqm_matched_code,

    (SELECT p.""{pn}""::text
     FROM ""{schema}"".""{pt}"" p
     WHERE LEFT(TRIM(CAST(b.hemis_cesm_code AS text)), 4) = LEFT(TRIM(CAST(p.""{pc1}"" AS text)), 4)
        OR LEFT(TRIM(CAST(b.hemis_cesm_code AS text)), 4) = LEFT(TRIM(CAST(p.""{pc2}"" AS text)), 4)
     LIMIT 1)                                   AS pqm_matched_name,

    CASE WHEN EXISTS (
        SELECT 1 FROM ""{schema}"".""{pt}"" p
        WHERE LEFT(TRIM(CAST(b.hemis_cesm_code AS text)), 4) = LEFT(TRIM(CAST(p.""{pc1}"" AS text)), 4)
           OR LEFT(TRIM(CAST(b.hemis_cesm_code AS text)), 4) = LEFT(TRIM(CAST(p.""{pc2}"" AS text)), 4)
    ) THEN 'YES' ELSE 'NO' END                 AS code_matched,

    CASE WHEN EXISTS (
        SELECT 1 FROM ""{schema}"".""{pt}"" p
        WHERE (LEFT(TRIM(CAST(b.hemis_cesm_code AS text)), 4) = LEFT(TRIM(CAST(p.""{pc1}"" AS text)), 4)
            OR LEFT(TRIM(CAST(b.hemis_cesm_code AS text)), 4) = LEFT(TRIM(CAST(p.""{pc2}"" AS text)), 4))
          AND UPPER(TRIM(CAST(b.hemis_qual_name AS text))) = UPPER(TRIM(CAST(p.""{pn}"" AS text)))
    ) THEN 'PASS' ELSE 'FAIL' END              AS validation_result

INTO TEMP TABLE rule37_val
FROM rule37_base b;

-- Step 3: Summary
SELECT
    COUNT(*)                                                             AS total,
    SUM(CASE WHEN validation_result = 'PASS' THEN 1 ELSE 0 END)          AS pass_count,
    SUM(CASE WHEN validation_result = 'FAIL' THEN 1 ELSE 0 END)          AS fail_count,
    ROUND(SUM(CASE WHEN validation_result = 'FAIL' THEN 1 ELSE 0 END)
          * 100.0 / NULLIF(COUNT(*), 0), 2)                              AS exception_rate_pct
FROM rule37_val;

-- Step 4: Full split-panel result
SELECT validation_number, record_id,
       hemis_cesm_code, hemis_qual_name,
       pqm_matched_code, pqm_matched_name,
       code_matched, validation_result
FROM rule37_val ORDER BY validation_number;

-- Step 5: Exceptions only
SELECT * FROM rule37_val WHERE validation_result = 'FAIL' ORDER BY validation_number;

DROP TABLE rule37_val;
DROP TABLE rule37_base;
-- ============================================================================
-- END OF RULE 37 CESM vs PQM VALIDATION
-- ============================================================================
";
        }

        // ── Analysis ─────────────────────────────────────────────────────────────────────

        private async Task<Rule37ValidationSummary> AnalyseAsync(Rule37ValidationRequest request)
        {
            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            // Load CESM join QUAL
            var hemis = new List<HemisRecord>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT c.""{request.CesmIdCol}"", c.""{request.CesmCodeCol}"", q.""{request.QualNameCol}""
FROM ""{schema}"".""{request.CesmTable}"" c
INNER JOIN ""{schema}"".""{request.QualTable}"" q
    ON TRIM(CAST(c.""{request.CesmIdCol}"" AS text)) = TRIM(CAST(q.""{request.QualIdCol}"" AS text))
LIMIT @limit;";
                cmd.Parameters.AddWithValue("limit", RowLimit + 1);

                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    hemis.Add(new HemisRecord(
                        r.IsDBNull(0) ? "" : Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture) ?? "",
                        r.IsDBNull(1) ? "" : Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture) ?? "",
                        r.IsDBNull(2) ? "" : Convert.ToString(r.GetValue(2), CultureInfo.InvariantCulture) ?? ""));
                }
            }

            var rowsTruncated = hemis.Count > RowLimit;
            if (rowsTruncated)
                hemis = hemis.Take(RowLimit).ToList();

            var totalMerged = rowsTruncated
                ? await CountAsync(connection, $@"
SELECT COUNT(*)
FROM ""{schema}"".""{request.CesmTable}"" c
INNER JOIN ""{schema}"".""{request.QualTable}"" q
    ON TRIM(CAST(c.""{request.CesmIdCol}"" AS text)) = TRIM(CAST(q.""{request.QualIdCol}"" AS text));")
                : hemis.Count;

            // Load PQM
            var pqm = new List<PqmRow>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = string.IsNullOrWhiteSpace(request.PqmCode2Col)
                    ? $@"SELECT ""{request.PqmNameCol}"", ""{request.PqmCode1Col}"" FROM ""{schema}"".""{request.PqmTable}"";"
                    : $@"SELECT ""{request.PqmNameCol}"", ""{request.PqmCode1Col}"", ""{request.PqmCode2Col}"" FROM ""{schema}"".""{request.PqmTable}"";";

                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    pqm.Add(new PqmRow(
                        r.IsDBNull(1) ? null : Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture),
                        r.FieldCount > 2 && !r.IsDBNull(2) ? Convert.ToString(r.GetValue(2), CultureInfo.InvariantCulture) : null,
                        r.IsDBNull(0) ? null : Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)));
                }
            }

            // Validate in memory (unchanged business logic)
            var validationRows = hemis
                .Select((h, idx) => ValidateRecord(idx + 1, h, pqm))
                .ToList();

            var total = totalMerged;
            var passCount = validationRows.Count(r => r.ValidationResult == "PASS" && !r.NeedsReview);
            var failCount = validationRows.Count(r => r.ValidationResult == "FAIL");
            var reviewCount = validationRows.Count(r => r.NeedsReview);
            var rate = total > 0 ? Math.Round((decimal)failCount / total * 100, 2) : 0;

            var exceptions = validationRows
                .Where(r => r.ValidationResult == "FAIL" || r.NeedsReview)
                .Select(r => new Rule37ExceptionRecord
                {
                    ValidationNumber = r.ValidationNumber,
                    RecordId = r.RecordId,
                    HemisCesmCode = r.HemisCesmCode,
                    HemisQualName = r.HemisQualName,
                    PqmCode = r.PqmCode,
                    PqmName = r.PqmName,
                    CodeMatch = r.CodeMatch,
                    NameMatch = r.NameMatch,
                    NeedsReview = r.NeedsReview,
                    ValidationResult = r.ValidationResult,
                    ExceptionReason = r.ExceptionReason ?? ""
                })
                .ToList();

            return new Rule37ValidationSummary
            {
                Success = true,
                TotalValidated = total,
                PassCount = passCount,
                FailCount = failCount,
                ReviewCount = reviewCount,
                ExceptionRate = rate,
                Status = failCount == 0 ? (reviewCount == 0 ? "PASS" : "PASS WITH REVIEW") : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                CesmTable = request.CesmTable,
                QualTable = request.QualTable,
                PqmTable = request.PqmTable,
                CesmIdCol = request.CesmIdCol,
                CesmCodeCol = request.CesmCodeCol,
                QualIdCol = request.QualIdCol,
                QualNameCol = request.QualNameCol,
                PqmNameCol = request.PqmNameCol,
                PqmCode1Col = request.PqmCode1Col,
                PqmCode2Col = request.PqmCode2Col,
                ClientId = request.ClientId,
                RowsTruncated = rowsTruncated,
                ValidationRows = validationRows,
                Exceptions = exceptions,
                Warning = rowsTruncated
                    ? $"Only the first {RowLimit:N0} merged CESM/QUAL rows were analysed for browser review and export performance. Total merged records: {total:N0}."
                    : null
            };
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule37ValidationRequest request, Rule37ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 37);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 37,
                RuleName = "CESM vs PQM Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.CesmTable,
                DeceasedTable = request.QualTable,
                StudColumn = request.PqmTable,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.Exceptions)),
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

        private static Rule37ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule37ValidationSummary>(decoded);
            }
            catch { return null; }
        }

        private static void ApplyBrowserPreview(Rule37ValidationSummary summary)
        {
            summary.ValidationRows = summary.ValidationRows.Take(BrowserPreviewRowLimit).ToList();
            summary.Exceptions = summary.Exceptions.Take(BrowserPreviewRowLimit).ToList();
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
