using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 14: validates against the engagement's own uploaded Supabase data instead of a live
    // SQL Server connection, and saves through the shared Postgres-native persistence layer.
    // Ported from the Rule15/16 pattern. Effectively a 2-table rule — StudTable actually holds
    // the CRSE (course) table, BridgeTable holds the CREG (registration) table; CrseTable is a
    // vestigial third property that always mirrors BridgeTable and isn't queried separately
    // (preserved for shape compatibility with the other rule view models).
    public class Rule14Service : IRule14Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IPendingValidationCacheService _pendingValidationCache;

        public Rule14Service(
            IConfiguration configuration,
            IEngagementDatasetService datasets,
            ISystemDatabaseService systemDb,
            UserManager<ApplicationUser> userManager,
            IPendingValidationCacheService pendingValidationCache)
        {
            _configuration = configuration;
            _datasets = datasets;
            _systemDb = systemDb;
            _userManager = userManager;
            _pendingValidationCache = pendingValidationCache;
        }

        // ── Engagement data source (uploaded tables, not a live SQL Server) ────────────────

        public async Task<Rule14TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule14TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule14TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, new[] { "dbo_CRSE", "dbo_crse", "CRSE", "crse" }, new[] { "crse" }),
                    AutoBridgeTable = FindFirst(tables, new[] { "dbo_CREG", "dbo_creg", "CREG", "creg" }, new[] { "creg" }),
                    AutoCrseTable = FindFirst(tables, new[] { "dbo_CREG", "dbo_creg", "CREG", "creg" }, new[] { "creg" })
                };
            }
            catch (Exception ex)
            {
                return new Rule14TableDiscoveryResult { Success = false, Error = ex.Message };
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

        public async Task<Rule14VerifyResult> VerifyTablesAsync(Rule14VerifyRequest request)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable);
                var crseApprovedCol = string.IsNullOrWhiteSpace(request.CrseApprovedCol) ? "_031" : request.CrseApprovedCol;
                var crseLinkCol = string.IsNullOrWhiteSpace(request.CrseLinkCol) ? "_030" : request.CrseLinkCol;
                var cregLinkCol = string.IsNullOrWhiteSpace(request.CregLinkCol) ? "_030" : request.CregLinkCol;

                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, crseLinkCol, crseApprovedCol);
                await ValidateColumnsExistAsync(request.ClientId, request.BridgeTable, cregLinkCol);

                await EnsureRule14IndexesAsync(request.ClientId, request.StudTable, request.BridgeTable, crseApprovedCol, crseLinkCol, cregLinkCol);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var crseTable = request.StudTable;
                var cregTable = request.BridgeTable;

                var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{crseTable}\";");
                var bridgeCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{cregTable}\";");

                await using var command = connection.CreateCommand();
                command.CommandText = BuildVerifyCountSql(schema, crseTable, cregTable, crseApprovedCol, crseLinkCol, cregLinkCol);
                await using var reader = await command.ExecuteReaderAsync();

                var result = new Rule14VerifyResult
                {
                    Success = true,
                    StudRecordCount = studCount,
                    BridgeRecordCount = bridgeCount,
                    CrseRecordCount = bridgeCount
                };

                if (await reader.ReadAsync())
                {
                    result.ApprovedQualificationCount = GetInt(reader, 0);
                    result.ApprovedCredentialCount = GetInt(reader, 1);
                    result.RegisteredCredentialCount = GetInt(reader, 2);
                    result.Control1PopulationCount = result.ApprovedCredentialCount;
                }

                return result;
            }
            catch (Exception ex)
            {
                return new Rule14VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule14ValidationSummary> RunValidationAsync(Rule14ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable);
                var crseApprovedCol = string.IsNullOrWhiteSpace(request.CrseApprovedCol) ? "_031" : request.CrseApprovedCol;
                var crseLinkCol = string.IsNullOrWhiteSpace(request.CrseLinkCol) ? "_030" : request.CrseLinkCol;
                var cregLinkCol = string.IsNullOrWhiteSpace(request.CregLinkCol) ? "_030" : request.CregLinkCol;

                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, crseLinkCol, crseApprovedCol);
                await ValidateColumnsExistAsync(request.ClientId, request.BridgeTable, cregLinkCol);

                var browserSummary = await AnalyseAsync(request, includeAllReviewRows: true);
                if (browserSummary.Success && request.ClientId > 0)
                {
                    try
                    {
                        var summaryToPersist = CloneSummary(browserSummary);
                        summaryToPersist.SavedRunId = null;
                        browserSummary.SavedRunId = await SaveValidationRunAsync(request, summaryToPersist, userEmail, userName);

                        if (!string.IsNullOrWhiteSpace(userEmail))
                            _pendingValidationCache.ClearPending(14, request.ClientId, userEmail!);
                    }
                    catch (Exception ex)
                    {
                        browserSummary.Warning = $"Analysis completed, but the workspace could not be saved automatically: {ex.Message}";
                    }
                }

                if (!browserSummary.SavedRunId.HasValue)
                {
                    if (browserSummary.Success && request.ClientId > 0 && !string.IsNullOrWhiteSpace(userEmail))
                        _pendingValidationCache.StorePending(14, request.ClientId, userEmail!, request, CloneSummary(browserSummary), userName);

                    browserSummary.Warning = string.IsNullOrWhiteSpace(browserSummary.Warning)
                        ? "Counts reflect the full approved-course result set. Browser review rows are limited for performance."
                        : browserSummary.Warning;
                }
                else
                {
                    browserSummary.Warning = "The current Rule 14 run has been written to the system database. Click Save Workspace to finalize it for signoff.";
                }

                ApplyBrowserPreview(browserSummary);
                return browserSummary;
            }
            catch (Exception ex)
            {
                return new Rule14ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule14ValidationSummary> GetExportSummaryAsync(Rule14ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.BridgeTable);
            return await AnalyseAsync(request, includeAllReviewRows: true);
        }

        public Task<Rule14ValidationSummary?> GetPendingValidationPreviewAsync(int clientId, string reviewerEmail)
        {
            var pending = _pendingValidationCache.GetPending<Rule14ValidationRequest, Rule14ValidationSummary>(14, clientId, reviewerEmail);
            if (pending == null)
                return Task.FromResult<Rule14ValidationSummary?>(null);

            var preview = CloneSummary(pending.Summary);
            preview.SavedRunId = null;
            preview.Warning = "This Rule 14 validation is still pending. Click Save Workspace to write it to the system database.";
            ApplyBrowserPreview(preview);
            return Task.FromResult<Rule14ValidationSummary?>(preview);
        }

        public Task<bool> HasPendingValidationAsync(int clientId, string reviewerEmail)
            => Task.FromResult(_pendingValidationCache.HasPending(14, clientId, reviewerEmail));

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule14WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 14);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (deserializedSummary != null && includeSummary)
            {
                deserializedSummary = await ExpandAndPersistSavedSummaryIfNeededAsync(row.RunId, deserializedSummary, clientId);
                ApplyBrowserPreview(deserializedSummary);
            }
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule14WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = row.StudTable,
                BridgeTable = row.DeceasedTable,
                CrseTable = row.DeceasedTable,
                CrseApprovedCol = deserializedSummary?.CrseApprovedCol ?? "_031",
                CrseApprovedVal = deserializedSummary?.CrseApprovedVal ?? "A",
                CrseLinkCol = deserializedSummary?.CrseLinkCol ?? "_030",
                CregLinkCol = deserializedSummary?.CregLinkCol ?? "_030",
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (summary != null)
                workspace.CurrentStatus = summary.Status;

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

        public async Task<Rule14RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 14);
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

            var review = new Rule14RunReviewViewModel
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

        public async Task<Rule14WorkspaceSaveResult> SaveWorkspaceAsync(Rule14ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable);

                if (request.RunId.HasValue && request.RunId.Value > 0)
                {
                    var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                    if (!clientId.HasValue || clientId.Value != request.ClientId)
                        return new Rule14WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                    await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                    var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                    await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                    {
                        RunId = request.RunId.Value,
                        ClientId = request.ClientId,
                        StudTable = request.StudTable,
                        DeceasedTable = request.BridgeTable,
                        StudColumn = "",
                        DeceasedColumn = ""
                    }, reviewerName ?? reviewerEmail);

                    if (!string.IsNullOrWhiteSpace(reviewerEmail))
                        _pendingValidationCache.ClearPending(14, request.ClientId, reviewerEmail);

                    var currentWorkspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                    return new Rule14WorkspaceSaveResult
                    {
                        Success = true,
                        Message = clearedSignoffs > 0
                            ? "Workspace saved. Existing signoffs were removed and the run must be reviewed again."
                            : "Workspace saved and marked for review again.",
                        SignoffsCleared = clearedSignoffs > 0,
                        ClearedSignoffCount = clearedSignoffs,
                        Workspace = currentWorkspace
                    };
                }

                var pending = _pendingValidationCache.GetPending<Rule14ValidationRequest, Rule14ValidationSummary>(14, request.ClientId, reviewerEmail);
                if (pending == null)
                    return new Rule14WorkspaceSaveResult { Success = false, Error = "Run Rule 14 first so the current workspace is written to the system database." };

                if (!RequestsMatchForPendingSave(request, pending.Request))
                    return new Rule14WorkspaceSaveResult { Success = false, Error = "Workspace settings changed after validation. Run Rule 14 again before saving." };

                var summaryToSave = CloneSummary(pending.Summary);
                if (summaryToSave.IsPreviewOnly || summaryToSave.ReviewRows.Count < summaryToSave.TotalValidated)
                    summaryToSave = await AnalyseAsync(pending.Request, includeAllReviewRows: true);

                summaryToSave.SavedRunId = null;
                var savedRunId = await SaveValidationRunAsync(pending.Request, summaryToSave, reviewerEmail, reviewerName);
                _pendingValidationCache.ClearPending(14, request.ClientId, reviewerEmail);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = savedRunId,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.BridgeTable,
                    StudColumn = "",
                    DeceasedColumn = ""
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule14WorkspaceSaveResult
                {
                    Success = true,
                    Message = $"Workspace saved as Run #{savedRunId}. Sign off this saved workspace when you are ready.",
                    SignoffsCleared = false,
                    ClearedSignoffCount = 0,
                    Workspace = workspace
                };
            }
            catch (Exception ex)
            {
                return new Rule14WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule14WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule14WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                if (!string.IsNullOrWhiteSpace(reviewerEmail))
                    _pendingValidationCache.ClearPending(14, clientId.Value, reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule14WorkspaceSaveResult
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
                return new Rule14WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 14 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 14 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 14 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public Task<string> GenerateSqlAsync(Rule14ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.BridgeTable);

            var crseTable = request.StudTable;
            var cregTable = request.BridgeTable;
            var aCol = string.IsNullOrWhiteSpace(request.CrseApprovedCol) ? "_031" : request.CrseApprovedCol;
            var aVal = request.CrseApprovedVal ?? "A";
            var lCol = string.IsNullOrWhiteSpace(request.CrseLinkCol) ? "_030" : request.CrseLinkCol;
            var rCol = string.IsNullOrWhiteSpace(request.CregLinkCol) ? "_030" : request.CregLinkCol;

            var sql = $@"-- HEMIS RULE 14: COURSE REGISTRATION VALIDATION - 100% POPULATION
-- Source: this engagement's own uploaded tables, not a live SQL Server.
-- Rule:
--   Select 100% of approved courses where ""{crseTable}"".""{aCol}"" = '{aVal}'
--   Match CRSE.""{lCol}"" to CREG.""{rCol}""
--   PASS = approved course exists in ""{cregTable}""
--   FAIL = approved course does not exist in ""{cregTable}""

{BuildRule14PrepSql("{schema}", crseTable, cregTable, aCol, aVal, lCol, rCol, true, true, true, true, true, true, true, true, true, true)}

-- Full extracted population result
SELECT * FROM rule14_result ORDER BY course_code;

-- Summary
SELECT
    COUNT(*) AS total_approved_courses,
    SUM(CASE WHEN validation_result = 'PASS' THEN 1 ELSE 0 END) AS pass_count,
    SUM(CASE WHEN validation_result = 'FAIL' THEN 1 ELSE 0 END) AS fail_count,
    ROUND(SUM(CASE WHEN validation_result = 'FAIL' THEN 1 ELSE 0 END) * 100.0
         / NULLIF(COUNT(*), 0), 2) AS exception_rate_pct
FROM rule14_result;";

            return Task.FromResult(sql.Trim());
        }

        private async Task<Rule14ValidationSummary> AnalyseAsync(Rule14ValidationRequest request, bool includeAllReviewRows)
        {
            var crseApprovedCol = string.IsNullOrWhiteSpace(request.CrseApprovedCol) ? "_031" : request.CrseApprovedCol;
            var crseApprovedVal = request.CrseApprovedVal ?? "A";
            var crseLinkCol = string.IsNullOrWhiteSpace(request.CrseLinkCol) ? "_030" : request.CrseLinkCol;
            var cregLinkCol = string.IsNullOrWhiteSpace(request.CregLinkCol) ? "_030" : request.CregLinkCol;

            await EnsureRule14IndexesAsync(request.ClientId, request.StudTable, request.BridgeTable, crseApprovedCol, crseLinkCol, cregLinkCol);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var crseTable = request.StudTable;
            var cregTable = request.BridgeTable;

            var crseCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{crseTable}\";");
            var cregCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{cregTable}\";");

            // These optional display columns don't always exist on an analyst's uploaded CRSE
            // table — degrade gracefully to NULL rather than failing the whole validation.
            var crseColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(request.ClientId, crseTable), StringComparer.OrdinalIgnoreCase);
            bool has(string col) => crseColumns.Contains(col);

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule14PrepSql(schema, crseTable, cregTable, crseApprovedCol, crseApprovedVal, crseLinkCol, cregLinkCol,
                    has("_065"), has("_033"), has("_034"), has("_059"), has("_060"), has("_061"), has("_062"), has("_058"), has("_091"), has("_092") && has("_093"));
                await prepCommand.ExecuteNonQueryAsync();
            }

            int approvedCourseCount, registeredCourseCount, missingRegistrationCount;
            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = BuildPopulationCountSql();
                await using var countReader = await countCommand.ExecuteReaderAsync();
                approvedCourseCount = registeredCourseCount = missingRegistrationCount = 0;
                if (await countReader.ReadAsync())
                {
                    approvedCourseCount = GetInt(countReader, 0);
                    registeredCourseCount = GetInt(countReader, 1);
                    missingRegistrationCount = GetInt(countReader, 2);
                }
            }

            var reviewRows = await LoadControlRowsAsync(connection, includeAllReviewRows ? (int?)null : BrowserPreviewRowLimit);
            reviewRows = NormalizeReviewRows(reviewRows);

            var controlSummaries = BuildControlSummaries(approvedCourseCount, registeredCourseCount, crseApprovedCol, crseApprovedVal, crseLinkCol, cregLinkCol);
            var totalValidated = controlSummaries.Sum(x => x.TotalCount);
            var passCount = controlSummaries.Sum(x => x.PassCount);
            var failCount = controlSummaries.Sum(x => x.FailCount);
            var isPreviewOnly = !includeAllReviewRows && totalValidated > reviewRows.Count;

            return new Rule14ValidationSummary
            {
                Success = true,
                StudRecordCount = crseCount,
                CrseRecordCount = cregCount,
                BridgeRecordCount = cregCount,
                ApprovedQualificationCount = approvedCourseCount,
                ApprovedCredentialCount = approvedCourseCount,
                RegisteredCredentialCount = registeredCourseCount,
                UnfulfilledPopulationCount = approvedCourseCount,
                TotalRequested = totalValidated,
                TotalValidated = totalValidated,
                DisplayedCount = reviewRows.Count,
                IsPreviewOnly = isPreviewOnly,
                PreviewLimit = isPreviewOnly ? BrowserPreviewRowLimit : 0,
                PassCount = passCount,
                FailCount = failCount,
                ExceptionRate = totalValidated == 0 ? 0m : Math.Round(failCount * 100m / totalValidated, 2),
                Status = failCount == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable = request.StudTable,
                BridgeTable = request.BridgeTable,
                CrseTable = request.BridgeTable,
                CrseApprovedCol = crseApprovedCol,
                CrseApprovedVal = crseApprovedVal,
                CrseLinkCol = crseLinkCol,
                CregLinkCol = cregLinkCol,
                TableLinkageText = $"{request.StudTable} -> {request.BridgeTable}",
                RuleModeText = "100% population testing of approved courses",
                ProcedureSteps = BuildProcedureSteps(request.StudTable, request.BridgeTable),
                ClientId = request.ClientId,
                ControlSummaries = controlSummaries,
                ReviewRows = reviewRows,
                Warning = includeAllReviewRows
                    ? "Rule 14 completed with the full approved-course result set."
                    : "Counts reflect the full approved-course result set. Browser review rows are limited for performance."
            };
        }

        private async Task<int> SaveValidationRunAsync(Rule14ValidationRequest request, Rule14ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 14);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 14,
                RuleName = "Course Registration Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.BridgeTable,
                StudColumn = "",
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)))),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            summary.SavedRunId = runId;
            return runId;
        }

        private static string BuildVerifyCountSql(string schema, string crseTable, string cregTable, string crseApprovedCol, string crseLinkCol, string cregLinkCol) => $@"
