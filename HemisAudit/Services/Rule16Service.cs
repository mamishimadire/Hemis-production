using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 16: validates against the engagement's own uploaded Supabase data instead of a live
    // SQL Server connection, and saves through the shared Postgres-native persistence layer.
    // Ported from the Rule11/Rule17 pattern. Data source is 3 uploaded tables (STUD, bridge
    // [CREG or CRED], CRSE) joined and filtered into three control populations; the "unfulfilled
    // qualification" population is tested 100% (no sampling), same as the original.
    public class Rule16Service : IRule16Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IPendingValidationCacheService _pendingValidationCache;

        public Rule16Service(
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

        public async Task<Rule16TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule16TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule16TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "dbo_stud", "STUD", "stud"], ["stud"]),
                    AutoBridgeTable = FindFirst(tables, ["dbo_CREG", "dbo_creg", "CREG", "creg", "dbo_CRED", "dbo_cred", "CRED", "cred"], ["creg", "cred"]),
                    AutoCrseTable = FindFirst(tables, ["dbo_CRSE", "dbo_crse", "CRSE", "crse"], ["crse"])
                };
            }
            catch (Exception ex)
            {
                return new Rule16TableDiscoveryResult { Success = false, Error = ex.Message };
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

        public async Task<Rule16VerifyResult> VerifyTablesAsync(Rule16VerifyRequest request)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);
                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, "_001", "_007", "_025", "_024");
                await ValidateColumnsExistAsync(request.ClientId, request.BridgeTable, "_001", "_007", "_030");
                await ValidateColumnsExistAsync(request.ClientId, request.CrseTable, "_030", "_091");

                await EnsureRule16IndexesAsync(request.ClientId, request.StudTable, request.BridgeTable, request.CrseTable);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var studTable = request.StudTable;
                var bridgeTable = request.BridgeTable;
                var crseTable = request.CrseTable;

                var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{studTable}\";");
                var bridgeCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{bridgeTable}\";");
                var crseCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{crseTable}\";");

                await using var command = connection.CreateCommand();
                command.CommandText = BuildPopulationCountSql(schema, studTable, bridgeTable, crseTable);
                await using var reader = await command.ExecuteReaderAsync();

                var result = new Rule16VerifyResult
                {
                    Success = true,
                    StudRecordCount = studCount,
                    BridgeRecordCount = bridgeCount,
                    CrseRecordCount = crseCount
                };

                if (await reader.ReadAsync())
                {
                    result.UnfulfilledPopulationCount = GetInt(reader, 0);
                    result.Control1PopulationCount = GetInt(reader, 0);
                    result.Control2PopulationCount = GetInt(reader, 1);
                    result.Control3PopulationCount = GetInt(reader, 2);
                }

                return result;
            }
            catch (Exception ex)
            {
                return new Rule16VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule16ValidationSummary> RunValidationAsync(Rule16ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);
                var unfulfilledCol = string.IsNullOrWhiteSpace(request.UnfulfilledCol) ? "_025" : request.UnfulfilledCol;
                var foundationCol = string.IsNullOrWhiteSpace(request.FoundationCol) ? "_091" : request.FoundationCol;
                var distanceCol = string.IsNullOrWhiteSpace(request.DistanceCol) ? "_024" : request.DistanceCol;
                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, "_001", "_007", unfulfilledCol, distanceCol);
                await ValidateColumnsExistAsync(request.ClientId, request.BridgeTable, "_001", "_007", "_030");
                await ValidateColumnsExistAsync(request.ClientId, request.CrseTable, "_030", foundationCol);

                var summary = await AnalyseAsync(request, includeAllReviewRows: false);
                if (summary.Success && request.ClientId > 0)
                {
                    try
                    {
                        var summaryToPersist = CloneSummary(summary);
                        if (summaryToPersist.IsPreviewOnly || summaryToPersist.ReviewRows.Count < summaryToPersist.TotalValidated)
                            summaryToPersist = await AnalyseAsync(request, includeAllReviewRows: true);

                        summaryToPersist.SavedRunId = null;
                        summary.SavedRunId = await SaveValidationRunAsync(request, summaryToPersist, userEmail, userName);

                        if (!string.IsNullOrWhiteSpace(userEmail))
                            _pendingValidationCache.ClearPending(16, request.ClientId, userEmail!);
                    }
                    catch (Exception ex)
                    {
                        summary.Warning = $"Analysis completed, but the workspace could not be saved automatically: {ex.Message}";
                    }
                }

                if (!summary.SavedRunId.HasValue)
                {
                    if (summary.Success && request.ClientId > 0 && !string.IsNullOrWhiteSpace(userEmail))
                        _pendingValidationCache.StorePending(16, request.ClientId, userEmail!, request, CloneSummary(summary), userName);

                    summary.Warning = string.IsNullOrWhiteSpace(summary.Warning)
                        ? "Rule 16 validation completed. Click Save Workspace to write this validated result to the system database."
                        : summary.Warning;
                }
                else
                {
                    summary.Warning = "The current Rule 16 run has been written to the system database. Click Save Workspace to finalize it for signoff.";
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule16ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule16ValidationSummary> GetExportSummaryAsync(Rule16ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);
            return await AnalyseAsync(request, includeAllReviewRows: true);
        }

        public Task<Rule16ValidationSummary?> GetPendingValidationPreviewAsync(int clientId, string reviewerEmail)
        {
            var pending = _pendingValidationCache.GetPending<Rule16ValidationRequest, Rule16ValidationSummary>(16, clientId, reviewerEmail);
            if (pending == null)
                return Task.FromResult<Rule16ValidationSummary?>(null);

            var preview = CloneSummary(pending.Summary);
            preview.SavedRunId = null;
            preview.Warning = "This Rule 16 validation is still pending. Click Save Workspace to write it to the system database.";
            ApplyBrowserPreview(preview);
            return Task.FromResult<Rule16ValidationSummary?>(preview);
        }

        public Task<bool> HasPendingValidationAsync(int clientId, string reviewerEmail)
            => Task.FromResult(_pendingValidationCache.HasPending(16, clientId, reviewerEmail));

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule16WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 16);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (deserializedSummary != null && includeSummary)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule16WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = row.StudTable,
                BridgeTable = row.DeceasedTable,
                CrseTable = row.StudColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (deserializedSummary != null)
            {
                workspace.CurrentStatus = deserializedSummary.Status;
                workspace.StudTable = deserializedSummary.StudTable;
                workspace.BridgeTable = deserializedSummary.BridgeTable;
                workspace.CrseTable = deserializedSummary.CrseTable;
                workspace.UnfulfilledCol = string.IsNullOrWhiteSpace(deserializedSummary.UnfulfilledCol) ? "_025" : deserializedSummary.UnfulfilledCol;
                workspace.UnfulfilledVal = string.IsNullOrWhiteSpace(deserializedSummary.UnfulfilledVal) ? "N" : deserializedSummary.UnfulfilledVal;
                workspace.FoundationCol = string.IsNullOrWhiteSpace(deserializedSummary.FoundationCol) ? "_091" : deserializedSummary.FoundationCol;
                workspace.FoundationVal = string.IsNullOrWhiteSpace(deserializedSummary.FoundationVal) ? "Y" : deserializedSummary.FoundationVal;
                workspace.DistanceCol = string.IsNullOrWhiteSpace(deserializedSummary.DistanceCol) ? "_024" : deserializedSummary.DistanceCol;
                workspace.DistanceVal = string.IsNullOrWhiteSpace(deserializedSummary.DistanceVal) ? "D" : deserializedSummary.DistanceVal;
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

        public async Task<Rule16RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 16);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            if (includeFullResults)
            {
                summary = await ExpandSavedSummaryIfNeededAsync(summary, row.ClientId);
                summary.DisplayedCount = summary.ReviewRows.Count;
                summary.IsPreviewOnly = false;
                summary.PreviewLimit = 0;
            }
            else
            {
                ApplyBrowserPreview(summary);
            }

            var review = new Rule16RunReviewViewModel
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

        public async Task<Rule16WorkspaceSaveResult> SaveWorkspaceAsync(Rule16ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);

                if (request.RunId.HasValue && request.RunId.Value > 0)
                {
                    var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                    if (!clientId.HasValue || clientId.Value != request.ClientId)
                        return new Rule16WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                    await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                    var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                    await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                    {
                        RunId = request.RunId.Value,
                        ClientId = request.ClientId,
                        StudTable = request.StudTable,
                        DeceasedTable = request.BridgeTable,
                        StudColumn = request.CrseTable,
                        DeceasedColumn = ""
                    }, reviewerName ?? reviewerEmail);

                    if (!string.IsNullOrWhiteSpace(reviewerEmail))
                        _pendingValidationCache.ClearPending(16, request.ClientId, reviewerEmail);

                    var currentWorkspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                    return new Rule16WorkspaceSaveResult
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

                var pending = _pendingValidationCache.GetPending<Rule16ValidationRequest, Rule16ValidationSummary>(16, request.ClientId, reviewerEmail);
                if (pending == null)
                    return new Rule16WorkspaceSaveResult { Success = false, Error = "Run Rule 16 first so the current workspace is written to the system database." };

                if (!RequestsMatchForPendingSave(request, pending.Request))
                    return new Rule16WorkspaceSaveResult { Success = false, Error = "Workspace settings changed after validation. Run Rule 16 again before saving." };

                var summaryToSave = CloneSummary(pending.Summary);
                if (summaryToSave.IsPreviewOnly || summaryToSave.ReviewRows.Count < summaryToSave.TotalValidated)
                    summaryToSave = await AnalyseAsync(pending.Request, includeAllReviewRows: true);

                summaryToSave.SavedRunId = null;
                var savedRunId = await SaveValidationRunAsync(pending.Request, summaryToSave, reviewerEmail, reviewerName);
                _pendingValidationCache.ClearPending(16, request.ClientId, reviewerEmail);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = savedRunId,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.BridgeTable,
                    StudColumn = request.CrseTable,
                    DeceasedColumn = ""
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule16WorkspaceSaveResult
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
                return new Rule16WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule16WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule16WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                if (!string.IsNullOrWhiteSpace(reviewerEmail))
                    _pendingValidationCache.ClearPending(16, clientId.Value, reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule16WorkspaceSaveResult
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
                return new Rule16WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 16 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 16 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 16 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public Task<string> GenerateSqlAsync(Rule16ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);

            var studTable = request.StudTable;
            var bridgeTable = request.BridgeTable;
            var crseTable = request.CrseTable;
            var uc = string.IsNullOrWhiteSpace(request.UnfulfilledCol) ? "_025" : request.UnfulfilledCol;
            var uv = request.UnfulfilledVal;
            var fc = string.IsNullOrWhiteSpace(request.FoundationCol) ? "_091" : request.FoundationCol;
            var fv = request.FoundationVal;
            var dc = string.IsNullOrWhiteSpace(request.DistanceCol) ? "_024" : request.DistanceCol;
            var dv = request.DistanceVal;

            var sql = $@"-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- CONTROL 1: {uc}='{uv}' AND {fc}='{fv}'
SELECT
    'Control_1' AS control_type,
    s.""_007"", s.""_001"", s.""{uc}"", s.""{dc}"",
    bridge.""_001"", bridge.""_007"", bridge.""_030"",
    crse.""_030"", crse.""{fc}"", crse.""_058"",
    CASE
        WHEN COALESCE(s.""{uc}""::text, '') = '{uv}' AND COALESCE(crse.""{fc}""::text, '') = '{fv}'
        THEN 'PASS' ELSE 'FAIL'
    END AS control_check
FROM ""{{schema}}"".""{studTable}"" s
INNER JOIN ""{{schema}}"".""{bridgeTable}"" bridge ON s.""_007"" = bridge.""_007""
INNER JOIN ""{{schema}}"".""{crseTable}"" crse ON bridge.""_030"" = crse.""_030""
WHERE COALESCE(s.""{uc}""::text, '') = '{uv}' AND COALESCE(crse.""{fc}""::text, '') = '{fv}';

-- CONTROL 2: {uc}='{uv}' AND {fc}='{fv}' AND {dc}='{dv}'
SELECT
    'Control_2' AS control_type,
    s.""_007"", s.""_001"", s.""{uc}"", s.""{dc}"",
    bridge.""_001"", bridge.""_007"", bridge.""_030"",
    crse.""_030"", crse.""{fc}"", crse.""_058"",
    CASE
        WHEN COALESCE(s.""{uc}""::text, '') = '{uv}'
         AND COALESCE(crse.""{fc}""::text, '') = '{fv}'
         AND COALESCE(s.""{dc}""::text, '') = '{dv}'
        THEN 'PASS' ELSE 'FAIL'
    END AS control_check
FROM ""{{schema}}"".""{studTable}"" s
INNER JOIN ""{{schema}}"".""{bridgeTable}"" bridge ON s.""_007"" = bridge.""_007""
INNER JOIN ""{{schema}}"".""{crseTable}"" crse ON bridge.""_030"" = crse.""_030""
WHERE COALESCE(s.""{uc}""::text, '') = '{uv}'
  AND COALESCE(crse.""{fc}""::text, '') = '{fv}'
  AND COALESCE(s.""{dc}""::text, '') = '{dv}';

-- CONTROL 3: {uc}='{uv}' AND {fc}='{fv}' AND {dc}<>'{dv}'
SELECT
    'Control_3' AS control_type,
    s.""_007"", s.""_001"", s.""{uc}"", s.""{dc}"",
    bridge.""_001"", bridge.""_007"", bridge.""_030"",
    crse.""_030"", crse.""{fc}"", crse.""_058"",
    CASE
        WHEN COALESCE(s.""{uc}""::text, '') = '{uv}'
         AND COALESCE(crse.""{fc}""::text, '') = '{fv}'
         AND COALESCE(s.""{dc}""::text, '') <> '{dv}'
        THEN 'PASS' ELSE 'FAIL'
    END AS control_check
FROM ""{{schema}}"".""{studTable}"" s
INNER JOIN ""{{schema}}"".""{bridgeTable}"" bridge ON s.""_007"" = bridge.""_007""
INNER JOIN ""{{schema}}"".""{crseTable}"" crse ON bridge.""_030"" = crse.""_030""
WHERE COALESCE(s.""{uc}""::text, '') = '{uv}'
  AND COALESCE(crse.""{fc}""::text, '') = '{fv}'
  AND COALESCE(s.""{dc}""::text, '') <> '{dv}';";

            return Task.FromResult(sql.Trim());
        }

        private async Task<Rule16ValidationSummary> AnalyseAsync(Rule16ValidationRequest request, bool includeAllReviewRows)
        {
            await EnsureRule16IndexesAsync(request.ClientId, request.StudTable, request.BridgeTable, request.CrseTable);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var studTable = request.StudTable;
            var bridgeTable = request.BridgeTable;
            var crseTable = request.CrseTable;
            var unfulfilledCol = string.IsNullOrWhiteSpace(request.UnfulfilledCol) ? "_025" : request.UnfulfilledCol;
            var unfulfilledVal = request.UnfulfilledVal;
            var foundationCol = string.IsNullOrWhiteSpace(request.FoundationCol) ? "_091" : request.FoundationCol;
            var foundationVal = request.FoundationVal;
            var distanceCol = string.IsNullOrWhiteSpace(request.DistanceCol) ? "_024" : request.DistanceCol;
            var distanceVal = request.DistanceVal;

            var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{studTable}\";");
            var bridgeCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{bridgeTable}\";");
            var crseCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{crseTable}\";");

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule16PrepSql(schema, studTable, bridgeTable, crseTable, unfulfilledCol, unfulfilledVal, foundationCol, foundationVal, distanceCol, distanceVal);
                await prepCommand.ExecuteNonQueryAsync();
            }

            int unfulfilledPopulationCount, control1PassPopulation, control2PassPopulation, control3PassPopulation;
            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = BuildRule16CountAfterPrepSql(distanceVal);
                await using var countReader = await countCommand.ExecuteReaderAsync();
                unfulfilledPopulationCount = control1PassPopulation = control2PassPopulation = control3PassPopulation = 0;
                if (await countReader.ReadAsync())
                {
                    unfulfilledPopulationCount = control1PassPopulation = GetInt(countReader, 0);
                    control2PassPopulation = GetInt(countReader, 1);
                    control3PassPopulation = GetInt(countReader, 2);
                }
            }

            var reviewRows = await LoadControlRowsFromTempAsync(connection, includeAllReviewRows ? (int?)null : BrowserPreviewRowLimit, unfulfilledCol, unfulfilledVal, foundationCol, foundationVal, distanceCol, distanceVal);
            reviewRows = NormalizeReviewRows(reviewRows);

            var controlSummaries = BuildControlSummaries(control1PassPopulation, control2PassPopulation, control3PassPopulation, unfulfilledCol, unfulfilledVal, foundationCol, foundationVal, distanceCol, distanceVal);
            var totalValidated = control1PassPopulation;
            var passCount = control1PassPopulation;
            var failCount = 0;
            var isPreviewOnly = !includeAllReviewRows && totalValidated > reviewRows.Count;

            return new Rule16ValidationSummary
            {
                Success = true,
                StudRecordCount = studCount,
                BridgeRecordCount = bridgeCount,
                CrseRecordCount = crseCount,
                UnfulfilledPopulationCount = unfulfilledPopulationCount,
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
                CrseTable = request.CrseTable,
                UnfulfilledCol = request.UnfulfilledCol,
                UnfulfilledVal = request.UnfulfilledVal,
                FoundationCol = request.FoundationCol,
                FoundationVal = request.FoundationVal,
                DistanceCol = request.DistanceCol,
                DistanceVal = request.DistanceVal,
                TableLinkageText = $"{request.StudTable} -> {request.BridgeTable} -> {request.CrseTable}",
                RuleModeText = "100% population testing of matching control rows",
                ProcedureSteps = BuildProcedureSteps(request.StudTable, request.BridgeTable, request.CrseTable),
                ClientId = request.ClientId,
                ControlSummaries = controlSummaries,
                ReviewRows = reviewRows,
                Warning = includeAllReviewRows
                    ? "Rule 16 completed with the full matching control result set."
                    : "Counts reflect the full matching control result set. Browser review rows are limited for performance."
            };
        }

        private async Task<int> SaveValidationRunAsync(Rule16ValidationRequest request, Rule16ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 16);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 16,
                RuleName = "Student Population Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.BridgeTable,
                StudColumn = request.CrseTable,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)))),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            summary.SavedRunId = runId;
            return runId;
        }

        // Uploaded engagement tables have no indexes beyond their primary key. rule16_base's
        // joins run directly against the base STUD/CREG/CRSE tables (real engagements have seen
        // 450k+ row CREG tables) — building the expression indexes once, up front, makes every
        // run after the first fast.
        private async Task EnsureRule16IndexesAsync(int clientId, string studTable, string bridgeTable, string crseTable)
        {
            await _datasets.EnsureJoinIndexAsync(clientId, studTable, "_007");
            await _datasets.EnsureJoinIndexAsync(clientId, bridgeTable, "_007");
            await _datasets.EnsureJoinIndexAsync(clientId, bridgeTable, "_030");
            await _datasets.EnsureJoinIndexAsync(clientId, crseTable, "_030");
        }

        private static string BuildRule16PrepSql(
            string schema, string studTable, string bridgeTable, string crseTable,
            string unfulfilledCol, string unfulfilledVal,
            string foundationCol, string foundationVal,
            string distanceCol, string distanceVal) => $@"
DROP TABLE IF EXISTS rule16_base;
DROP TABLE IF EXISTS rule16_results;

CREATE TEMP TABLE rule16_base AS
SELECT
    CAST(s.""_007""               AS text) AS student_number,
    CAST(s.""_001""               AS text) AS student_qualification_code,
    CAST(s.""{unfulfilledCol}""   AS text) AS qualification_fulfilled_indicator,
    CAST(s.""{distanceCol}""      AS text) AS attendance_mode,
    CAST(creg.""_001""            AS text) AS creg_qualification_code,
    CAST(creg.""_007""            AS text) AS creg_student_number,
    CAST(creg.""_030""            AS text) AS creg_course_code,
    CAST(crse.""_030""            AS text) AS crse_course_code,
    CAST(crse.""{foundationCol}"" AS text) AS foundation_course_indicator,
    CAST(crse.""_058""            AS text) AS crse_058
FROM ""{schema}"".""{studTable}"" s
INNER JOIN ""{schema}"".""{bridgeTable}"" creg ON UPPER(TRIM(CAST(s.""_007"" AS text))) = UPPER(TRIM(CAST(creg.""_007"" AS text)))
INNER JOIN ""{schema}"".""{crseTable}""   crse ON UPPER(TRIM(CAST(creg.""_030"" AS text))) = UPPER(TRIM(CAST(crse.""_030"" AS text)))
WHERE COALESCE(CAST(s.""{unfulfilledCol}""   AS text), '') = '{EscapeSqlString(unfulfilledVal)}'
  AND COALESCE(CAST(crse.""{foundationCol}"" AS text), '') = '{EscapeSqlString(foundationVal)}';