WITH approved AS (
    SELECT DISTINCT CAST(crse.""{crseLinkCol}"" AS text) AS course_code
    FROM ""{schema}"".""{crseTable}"" crse
    WHERE crse.""{crseLinkCol}"" IS NOT NULL
)
SELECT
    (SELECT COUNT(*) FROM approved) AS approved_qualification_count,
    (SELECT COUNT(*) FROM ""{schema}"".""{cregTable}"") AS approved_credential_count,
    (SELECT COUNT(*) FROM ""{schema}"".""{cregTable}"") AS registered_credential_count;";

        private static string BuildPopulationCountSql() => @"
SELECT
    COUNT(1) AS approved_course_count,
    SUM(CASE WHEN validation_result = 'PASS' THEN 1 ELSE 0 END) AS registered_course_count,
    SUM(CASE WHEN validation_result = 'FAIL' THEN 1 ELSE 0 END) AS missing_registration_count
FROM rule14_result;";

        private static readonly Dictionary<string, string> DisplayNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sample_number"] = "Sample_Number",
            ["control_type"] = "Control_Type",
            ["control_label"] = "Control_Label",
            ["course_code"] = "Course_Code",
            ["course_name"] = "Course_Name",
            ["course_approval_status"] = "Course_Approval_Status",
            ["course_cesm"] = "Course_CESM",
            ["course_level_code"] = "Course_Level_Code",
            ["contact_only_availability"] = "Contact_Only_Availability",
            ["distance_only_availability"] = "Distance_Only_Availability",
            ["mixed_mode_availability"] = "Mixed_Mode_Availability",
            ["experiential_training_indicator"] = "Experiential_Training_Indicator",
            ["foundation_course"] = "Foundation_Course",
            ["nqf_level"] = "NQF_Level",
            ["nqf_credit"] = "NQF_Credit",
            ["creg_course_code"] = "CREG_Course_Code",
            ["validation_result"] = "Validation_Result",
            ["validation_reason"] = "Validation_Reason"
        };

        private static string ToPascalDisplayName(string columnName) =>
            DisplayNameMap.TryGetValue(columnName, out var mapped) ? mapped : columnName;

        // Uploaded engagement tables have no indexes beyond their primary key. The CREG dedup
        // subquery below scans/dedups the full CREG table on every run (real engagements have
        // seen 450k+ row CREG tables) — building the expression index once, up front, makes
        // every run after the first fast.
        private async Task EnsureRule14IndexesAsync(int clientId, string crseTable, string cregTable, string crseApprovedCol, string crseLinkCol, string cregLinkCol)
        {
            await _datasets.EnsureJoinIndexAsync(clientId, crseTable, crseApprovedCol);
            await _datasets.EnsureJoinIndexAsync(clientId, crseTable, crseLinkCol);
            await _datasets.EnsureJoinIndexAsync(clientId, cregTable, cregLinkCol);
        }

        private async Task<List<Rule14ValidationRowRecord>> LoadControlRowsAsync(NpgsqlConnection connection, int? maxRows)
        {
            var limitClause = maxRows.HasValue && maxRows.Value > 0 ? $"LIMIT {maxRows.Value}" : "";
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM rule14_result ORDER BY course_code {limitClause};";

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule14ValidationRowRecord>();
            while (await reader.ReadAsync())
            {
                var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var columnName = ToPascalDisplayName(reader.GetName(i));
                    displayValues[columnName] = reader.IsDBNull(i)
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }

                rows.Add(new Rule14ValidationRowRecord
                {
                    ValidationNumber = rows.Count + 1,
                    ControlType = ReadValue(displayValues, "Control_Type"),
                    ControlLabel = ReadValue(displayValues, "Control_Label"),
                    ValidationResult = ReadValue(displayValues, "Validation_Result"),
                    ValidationExplanation = ReadValue(displayValues, "Validation_Reason"),
                    DisplayValues = displayValues
                });

                EnrichRule14DisplayValues(rows[^1]);
            }

            return rows;
        }

        private static string BuildRule14PrepSql(
            string schema, string crseTable, string cregTable,
            string crseApprovedCol, string crseApprovedVal, string crseLinkCol, string cregLinkCol,
            bool hasFiller, bool hasCesm, bool hasLevelCode, bool hasContactOnly, bool hasDistanceOnly,
            bool hasMixedMode, bool hasExperiential, bool hasCourseName, bool hasFoundation, bool hasNqf)
        {
            var av = EscapeSqlString(crseApprovedVal);
            string col(bool has, string name) => has ? $@"CAST(crse.""{name}"" AS text)" : "NULL::text";

            return $@"
DROP TABLE IF EXISTS rule14_approved_courses;
DROP TABLE IF EXISTS rule14_result;

CREATE TEMP TABLE rule14_approved_courses AS
SELECT DISTINCT
    CAST(crse.""{crseLinkCol}""    AS text) AS course_code,
    {col(hasFiller, "_065")} AS filler1,
    CAST(crse.""{crseApprovedCol}"" AS text) AS course_approval_status,
    {col(hasCesm, "_033")} AS course_cesm,
    {col(hasLevelCode, "_034")} AS course_level_code,
    {col(hasContactOnly, "_059")} AS contact_only_availability,
    {col(hasDistanceOnly, "_060")} AS distance_only_availability,
    {col(hasMixedMode, "_061")} AS mixed_mode_availability,
    {col(hasExperiential, "_062")} AS experiential_training_indicator,
    {col(hasCourseName, "_058")} AS course_name,
    {col(hasFoundation, "_091")} AS foundation_course,
    {(hasNqf ? @"CAST(crse.""_092"" AS text)" : "NULL::text")} AS nqf_level,
    {(hasNqf ? @"CAST(crse.""_093"" AS text)" : "NULL::text")} AS nqf_credit
FROM ""{schema}"".""{crseTable}"" crse
WHERE UPPER(TRIM(CAST(crse.""{crseApprovedCol}"" AS text))) = '{av}'
  AND crse.""{crseLinkCol}"" IS NOT NULL;

ANALYZE rule14_approved_courses;

CREATE TEMP TABLE rule14_result AS
SELECT
    ROW_NUMBER() OVER (ORDER BY a.course_code) AS sample_number,
    'Control_1' AS control_type,
    'CONTROL 1: CRSE.{crseApprovedCol} = {crseApprovedVal} and CREG.{cregLinkCol} = CRSE.{crseLinkCol}' AS control_label,
    a.course_code,
    a.course_name,
    a.course_approval_status,
    a.course_cesm,
    a.course_level_code,
    a.contact_only_availability,
    a.distance_only_availability,
    a.mixed_mode_availability,
    a.experiential_training_indicator,
    a.foundation_course,
    a.nqf_level,
    a.nqf_credit,
    creg.course_code AS creg_course_code,
    CASE WHEN creg.course_code IS NOT NULL THEN 'PASS' ELSE 'FAIL' END AS validation_result,
    CASE
        WHEN creg.course_code IS NOT NULL THEN 'Approved course found in {cregTable}.'
        ELSE 'Approved course not found in {cregTable}.'
    END AS validation_reason
FROM rule14_approved_courses a
LEFT JOIN (
    -- CREG typically has one row per student registration, so many rows can share the
    -- same course code. Dedup to distinct course codes before joining, otherwise a
    -- popular course fans out into one PASS result row per registration instead of one.
    SELECT DISTINCT UPPER(TRIM(CAST(""{cregLinkCol}"" AS text))) AS join_key,
                     CAST(""{cregLinkCol}"" AS text) AS course_code
    FROM ""{schema}"".""{cregTable}""
    WHERE ""{cregLinkCol}"" IS NOT NULL
) creg
    ON UPPER(TRIM(CAST(a.course_code AS text))) = creg.join_key;

ANALYZE rule14_result;";
        }

        private static List<Rule14ControlSummaryItemViewModel> BuildControlSummaries(
            int approvedCourseCount, int registeredCourseCount,
            string crseApprovedCol, string crseApprovedVal, string crseLinkCol, string cregLinkCol)
        {
            return new List<Rule14ControlSummaryItemViewModel>
            {
                BuildControlSummary(
                    "Control_1",
                    "Control 1",
                    $"CRSE.{crseApprovedCol}='{crseApprovedVal}' AND CREG.{cregLinkCol}=CRSE.{crseLinkCol}",
                    approvedCourseCount,
                    registeredCourseCount)
            };
        }

        private static Rule14ControlSummaryItemViewModel BuildControlSummary(string controlType, string controlLabel, string criteriaText, int totalCount, int passCount)
        {
            var failCount = Math.Max(totalCount - passCount, 0);
            return new Rule14ControlSummaryItemViewModel
            {
                ControlType = controlType,
                ControlLabel = controlLabel,
                CriteriaText = criteriaText,
                RequestedCount = totalCount,
                AvailableCount = totalCount,
                AchievedCount = totalCount,
                TotalCount = totalCount,
                PassCount = passCount,
                FailCount = failCount,
                Status = failCount == 0 ? "PASS" : "FAIL"
            };
        }

        private static List<Rule14ValidationRowRecord> NormalizeReviewRows(IEnumerable<Rule14ValidationRowRecord>? rows) =>
            (rows ?? Enumerable.Empty<Rule14ValidationRowRecord>())
                .Select((row, index) => { row.ValidationNumber = index + 1; return row; })
                .ToList();

        private async Task<Rule14ValidationSummary> ExpandAndPersistSavedSummaryIfNeededAsync(int runId, Rule14ValidationSummary summary, int clientId)
        {
            var looksLikeStoredPreviewSample =
                summary.ReviewRows.Count > 0 &&
                summary.ReviewRows.Count <= BrowserPreviewRowLimit &&
                summary.TotalValidated > 0;

            if (!summary.IsPreviewOnly && summary.ReviewRows.Count >= summary.TotalValidated && !looksLikeStoredPreviewSample)
                return summary;

            if (string.IsNullOrWhiteSpace(summary.StudTable) || string.IsNullOrWhiteSpace(summary.BridgeTable))
                return summary;

            try
            {
                var expanded = await AnalyseAsync(new Rule14ValidationRequest
                {
                    ClientId = clientId,
                    RunId = summary.SavedRunId,
                    StudTable = summary.StudTable,
                    BridgeTable = summary.BridgeTable,
                    CrseApprovedCol = summary.CrseApprovedCol,
                    CrseApprovedVal = summary.CrseApprovedVal,
                    CrseLinkCol = summary.CrseLinkCol,
                    CregLinkCol = summary.CregLinkCol
                }, includeAllReviewRows: true);

                expanded.Timestamp = string.IsNullOrWhiteSpace(summary.Timestamp) ? expanded.Timestamp : summary.Timestamp;
                expanded.ClientId = summary.ClientId;
                expanded.SavedRunId = summary.SavedRunId;
                expanded.Warning = string.IsNullOrWhiteSpace(summary.Warning)
                    ? "Saved Rule 14 results were expanded from the stored browser preview to the full result set."
                    : $"{summary.Warning} Full saved results were reloaded from the saved Rule 14 configuration.";

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

        private async Task UpdateStoredSummaryAsync(int runId, Rule14ValidationSummary summary)
        {
            await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = summary.ClientId,
                RuleNumber = 14,
                RuleName = "Course Registration Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = summary.StudTable,
                DeceasedTable = summary.BridgeTable,
                StudColumn = "",
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)))),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, null, null);
        }

        private static Rule14ValidationSummary CloneSummary(Rule14ValidationSummary summary) => new()
        {
            Success = summary.Success,
            StudRecordCount = summary.StudRecordCount,
            BridgeRecordCount = summary.BridgeRecordCount,
            CrseRecordCount = summary.CrseRecordCount,
            UnfulfilledPopulationCount = summary.UnfulfilledPopulationCount,
            ApprovedCredentialCount = summary.ApprovedCredentialCount,
            RegisteredCredentialCount = summary.RegisteredCredentialCount,
            TotalRequested = summary.TotalRequested,
            TotalValidated = summary.TotalValidated,
            DisplayedCount = summary.DisplayedCount,
            IsPreviewOnly = summary.IsPreviewOnly,
            PreviewLimit = summary.PreviewLimit,
            PassCount = summary.PassCount,
            FailCount = summary.FailCount,
            ExceptionRate = summary.ExceptionRate,
            Status = summary.Status,
            Timestamp = summary.Timestamp,
            StudTable = summary.StudTable,
            BridgeTable = summary.BridgeTable,
            CrseTable = summary.CrseTable,
            CrseApprovedCol = summary.CrseApprovedCol,
            CrseApprovedVal = summary.CrseApprovedVal,
            CrseLinkCol = summary.CrseLinkCol,
            CregLinkCol = summary.CregLinkCol,
            TableLinkageText = summary.TableLinkageText,
            RuleModeText = summary.RuleModeText,
            ProcedureSteps = summary.ProcedureSteps.ToList(),
            ClientId = summary.ClientId,
            SavedRunId = summary.SavedRunId,
            ControlSummaries = summary.ControlSummaries.Select(item => new Rule14ControlSummaryItemViewModel
            {
                ControlType = item.ControlType,
                ControlLabel = item.ControlLabel,
                CriteriaText = item.CriteriaText,
                RequestedCount = item.RequestedCount,
                AvailableCount = item.AvailableCount,
                AchievedCount = item.AchievedCount,
                TotalCount = item.TotalCount,
                PassCount = item.PassCount,
                FailCount = item.FailCount,
                Status = item.Status
            }).ToList(),
            ReviewRows = summary.ReviewRows.Select(CloneReviewRow).ToList(),
            Warning = summary.Warning,
            Error = summary.Error
        };

        private static Rule14ValidationRowRecord CloneReviewRow(Rule14ValidationRowRecord row) => new()
        {
            ValidationNumber = row.ValidationNumber,
            ControlType = row.ControlType,
            ControlLabel = row.ControlLabel,
            ValidationResult = row.ValidationResult,
            ValidationExplanation = row.ValidationExplanation,
            DisplayValues = new Dictionary<string, string?>(row.DisplayValues, StringComparer.OrdinalIgnoreCase)
        };

        private static Rule14ValidationSummary CreateBrowserPreview(Rule14ValidationSummary summary)
        {
            var previewRows = summary.ReviewRows.OrderBy(row => row.ValidationNumber).Take(BrowserPreviewRowLimit).ToList();
            var clone = CloneSummary(summary);
            clone.DisplayedCount = previewRows.Count;
            clone.IsPreviewOnly = summary.TotalValidated > previewRows.Count;
            clone.PreviewLimit = summary.TotalValidated > previewRows.Count ? previewRows.Count : 0;
            clone.ReviewRows = previewRows;
            return clone;
        }

        private static void ApplyBrowserPreview(Rule14ValidationSummary summary)
        {
            var preview = CreateBrowserPreview(summary);
            summary.DisplayedCount = preview.DisplayedCount;
            summary.IsPreviewOnly = preview.IsPreviewOnly;
            summary.PreviewLimit = preview.PreviewLimit;
            summary.ReviewRows = preview.ReviewRows;
        }

        private static List<string> BuildProcedureSteps(string crseTable, string cregTable) => new()
        {
            $"Filter {crseTable} to approved courses where the configured approval column matches the configured value.",
            $"Keep the distinct approved course codes from {crseTable}.",
            $"For each approved course code, test whether a matching {cregTable} row exists on the configured link columns.",
            "Mark rows PASS when a matching registration exists and FAIL when no registration exists.",
            "Return the full approved-course population; no sampling is applied."
        };

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

        private static void ValidateRequest(string studTable, string bridgeTable)
        {
            if (string.IsNullOrWhiteSpace(studTable))
                throw new InvalidOperationException("CRSE table is required.");
            if (string.IsNullOrWhiteSpace(bridgeTable))
                throw new InvalidOperationException("CREG table is required.");
        }

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

        private static void EnrichRule14DisplayValues(Rule14ValidationRowRecord row)
        {
            var values = row.DisplayValues;
            var validationResult = ReadValue(values, "Validation_Result");
            var isPass = string.Equals(validationResult, "PASS", StringComparison.OrdinalIgnoreCase);
            var courseCode = FormatRule14ColumnValue(ReadValue(values, "Course_Code"));
            var courseStatus = FormatRule14ColumnValue(ReadValue(values, "Course_Approval_Status"));
            var cregCode = FormatRule14ColumnValue(ReadValue(values, "CREG_Course_Code"));
            var controlLabel = ReadValue(values, "Control_Label");

            var validationExplanation = isPass
                ? $"CRSE approval: FOUND ('{courseStatus}') | CREG registration: FOUND (matched on '{cregCode}')"
                : $"CRSE approval: FOUND ('{courseStatus}') | CREG registration: NOT FOUND (no match for course '{courseCode}')";
            var registrationMessage = isPass
                ? $"Matched registration: CREG code='{cregCode}'."
                : $"No matching CREG registration row found for course code '{courseCode}'.";

            // Keys the Razor view reads directly from DisplayValues
            values["CRSE__030"] = ReadValue(values, "Course_Code");
            values["CRSE__031"] = ReadValue(values, "Course_Approval_Status");
            values["CREG__030"] = ReadValue(values, "CREG_Course_Code");
            values["CRSE_CRITERIA_MESSAGE"] = $"_031 = '{courseStatus}'";

            values["FINAL_RULE_TEXT"] = controlLabel;
            values["Validation_Reason"] = ReadValue(values, "Validation_Reason").Length > 0
                ? ReadValue(values, "Validation_Reason")
                : validationExplanation;
            values["Validation_Explanation"] = validationExplanation;
            values["CREG_LINK_MESSAGE"] = registrationMessage;
            values["FINAL_RESULT_MESSAGE"] = isPass
                ? "Passed: approved course found in CREG."
                : "Failed: approved course not found in CREG.";
            row.ValidationExplanation = validationExplanation;
        }

        private static string FormatRule14ColumnValue(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "[blank]" : value.Trim();

        private static bool RequestsMatchForPendingSave(Rule14ValidationRequest current, Rule14ValidationRequest pending) =>
            current.ClientId == pending.ClientId &&
            string.Equals(current.StudTable?.Trim(), pending.StudTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.BridgeTable?.Trim(), pending.BridgeTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CrseApprovedCol?.Trim(), pending.CrseApprovedCol?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CrseApprovedVal?.Trim(), pending.CrseApprovedVal?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CrseLinkCol?.Trim(), pending.CrseLinkCol?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CregLinkCol?.Trim(), pending.CregLinkCol?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static int GetInt(System.Data.Common.DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

        private static string ReadValue(IReadOnlyDictionary<string, string?> values, string key) =>
            values.TryGetValue(key, out var value) ? value ?? "" : "";

        private static string EscapeSqlString(string? value) => (value ?? "").Replace("'", "''");

        private static Rule14ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                if (string.IsNullOrWhiteSpace(decoded)) return null;
                return JsonConvert.DeserializeObject<Rule14ValidationSummary>(decoded);
            }
            catch { return null; }
        }
    }
}