CREATE INDEX ON rule16_base (student_number, student_qualification_code, creg_course_code);
ANALYZE rule16_base;

CREATE TEMP TABLE rule16_results AS
SELECT
    ROW_NUMBER() OVER (ORDER BY student_number, student_qualification_code, creg_course_code) AS extract_number,
    student_number,
    student_qualification_code,
    qualification_fulfilled_indicator,
    attendance_mode,
    creg_qualification_code,
    creg_student_number,
    creg_course_code,
    crse_course_code,
    foundation_course_indicator,
    crse_058,
    'PASS' AS control_check
FROM rule16_base;

ANALYZE rule16_results;";

        private static string BuildRule16CountAfterPrepSql(string distanceVal) => $@"
SELECT
    COUNT(*) AS total_population,
    SUM(CASE WHEN COALESCE(attendance_mode,'') = '{EscapeSqlString(distanceVal)}'  THEN 1 ELSE 0 END) AS control2_pass,
    SUM(CASE WHEN COALESCE(attendance_mode,'') <> '{EscapeSqlString(distanceVal)}' THEN 1 ELSE 0 END) AS control3_pass
FROM rule16_results;";

        private static string BuildPopulationCountSql(
            string schema, string studTable, string bridgeTable, string crseTable,
            string unfulfilledCol = "_025", string unfulfilledVal = "N",
            string foundationCol = "_091", string foundationVal = "Y",
            string distanceCol = "_024", string distanceVal = "D") => $@"
WITH base AS (
    SELECT
        CASE WHEN COALESCE(CAST(s.""{unfulfilledCol}""   AS text),'') = '{EscapeSqlString(unfulfilledVal)}'
              AND COALESCE(CAST(crse.""{foundationCol}"" AS text),'') = '{EscapeSqlString(foundationVal)}' THEN 1 ELSE 0 END AS is_c1,
        CASE WHEN COALESCE(CAST(s.""{unfulfilledCol}""   AS text),'') = '{EscapeSqlString(unfulfilledVal)}'
              AND COALESCE(CAST(crse.""{foundationCol}"" AS text),'') = '{EscapeSqlString(foundationVal)}'
              AND COALESCE(CAST(s.""{distanceCol}""       AS text),'') = '{EscapeSqlString(distanceVal)}' THEN 1 ELSE 0 END AS is_c2,
        CASE WHEN COALESCE(CAST(s.""{unfulfilledCol}""   AS text),'') = '{EscapeSqlString(unfulfilledVal)}'
              AND COALESCE(CAST(crse.""{foundationCol}"" AS text),'') = '{EscapeSqlString(foundationVal)}'
              AND COALESCE(CAST(s.""{distanceCol}""       AS text),'') <> '{EscapeSqlString(distanceVal)}' THEN 1 ELSE 0 END AS is_c3
    FROM ""{schema}"".""{studTable}"" s
    INNER JOIN ""{schema}"".""{bridgeTable}"" creg ON s.""_007"" = creg.""_007""
    INNER JOIN ""{schema}"".""{crseTable}""   crse ON creg.""_030"" = crse.""_030""
)
SELECT
    COALESCE(SUM(is_c1), 0) AS unfulfilled_population_count,
    COALESCE(SUM(is_c2), 0) AS control2_pass_population,
    COALESCE(SUM(is_c3), 0) AS control3_pass_population
FROM base;";

        private async Task<List<Rule16ValidationRowRecord>> LoadControlRowsFromTempAsync(
            NpgsqlConnection connection, int? maxRows,
            string unfulfilledCol, string unfulfilledVal,
            string foundationCol, string foundationVal,
            string distanceCol, string distanceVal)
        {
            var limitClause = maxRows.HasValue && maxRows.Value > 0 ? $"LIMIT {maxRows.Value}" : "";
            await using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT
    extract_number,
    'Control_1'              AS control_type,
    'PASS'                   AS validation_result,
    student_number,
    student_qualification_code,
    qualification_fulfilled_indicator,
    attendance_mode,
    creg_qualification_code,
    creg_student_number,
    creg_course_code,
    crse_course_code,
    foundation_course_indicator,
    crse_058,
    control_check
FROM rule16_results
ORDER BY extract_number
{limitClause};";

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule16ValidationRowRecord>();
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

                rows.Add(new Rule16ValidationRowRecord
                {
                    ValidationNumber = rows.Count + 1,
                    ControlType = ReadValue(displayValues, "Control_Type"),
                    ControlLabel = ReadValue(displayValues, "Control_Label"),
                    ValidationResult = ReadValue(displayValues, "Validation_Result"),
                    ValidationExplanation = "",
                    DisplayValues = displayValues
                });

                EnrichRule16DisplayValues(rows[^1], unfulfilledCol, unfulfilledVal, foundationCol, foundationVal, distanceCol, distanceVal);
            }

            return rows;
        }

        // Postgres lower-cases unquoted column names; the view's JS keys off the original
        // (Pascal_Case) names that the SQL Server version's reader produced, so translate back.
        private static readonly Dictionary<string, string> DisplayNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["extract_number"] = "Extract_Number",
            ["control_type"] = "Control_Type",
            ["validation_result"] = "Validation_Result",
            ["student_number"] = "Student_Number",
            ["student_qualification_code"] = "Student_Qualification_Code",
            ["qualification_fulfilled_indicator"] = "Qualification_Fulfilled_Indicator",
            ["attendance_mode"] = "Attendance_Mode",
            ["creg_qualification_code"] = "CREG_Qualification_Code",
            ["creg_student_number"] = "CREG_Student_Number",
            ["creg_course_code"] = "CREG_Course_Code",
            ["crse_course_code"] = "CRSE_Course_Code",
            ["foundation_course_indicator"] = "Foundation_Course_Indicator",
            ["crse_058"] = "CRSE_058",
            ["control_check"] = "Control_Check"
        };

        private static string ToPascalDisplayName(string columnName) =>
            DisplayNameMap.TryGetValue(columnName, out var mapped) ? mapped : columnName;

        private static List<Rule16ControlSummaryItemViewModel> BuildControlSummaries(
            int control1PassPopulation, int control2PassPopulation, int control3PassPopulation,
            string unfulfilledCol, string unfulfilledVal,
            string foundationCol, string foundationVal,
            string distanceCol, string distanceVal)
        {
            return new List<Rule16ControlSummaryItemViewModel>
            {
                BuildControlSummary("Control_1", "Control 1", $"{unfulfilledCol}='{unfulfilledVal}' AND {foundationCol}='{foundationVal}'", control1PassPopulation),
                BuildControlSummary("Control_2", "Control 2", $"{unfulfilledCol}='{unfulfilledVal}' AND {foundationCol}='{foundationVal}' AND {distanceCol}='{distanceVal}'", control2PassPopulation),
                BuildControlSummary("Control_3", "Control 3", $"{unfulfilledCol}='{unfulfilledVal}' AND {foundationCol}='{foundationVal}' AND {distanceCol}<>'{distanceVal}'", control3PassPopulation)
            };
        }

        private static Rule16ControlSummaryItemViewModel BuildControlSummary(string controlType, string controlLabel, string criteriaText, int passCount) => new()
        {
            ControlType = controlType,
            ControlLabel = controlLabel,
            CriteriaText = criteriaText,
            RequestedCount = passCount,
            AvailableCount = passCount,
            AchievedCount = passCount,
            TotalCount = passCount,
            PassCount = passCount,
            FailCount = 0,
            Status = passCount > 0 ? "PASS" : "NO DATA"
        };

        private static List<Rule16ValidationRowRecord> NormalizeReviewRows(IEnumerable<Rule16ValidationRowRecord>? rows) =>
            (rows ?? Enumerable.Empty<Rule16ValidationRowRecord>())
                .Select((row, index) => { row.ValidationNumber = index + 1; return row; })
                .ToList();

        private async Task<Rule16ValidationSummary> ExpandSavedSummaryIfNeededAsync(Rule16ValidationSummary summary, int clientId)
        {
            var looksLikeStoredPreviewSample =
                summary.ReviewRows.Count > 0 &&
                summary.ReviewRows.Count <= BrowserPreviewRowLimit &&
                summary.TotalValidated > 0;

            if (!summary.IsPreviewOnly && summary.ReviewRows.Count >= summary.TotalValidated && !looksLikeStoredPreviewSample)
                return summary;

            if (string.IsNullOrWhiteSpace(summary.StudTable) || string.IsNullOrWhiteSpace(summary.BridgeTable) || string.IsNullOrWhiteSpace(summary.CrseTable))
                return summary;

            try
            {
                var expanded = await AnalyseAsync(new Rule16ValidationRequest
                {
                    ClientId = clientId,
                    RunId = summary.SavedRunId,
                    StudTable = summary.StudTable,
                    BridgeTable = summary.BridgeTable,
                    CrseTable = summary.CrseTable,
                    UnfulfilledCol = summary.UnfulfilledCol,
                    UnfulfilledVal = summary.UnfulfilledVal,
                    FoundationCol = summary.FoundationCol,
                    FoundationVal = summary.FoundationVal,
                    DistanceCol = summary.DistanceCol,
                    DistanceVal = summary.DistanceVal
                }, includeAllReviewRows: true);

                expanded.Timestamp = string.IsNullOrWhiteSpace(summary.Timestamp) ? expanded.Timestamp : summary.Timestamp;
                expanded.ClientId = summary.ClientId;
                expanded.SavedRunId = summary.SavedRunId;
                expanded.Warning = string.IsNullOrWhiteSpace(summary.Warning)
                    ? "Saved Rule 16 results were expanded from the stored browser preview to the full result set."
                    : $"{summary.Warning} Full saved results were reloaded from the saved Rule 16 configuration.";

                return expanded;
            }
            catch
            {
                return summary;
            }
        }

        private static Rule16ValidationSummary CloneSummary(Rule16ValidationSummary summary) => new()
        {
            Success = summary.Success,
            StudRecordCount = summary.StudRecordCount,
            BridgeRecordCount = summary.BridgeRecordCount,
            CrseRecordCount = summary.CrseRecordCount,
            UnfulfilledPopulationCount = summary.UnfulfilledPopulationCount,
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
            UnfulfilledCol = summary.UnfulfilledCol,
            UnfulfilledVal = summary.UnfulfilledVal,
            FoundationCol = summary.FoundationCol,
            FoundationVal = summary.FoundationVal,
            DistanceCol = summary.DistanceCol,
            DistanceVal = summary.DistanceVal,
            TableLinkageText = summary.TableLinkageText,
            RuleModeText = summary.RuleModeText,
            ProcedureSteps = summary.ProcedureSteps.ToList(),
            ClientId = summary.ClientId,
            SavedRunId = summary.SavedRunId,
            ControlSummaries = summary.ControlSummaries.Select(item => new Rule16ControlSummaryItemViewModel
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

        private static Rule16ValidationRowRecord CloneReviewRow(Rule16ValidationRowRecord row) => new()
        {
            ValidationNumber = row.ValidationNumber,
            ControlType = row.ControlType,
            ControlLabel = row.ControlLabel,
            ValidationResult = row.ValidationResult,
            ValidationExplanation = row.ValidationExplanation,
            DisplayValues = new Dictionary<string, string?>(row.DisplayValues, StringComparer.OrdinalIgnoreCase)
        };

        private static void ApplyBrowserPreview(Rule16ValidationSummary summary)
        {
            var previewRows = summary.ReviewRows.OrderBy(row => row.ValidationNumber).Take(BrowserPreviewRowLimit).ToList();
            summary.DisplayedCount = previewRows.Count;
            summary.IsPreviewOnly = summary.TotalValidated > previewRows.Count;
            summary.PreviewLimit = summary.TotalValidated > previewRows.Count ? previewRows.Count : 0;
            summary.ReviewRows = previewRows;
        }

        private static List<string> BuildProcedureSteps(string studTable, string bridgeTable, string crseTable) => new()
        {
            $"Link {studTable}._007 to {bridgeTable}._007.",
            $"Link {bridgeTable}._030 to {crseTable}._030.",
            "Filter the joined population to students where the configured Unfulfilled column matches the configured value.",
            "Count full matching joined rows per control using the exact control-specific distance and foundation conditions.",
            "Return the full matching control result set for Control 1, Control 2, and Control 3."
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

        private static void ValidateRequest(string studTable, string bridgeTable, string crseTable)
        {
            if (string.IsNullOrWhiteSpace(studTable))
                throw new InvalidOperationException("STUD table is required.");
            if (string.IsNullOrWhiteSpace(bridgeTable))
                throw new InvalidOperationException("Bridge table is required.");
            if (string.IsNullOrWhiteSpace(crseTable))
                throw new InvalidOperationException("CRSE table is required.");
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

        private static void EnrichRule16DisplayValues(
            Rule16ValidationRowRecord row,
            string unfulfilledCol, string unfulfilledVal,
            string foundationCol, string foundationVal,
            string distanceCol, string distanceVal)
        {
            var values = row.DisplayValues;
            var attendanceMode = FormatRule16ColumnValue(ReadValue(values, "Attendance_Mode"));
            var foundationDisplay = FormatRule16ColumnValue(ReadValue(values, "Foundation_Course_Indicator"));
            var studentNum = FormatRule16ColumnValue(ReadValue(values, "Student_Number"));
            var qualCode = FormatRule16ColumnValue(ReadValue(values, "Student_Qualification_Code"));
            var courseCode = FormatRule16ColumnValue(ReadValue(values, "CREG_Course_Code"));
            var isDistance = string.Equals(attendanceMode.Trim(), distanceVal, StringComparison.OrdinalIgnoreCase);
            var category = isDistance ? "Distance Learning" : "Normal Student";

            var criteriaText = $"{unfulfilledCol}='{unfulfilledVal}' AND {foundationCol}='{foundationVal}'";
            var studCriteriaMessage = $"Passed: {unfulfilledCol}='{unfulfilledVal}' FOUND. Foundation: {foundationCol}='{foundationDisplay}'.";
            var bridgeLinkMessage = $"Linked via bridge table: Student={studentNum}, QualCode={qualCode}, CourseCode={courseCode}.";
            var validationExplanation = $"{unfulfilledCol}='{unfulfilledVal}': FOUND | {foundationCol}='{foundationDisplay}': FOUND | {distanceCol}='{attendanceMode}' | Category: {category}";

            values["FINAL_RULE_TEXT"] = criteriaText;
            values["Validation_Explanation"] = validationExplanation;
            values["STUD_CRITERIA_MESSAGE"] = studCriteriaMessage;
            values["BRIDGE_LINK_MESSAGE"] = bridgeLinkMessage;
            values["Student_Category"] = category;
            row.ValidationExplanation = validationExplanation;
        }

        private static string FormatRule16ColumnValue(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "[blank]" : value.Trim();

        private static bool RequestsMatchForPendingSave(Rule16ValidationRequest current, Rule16ValidationRequest pending) =>
            current.ClientId == pending.ClientId &&
            string.Equals(current.StudTable?.Trim(), pending.StudTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.BridgeTable?.Trim(), pending.BridgeTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CrseTable?.Trim(), pending.CrseTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.UnfulfilledCol?.Trim(), pending.UnfulfilledCol?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.UnfulfilledVal?.Trim(), pending.UnfulfilledVal?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.FoundationCol?.Trim(), pending.FoundationCol?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.FoundationVal?.Trim(), pending.FoundationVal?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.DistanceCol?.Trim(), pending.DistanceCol?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.DistanceVal?.Trim(), pending.DistanceVal?.Trim(), StringComparison.OrdinalIgnoreCase);

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

        private static Rule16ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                if (string.IsNullOrWhiteSpace(decoded)) return null;
                return JsonConvert.DeserializeObject<Rule16ValidationSummary>(decoded);
            }
            catch { return null; }
        }
    }
}
