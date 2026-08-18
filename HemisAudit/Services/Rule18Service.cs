using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 18: NSFAS student validation — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. Ported from the Rule12/14/15
    // pattern. 3-table rule (STUD, CREG "bridge", CRSE); this is a 100% population extraction
    // rule (three overlapping "control" slices of the same joined population), not a
    // pass/fail existence test, so every extracted row is PASS by construction — matching the
    // original design exactly.
    //
    // The original SQL-Server implementation also bulk-copied results into a separate
    // dbo.Rule18Results cache table so downloads didn't have to re-query the live institution
    // server. That optimization doesn't apply here: engagement data and system tables already
    // live in the same Postgres database, so a full re-scan (the same ExpandAndPersistSavedSummaryIfNeededAsync
    // pattern used by every other migrated rule) is already fast and reliable — the cache
    // table subsystem is dropped rather than ported.
    public class Rule18Service : IRule18Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IPendingValidationCacheService _pendingValidationCache;

        public Rule18Service(
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

        public async Task<Rule18TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule18TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule18TableDiscoveryResult
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
                return new Rule18TableDiscoveryResult { Success = false, Error = ex.Message };
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

        public async Task<ColumnValuesResult> GetColumnValuesAsync(int clientId, string tableName, string columnName)
        {
            try
            {
                var validColumns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                if (!validColumns.Contains(columnName, StringComparer.Ordinal))
                    return new ColumnValuesResult { Success = false, Error = $"Column '{columnName}' was not found in table '{tableName}'." };

                var (conn, schema) = await OpenEngagementConnectionAsync(clientId);
                await using var connection = conn;
                await using var command = connection.CreateCommand();
                command.CommandText = $@"
SELECT DISTINCT CAST(""{columnName}"" AS text)
FROM ""{schema}"".""{tableName}""
WHERE ""{columnName}"" IS NOT NULL
ORDER BY 1
LIMIT 200;";
                await using var reader = await command.ExecuteReaderAsync();
                var values = new List<string>();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0))
                        values.Add(reader.GetString(0));
                }

                return new ColumnValuesResult { Success = true, Values = values };
            }
            catch (Exception ex)
            {
                return new ColumnValuesResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule18VerifyResult> VerifyTablesAsync(Rule18VerifyRequest request)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);

                var cfg = await ResolveColumnConfigAsync(request.ClientId, request.StudTable, request.BridgeTable, request.CrseTable,
                    request.NsfasFilterCol, request.NsfasFilterValue, request.FoundationFilterCol, request.FoundationFilterValue,
                    request.DistanceFilterCol, request.DistanceFilterValue, request.CredJoinCol, request.CredCourseCol,
                    request.CrseCourseCol, request.CrseNameCol);

                await EnsureRule18IndexesAsync(request.ClientId, request.StudTable, request.BridgeTable, request.CrseTable, cfg);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";");
                var bridgeCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.BridgeTable}\";");
                var crseCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CrseTable}\";");

                await using (var prepCommand = connection.CreateCommand())
                {
                    prepCommand.CommandText = BuildRule18PrepSql(schema, request.StudTable, request.BridgeTable, request.CrseTable, cfg);
                    await prepCommand.ExecuteNonQueryAsync();
                }

                var (nsfasCount, c1, c2, c3) = await GetCountsAsync(connection, cfg.NsfasFilterValue);

                return new Rule18VerifyResult
                {
                    Success = true,
                    StudRecordCount = studCount,
                    BridgeRecordCount = bridgeCount,
                    CrseRecordCount = crseCount,
                    NsfasPopulationCount = nsfasCount,
                    Control1PopulationCount = c1,
                    Control2PopulationCount = c2,
                    Control3PopulationCount = c3,
                    Control4PopulationCount = 0
                };
            }
            catch (Exception ex)
            {
                return new Rule18VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule18ValidationSummary> RunValidationAsync(Rule18ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);

                // Save the full population immediately (matching Rule16's approach) rather than
                // saving a 10-row preview and lazily expanding it later. The lazy-expand path
                // (ExpandAndPersistSavedSummaryIfNeededAsync) used to fire the FIRST time the
                // workspace page loaded after a run, silently re-running the full analysis in
                // what looked like a routine page load — that's what made loading/saving feel
                // slow. Saving complete data up front avoids that hidden re-analysis entirely.
                var summary = await AnalyseAsync(request, includeAllReviewRows: true);
                if (summary.Success && request.ClientId > 0)
                {
                    try
                    {
                        var summaryToPersist = CloneSummary(summary);
                        summaryToPersist.SavedRunId = null;
                        summary.SavedRunId = await SaveValidationRunAsync(request, summaryToPersist, userEmail, userName);

                        if (!string.IsNullOrWhiteSpace(userEmail))
                            _pendingValidationCache.ClearPending(18, request.ClientId, userEmail!);
                    }
                    catch (Exception ex)
                    {
                        summary.Warning = $"Analysis completed, but the workspace could not be saved automatically: {ex.Message}";
                    }
                }

                if (!summary.SavedRunId.HasValue)
                {
                    if (summary.Success && request.ClientId > 0 && !string.IsNullOrWhiteSpace(userEmail))
                        _pendingValidationCache.StorePending(18, request.ClientId, userEmail!, request, CloneSummary(summary), userName);

                    summary.Warning = string.IsNullOrWhiteSpace(summary.Warning)
                        ? "Rule 18 validation completed. Click Save Workspace to write this validated result to the system database."
                        : summary.Warning;
                }
                else
                {
                    summary.Warning = "The current Rule 18 run has been written to the system database. Click Save Workspace to finalize it for signoff.";
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule18ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule18ValidationSummary> GetExportSummaryAsync(Rule18ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);
            return await AnalyseAsync(request, includeAllReviewRows: true);
        }

        public Task<Rule18ValidationSummary?> GetPendingValidationPreviewAsync(int clientId, string reviewerEmail)
        {
            var pending = _pendingValidationCache.GetPending<Rule18ValidationRequest, Rule18ValidationSummary>(18, clientId, reviewerEmail);
            if (pending == null)
                return Task.FromResult<Rule18ValidationSummary?>(null);

            var preview = CloneSummary(pending.Summary);
            preview.SavedRunId = null;
            preview.Warning = "This Rule 18 validation is still pending. Click Save Workspace to write it to the system database.";
            ApplyBrowserPreview(preview);
            return Task.FromResult<Rule18ValidationSummary?>(preview);
        }

        public Task<bool> HasPendingValidationAsync(int clientId, string reviewerEmail)
            => Task.FromResult(_pendingValidationCache.HasPending(18, clientId, reviewerEmail));

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule18WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 18);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (deserializedSummary != null && includeSummary)
            {
                deserializedSummary = await ExpandAndPersistSavedSummaryIfNeededAsync(row.RunId, deserializedSummary, clientId);
                ApplyBrowserPreview(deserializedSummary);
            }
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule18WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable,
                BridgeTable = deserializedSummary?.BridgeTable ?? (string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_CREG" : row.DeceasedTable),
                CrseTable = deserializedSummary?.CrseTable ?? (string.IsNullOrWhiteSpace(row.StudColumn) ? "dbo_CRSE" : row.StudColumn),
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary,
                Control1FilterCol = deserializedSummary?.Control1FilterCol ?? "_019",
                Control1FilterValue = deserializedSummary?.Control1FilterValue ?? "NS",
                NsfasFilterCol = deserializedSummary?.NsfasFilterCol ?? "_019",
                NsfasFilterValue = deserializedSummary?.NsfasFilterValue ?? "NS",
                FoundationFilterCol = deserializedSummary?.FoundationFilterCol ?? "_091",
                FoundationFilterValue = deserializedSummary?.FoundationFilterValue ?? "Y",
                DistanceFilterCol = deserializedSummary?.DistanceFilterCol ?? "_024",
                DistanceFilterValue = deserializedSummary?.DistanceFilterValue ?? "D",
                CredJoinCol = string.IsNullOrWhiteSpace(deserializedSummary?.CredJoinCol) ? "_001" : deserializedSummary!.CredJoinCol,
                CredCourseCol = string.IsNullOrWhiteSpace(deserializedSummary?.CredCourseCol) ? "_030" : deserializedSummary!.CredCourseCol,
                CrseCourseCol = string.IsNullOrWhiteSpace(deserializedSummary?.CrseCourseCol) ? "_030" : deserializedSummary!.CrseCourseCol,
                CrseNameCol = string.IsNullOrWhiteSpace(deserializedSummary?.CrseNameCol) ? "_058" : deserializedSummary!.CrseNameCol
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

        public async Task<Rule18RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 18);
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

            var review = new Rule18RunReviewViewModel
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

        public async Task<Rule18WorkspaceSaveResult> SaveWorkspaceAsync(Rule18ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);

                if (request.RunId.HasValue && request.RunId.Value > 0)
                {
                    var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                    if (!clientId.HasValue || clientId.Value != request.ClientId)
                        return new Rule18WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

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
                        _pendingValidationCache.ClearPending(18, request.ClientId, reviewerEmail);

                    var currentWorkspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                    return new Rule18WorkspaceSaveResult
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

                var pending = _pendingValidationCache.GetPending<Rule18ValidationRequest, Rule18ValidationSummary>(18, request.ClientId, reviewerEmail);
                if (pending == null)
                    return new Rule18WorkspaceSaveResult { Success = false, Error = "Run Rule 18 first so the current workspace is written to the system database." };

                if (!RequestsMatchForPendingSave(request, pending.Request))
                    return new Rule18WorkspaceSaveResult { Success = false, Error = "Workspace settings changed after validation. Run Rule 18 again before saving." };

                var summaryToSave = CloneSummary(pending.Summary);
                if (summaryToSave.IsPreviewOnly || summaryToSave.ReviewRows.Count < summaryToSave.TotalValidated)
                    summaryToSave = await AnalyseAsync(pending.Request, includeAllReviewRows: true);

                summaryToSave.SavedRunId = null;
                var savedRunId = await SaveValidationRunAsync(pending.Request, summaryToSave, reviewerEmail, reviewerName ?? pending.ReviewerName);
                _pendingValidationCache.ClearPending(18, request.ClientId, reviewerEmail);

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
                return new Rule18WorkspaceSaveResult
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
                return new Rule18WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule18WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule18WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                if (!string.IsNullOrWhiteSpace(reviewerEmail))
                    _pendingValidationCache.ClearPending(18, clientId.Value, reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule18WorkspaceSaveResult
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
                return new Rule18WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 18 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 18 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 18 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule18ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);

            var cfg = await ResolveColumnConfigAsync(request.ClientId, request.StudTable, request.BridgeTable, request.CrseTable,
                request.NsfasFilterCol, request.NsfasFilterValue, request.FoundationFilterCol, request.FoundationFilterValue,
                request.DistanceFilterCol, request.DistanceFilterValue, request.CredJoinCol, request.CredCourseCol,
                request.CrseCourseCol, request.CrseNameCol);

            var sql = $@"-- HEMIS RULE 18: NSFAS STUDENTS VALIDATION
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.

{BuildRule18PrepSql("{schema}", request.StudTable, request.BridgeTable, request.CrseTable, cfg)}

-- Full result set
SELECT * FROM rule18_validation ORDER BY ""Control_Type"", ""Student_Number"", ""CREG_Course_Code"";

-- Summary
SELECT
    (SELECT COUNT(DISTINCT student_number) FROM rule18_base WHERE COALESCE(nsfas_status, '') = '{EscapeSqlString(cfg.NsfasFilterValue.ToUpperInvariant())}') AS nsfas_count,
    COUNT(CASE WHEN ""Control_Type"" = 'Control_1' THEN 1 END) AS control1_count,
    COUNT(CASE WHEN ""Control_Type"" = 'Control_2' THEN 1 END) AS control2_count,
    COUNT(CASE WHEN ""Control_Type"" = 'Control_3' THEN 1 END) AS control3_count
FROM rule18_validation;";

            return sql.Trim();
        }

        // Rule 18's population is a UNION ALL of three overlapping controls (the same
        // registration row can appear in Control_1, Control_2, and Control_3 at once), so on a
        // real institution's data the row count can be several times the size of the raw CREG
        // table — large enough to exhaust process memory if ever materialized into C# objects in
        // one shot (confirmed: a Run Validation on real data threw OutOfMemoryException once this
        // "load everything" path became reachable from the interactive Run button). This cap
        // applies to every "full" load, not just the browser preview, so there is no path — Run,
        // workspace reload, or export — that can try to hold an unbounded row count in memory.
        private const int MaxSafeReviewRows = 5000;

        private async Task<Rule18ValidationSummary> AnalyseAsync(Rule18ValidationRequest request, bool includeAllReviewRows)
        {
            var cfg = await ResolveColumnConfigAsync(request.ClientId, request.StudTable, request.BridgeTable, request.CrseTable,
                request.NsfasFilterCol, request.NsfasFilterValue, request.FoundationFilterCol, request.FoundationFilterValue,
                request.DistanceFilterCol, request.DistanceFilterValue, request.CredJoinCol, request.CredCourseCol,
                request.CrseCourseCol, request.CrseNameCol);

            await EnsureRule18IndexesAsync(request.ClientId, request.StudTable, request.BridgeTable, request.CrseTable, cfg);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";");
            var bridgeCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.BridgeTable}\";");
            var crseCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CrseTable}\";");

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule18PrepSql(schema, request.StudTable, request.BridgeTable, request.CrseTable, cfg);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var (nsfasCount, c1, c2, c3) = await GetCountsAsync(connection, cfg.NsfasFilterValue);

            var reviewRowCap = includeAllReviewRows ? MaxSafeReviewRows : BrowserPreviewRowLimit;
            var reviewRows = await LoadControlRowsAsync(connection, reviewRowCap);
            reviewRows = NormalizeReviewRows(reviewRows);

            var controlSummaries = BuildControlSummaries(c1, c2, c3,
                cfg.NsfasFilterCol, cfg.NsfasFilterValue, cfg.FoundationFilterCol, cfg.FoundationFilterValue,
                cfg.DistanceFilterCol, cfg.DistanceFilterValue);
            var totalValidated = controlSummaries.Sum(x => x.TotalCount);
            var passCount = controlSummaries.Sum(x => x.PassCount);
            var failCount = controlSummaries.Sum(x => x.FailCount);
            var isPreviewOnly = totalValidated > reviewRows.Count;

            return new Rule18ValidationSummary
            {
                Success = true,
                StudRecordCount = studCount,
                BridgeRecordCount = bridgeCount,
                CrseRecordCount = crseCount,
                NsfasPopulationCount = nsfasCount,
                TotalRequested = totalValidated,
                TotalValidated = totalValidated,
                DisplayedCount = reviewRows.Count,
                IsPreviewOnly = isPreviewOnly,
                PreviewLimit = isPreviewOnly ? reviewRowCap : 0,
                PassCount = passCount,
                FailCount = failCount,
                ExceptionRate = totalValidated == 0 ? 0m : Math.Round(failCount * 100m / totalValidated, 2),
                Status = failCount == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable = request.StudTable,
                BridgeTable = request.BridgeTable,
                CrseTable = request.CrseTable,
                Control1FilterCol = cfg.NsfasFilterCol,
                Control1FilterValue = cfg.NsfasFilterValue,
                NsfasFilterCol = cfg.NsfasFilterCol,
                NsfasFilterValue = cfg.NsfasFilterValue,
                FoundationFilterCol = cfg.FoundationFilterCol,
                FoundationFilterValue = cfg.FoundationFilterValue,
                DistanceFilterCol = cfg.DistanceFilterCol,
                DistanceFilterValue = cfg.DistanceFilterValue,
                CredJoinCol = cfg.CredJoinCol,
                CredCourseCol = cfg.CredCourseCol,
                CrseCourseCol = cfg.CrseCourseCol,
                CrseNameCol = cfg.HasCrseName ? cfg.CrseNameCol : "",
                TableLinkageText = $"{request.StudTable}.{cfg.CredJoinCol} -> {request.BridgeTable}.{cfg.CredJoinCol} | {request.BridgeTable}.{cfg.CredCourseCol} -> {request.CrseTable}.{cfg.CrseCourseCol}",
                RuleModeText = "100% population testing of all matching control rows",
                ProcedureSteps = BuildProcedureSteps(request.StudTable, request.BridgeTable, request.CrseTable, cfg.CredJoinCol, cfg.CredCourseCol, cfg.CrseCourseCol, cfg.CrseNameCol),
                ClientId = request.ClientId,
                ControlSummaries = controlSummaries,
                ReviewRows = reviewRows,
                Warning = !isPreviewOnly
                    ? "Rule 18 completed with the full matching control result set."
                    : includeAllReviewRows
                        ? $"Counts reflect the full matching control result set. The saved result rows are capped at {MaxSafeReviewRows:N0} to keep the app stable on very large populations; totals and pass/fail counts above are still exact."
                        : "Counts reflect the full matching control result set. Browser review rows are limited for performance."
            };
        }

        private async Task<int> SaveValidationRunAsync(Rule18ValidationRequest request, Rule18ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 18);

            var failRows = summary.ReviewRows.Where(row => string.Equals(row.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList();
            var persistedSummary = CloneSummary(summary);
            persistedSummary.SavedRunId = summary.SavedRunId;

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 18,
                RuleName = "NSFAS Student Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.BridgeTable,
                StudColumn = request.CrseTable,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(failRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(persistedSummary))
            }, userEmail, userName);

            summary.SavedRunId = runId;
            return runId;
        }

        // ── Column configuration resolution (degrades optional display columns to NULL) ────

        private sealed class Rule18ColumnConfig
        {
            public string NsfasFilterCol = "_019";
            public string NsfasFilterValue = "NS";
            public string FoundationFilterCol = "_091";
            public string FoundationFilterValue = "Y";
            public string DistanceFilterCol = "_024";
            public string DistanceFilterValue = "D";
            public string CredJoinCol = "_001";
            public string CredCourseCol = "_030";
            public string CrseCourseCol = "_030";
            public string CrseNameCol = "_058";
            public bool HasCrseName;
            public bool HasQualFulfilled;
        }

        private async Task<Rule18ColumnConfig> ResolveColumnConfigAsync(
            int clientId, string studTable, string bridgeTable, string crseTable,
            string? nsfasFilterCol, string? nsfasFilterValue,
            string? foundationFilterCol, string? foundationFilterValue,
            string? distanceFilterCol, string? distanceFilterValue,
            string? credJoinCol, string? credCourseCol, string? crseCourseCol, string? crseNameCol)
        {
            var cfg = new Rule18ColumnConfig
            {
                NsfasFilterCol = string.IsNullOrWhiteSpace(nsfasFilterCol) ? "_019" : nsfasFilterCol,
                NsfasFilterValue = string.IsNullOrWhiteSpace(nsfasFilterValue) ? "NS" : nsfasFilterValue,
                FoundationFilterCol = string.IsNullOrWhiteSpace(foundationFilterCol) ? "_091" : foundationFilterCol,
                FoundationFilterValue = string.IsNullOrWhiteSpace(foundationFilterValue) ? "Y" : foundationFilterValue,
                DistanceFilterCol = string.IsNullOrWhiteSpace(distanceFilterCol) ? "_024" : distanceFilterCol,
                DistanceFilterValue = string.IsNullOrWhiteSpace(distanceFilterValue) ? "D" : distanceFilterValue,
                CredJoinCol = string.IsNullOrWhiteSpace(credJoinCol) ? "_001" : credJoinCol,
                CredCourseCol = string.IsNullOrWhiteSpace(credCourseCol) ? "_030" : credCourseCol,
                CrseCourseCol = string.IsNullOrWhiteSpace(crseCourseCol) ? "_030" : crseCourseCol,
                CrseNameCol = string.IsNullOrWhiteSpace(crseNameCol) ? "_058" : crseNameCol
            };

            var studColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(clientId, studTable), StringComparer.OrdinalIgnoreCase);
            var bridgeColumns = await _datasets.GetValidatedColumnsAsync(clientId, bridgeTable);
            var crseColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(clientId, crseTable), StringComparer.OrdinalIgnoreCase);

            EnsureHasColumns(studTable, studColumns.ToList(), "_001", "_007", cfg.NsfasFilterCol, cfg.DistanceFilterCol);
            EnsureHasColumns(bridgeTable, bridgeColumns, cfg.CredJoinCol, cfg.CredCourseCol);
            EnsureHasColumns(crseTable, crseColumns.ToList(), cfg.CrseCourseCol, cfg.FoundationFilterCol);

            cfg.HasCrseName = crseColumns.Contains(cfg.CrseNameCol);
            cfg.HasQualFulfilled = studColumns.Contains("_025");

            return cfg;
        }

        private static void EnsureHasColumns(string tableName, IReadOnlyCollection<string> availableColumns, params string[] requiredColumns)
        {
            var missing = requiredColumns.Where(required => !availableColumns.Contains(required, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"Table {tableName} is missing required column(s): {string.Join(", ", missing)}.");
        }

        // Uploaded engagement tables have no indexes beyond their primary key. rule18_base joins
        // directly against the base STUD/BRIDGE/CRSE tables (real engagements have seen 450k+
        // row CREG-equivalent tables) — building the expression indexes once, up front, makes
        // every run after the first fast. Now load-bearing for every "Run Validation" click since
        // RunValidationAsync always analyses the full population (no more deferred preview save).
        private async Task EnsureRule18IndexesAsync(int clientId, string studTable, string bridgeTable, string crseTable, Rule18ColumnConfig cfg)
        {
            await _datasets.EnsureJoinIndexAsync(clientId, studTable, cfg.CredJoinCol);
            await _datasets.EnsureJoinIndexAsync(clientId, bridgeTable, cfg.CredJoinCol);
            await _datasets.EnsureJoinIndexAsync(clientId, bridgeTable, cfg.CredCourseCol);
            await _datasets.EnsureJoinIndexAsync(clientId, crseTable, cfg.CrseCourseCol);
        }

        // ── SQL builders (Postgres) ─────────────────────────────────────────────────────

        private static string BuildRule18PrepSql(string schema, string studTable, string bridgeTable, string crseTable, Rule18ColumnConfig cfg)
        {
            var nsfasVal = EscapeSqlString(cfg.NsfasFilterValue.ToUpperInvariant());
            var foundVal = EscapeSqlString(cfg.FoundationFilterValue.ToUpperInvariant());
            var distVal = EscapeSqlString(cfg.DistanceFilterValue.ToUpperInvariant());
            var crseNameSql = cfg.HasCrseName ? $@"CAST(crse.""{cfg.CrseNameCol}"" AS text)" : "NULL::text";
            var qualFulfilledSql = cfg.HasQualFulfilled ? @"CAST(s.""_025"" AS text)" : "NULL::text";

            return $@"
DROP TABLE IF EXISTS rule18_base;
DROP TABLE IF EXISTS rule18_validation;

CREATE TEMP TABLE rule18_base AS
SELECT
    CAST(s.""_007"" AS text) AS student_number,
    CAST(s.""_001"" AS text) AS student_qualification_code,
    UPPER(TRIM(CAST(s.""{cfg.NsfasFilterCol}"" AS text))) AS nsfas_status,
    UPPER(TRIM(CAST(s.""{cfg.DistanceFilterCol}"" AS text))) AS attendance_mode,
    {qualFulfilledSql} AS qualification_fulfilled_indicator,
    CAST(bridge.""{cfg.CredJoinCol}"" AS text) AS creg_qualification_code,
    CAST(bridge.""{cfg.CredCourseCol}"" AS text) AS creg_course_code,
    CAST(crse.""{cfg.CrseCourseCol}"" AS text) AS crse_course_code,
    UPPER(TRIM(CAST(crse.""{cfg.FoundationFilterCol}"" AS text))) AS foundation_course_indicator,
    {crseNameSql} AS crse_058
FROM ""{schema}"".""{studTable}"" s
INNER JOIN ""{schema}"".""{bridgeTable}"" bridge
    ON UPPER(TRIM(CAST(s.""{cfg.CredJoinCol}"" AS text))) = UPPER(TRIM(CAST(bridge.""{cfg.CredJoinCol}"" AS text)))
INNER JOIN ""{schema}"".""{crseTable}"" crse
    ON UPPER(TRIM(CAST(bridge.""{cfg.CredCourseCol}"" AS text))) = UPPER(TRIM(CAST(crse.""{cfg.CrseCourseCol}"" AS text)));

ANALYZE rule18_base;

CREATE TEMP TABLE rule18_validation AS
SELECT
    ROW_NUMBER() OVER (ORDER BY x.control_sort, x.student_number, x.creg_course_code) AS ""Extract_Number"",
    x.control_type AS ""Control_Type"",
    x.student_number AS ""Student_Number"",
    x.student_qualification_code AS ""Student_Qualification_Code"",
    x.nsfas_status AS ""NSFAS_Status"",
    x.attendance_mode AS ""Attendance_Mode"",
    x.qualification_fulfilled_indicator AS ""Qualification_Fulfilled_Indicator"",
    x.creg_qualification_code AS ""CREG_Qualification_Code"",
    x.creg_course_code AS ""CREG_Course_Code"",
    x.crse_course_code AS ""CRSE_Course_Code"",
    x.foundation_course_indicator AS ""Foundation_Course_Indicator"",
    x.crse_058 AS ""CRSE_058"",
    'PASS' AS ""Validation_Result""
FROM (
    SELECT 1 AS control_sort, 'Control_1' AS control_type, student_number, student_qualification_code, nsfas_status, attendance_mode,
           qualification_fulfilled_indicator, creg_qualification_code, creg_course_code, crse_course_code, foundation_course_indicator, crse_058
    FROM rule18_base
    WHERE COALESCE(nsfas_status, '') = '{nsfasVal}' AND COALESCE(foundation_course_indicator, '') = '{foundVal}'

    UNION ALL

    SELECT 2, 'Control_2', student_number, student_qualification_code, nsfas_status, attendance_mode,
           qualification_fulfilled_indicator, creg_qualification_code, creg_course_code, crse_course_code, foundation_course_indicator, crse_058
    FROM rule18_base
    WHERE COALESCE(nsfas_status, '') = '{nsfasVal}' AND COALESCE(foundation_course_indicator, '') = '{foundVal}' AND COALESCE(attendance_mode, '') = '{distVal}'

    UNION ALL

    SELECT 3, 'Control_3', student_number, student_qualification_code, nsfas_status, attendance_mode,
           qualification_fulfilled_indicator, creg_qualification_code, creg_course_code, crse_course_code, foundation_course_indicator, crse_058
    FROM rule18_base
    WHERE COALESCE(nsfas_status, '') = '{nsfasVal}' AND COALESCE(foundation_course_indicator, '') <> '{foundVal}' AND COALESCE(attendance_mode, '') <> '{distVal}'
) x;";
        }

        private static async Task<(int NsfasCount, int C1, int C2, int C3)> GetCountsAsync(NpgsqlConnection connection, string nsfasFilterValue)
        {
            var nsfasVal = EscapeSqlString(nsfasFilterValue.ToUpperInvariant());
            await using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT
    (SELECT COUNT(DISTINCT student_number) FROM rule18_base WHERE COALESCE(nsfas_status, '') = '{nsfasVal}') AS nsfas_count,
    COUNT(CASE WHEN ""Control_Type"" = 'Control_1' THEN 1 END) AS control1_count,
    COUNT(CASE WHEN ""Control_Type"" = 'Control_2' THEN 1 END) AS control2_count,
    COUNT(CASE WHEN ""Control_Type"" = 'Control_3' THEN 1 END) AS control3_count
FROM rule18_validation;";
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return (GetInt(reader, 0), GetInt(reader, 1), GetInt(reader, 2), GetInt(reader, 3));
            return (0, 0, 0, 0);
        }

        private async Task<List<Rule18ValidationRowRecord>> LoadControlRowsAsync(NpgsqlConnection connection, int? maxRows)
        {
            var perControlLimit = maxRows.HasValue && maxRows.Value > 0 ? Math.Max(maxRows.Value / 3, 1) : 0;

            var sql = perControlLimit > 0
                ? $@"
SELECT ""Control_Type"", ""Student_Number"", ""Student_Qualification_Code"", ""NSFAS_Status"", ""Attendance_Mode"",
       ""Qualification_Fulfilled_Indicator"", ""CREG_Qualification_Code"", ""CREG_Course_Code"", ""CRSE_Course_Code"",
       ""Foundation_Course_Indicator"", ""CRSE_058"", ""Validation_Result""
FROM (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY ""Control_Type"" ORDER BY ""Student_Number"", ""CREG_Course_Code"") AS preview_row_num
    FROM rule18_validation
) x
WHERE preview_row_num <= {perControlLimit}
ORDER BY ""Control_Type"", ""Student_Number"", ""CREG_Course_Code"";"
                : @"
SELECT ""Control_Type"", ""Student_Number"", ""Student_Qualification_Code"", ""NSFAS_Status"", ""Attendance_Mode"",
       ""Qualification_Fulfilled_Indicator"", ""CREG_Qualification_Code"", ""CREG_Course_Code"", ""CRSE_Course_Code"",
       ""Foundation_Course_Indicator"", ""CRSE_058"", ""Validation_Result""
FROM rule18_validation
ORDER BY ""Control_Type"", ""Student_Number"", ""CREG_Course_Code"";";

            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule18ValidationRowRecord>();
            while (await reader.ReadAsync())
            {
                var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    displayValues[reader.GetName(i)] = reader.IsDBNull(i)
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }

                rows.Add(new Rule18ValidationRowRecord
                {
                    ValidationNumber = rows.Count + 1,
                    ControlType = ReadValue(displayValues, "Control_Type"),
                    ValidationResult = ReadValue(displayValues, "Validation_Result"),
                    DisplayValues = displayValues
                });

                EnrichRule18DisplayValues(rows[^1]);
            }

            return rows;
        }

        private static List<Rule18ControlSummaryItemViewModel> BuildControlSummaries(
            int c1Count, int c2Count, int c3Count,
            string nsfasFilterCol, string nsfasFilterValue,
            string foundationFilterCol, string foundationFilterValue,
            string distanceFilterCol, string distanceFilterValue)
        {
            return new List<Rule18ControlSummaryItemViewModel>
            {
                BuildControlSummary("Control_1", "Control 1",
                    $"NSFAS_Status='{nsfasFilterValue}' AND Foundation_Course_Indicator='{foundationFilterValue}'", c1Count),
                BuildControlSummary("Control_2", "Control 2",
                    $"NSFAS_Status='{nsfasFilterValue}' AND Foundation_Course_Indicator='{foundationFilterValue}' AND Attendance_Mode='{distanceFilterValue}'", c2Count),
                BuildControlSummary("Control_3", "Control 3",
                    $"NSFAS_Status='{nsfasFilterValue}' AND Foundation_Course_Indicator<>'{foundationFilterValue}' AND Attendance_Mode<>'{distanceFilterValue}'", c3Count)
            };
        }

        private static Rule18ControlSummaryItemViewModel BuildControlSummary(string controlType, string controlLabel, string criteriaText, int passCount) => new()
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

        private static List<Rule18ValidationRowRecord> NormalizeReviewRows(IEnumerable<Rule18ValidationRowRecord>? rows) =>
            (rows ?? Enumerable.Empty<Rule18ValidationRowRecord>())
                .Select((row, index) => { row.ValidationNumber = index + 1; return row; })
                .ToList();

        private async Task<Rule18ValidationSummary> ExpandAndPersistSavedSummaryIfNeededAsync(int runId, Rule18ValidationSummary summary, int clientId)
        {
            // A saved run is "as complete as it will ever get" once ReviewRows already holds
            // min(TotalValidated, MaxSafeReviewRows) rows — re-running AnalyseAsync would just
            // recompute the identical capped result. Without this, any run whose true population
            // exceeds the cap (IsPreviewOnly permanently true, by design) would silently re-run
            // the full analysis on every single workspace load — the exact "hidden re-analysis on
            // routine page load" bug this method was already fixed for once, just re-triggered by
            // the safety cap that stops it from OOMing on very large populations.
            var looksLikeStoredPreviewSample =
                summary.ReviewRows.Count > 0 &&
                summary.ReviewRows.Count <= BrowserPreviewRowLimit &&
                summary.TotalValidated > BrowserPreviewRowLimit;

            var completenessTarget = Math.Min(summary.TotalValidated, MaxSafeReviewRows);
            if (summary.ReviewRows.Count >= completenessTarget && !looksLikeStoredPreviewSample)
                return summary;

            if (string.IsNullOrWhiteSpace(summary.StudTable) || string.IsNullOrWhiteSpace(summary.BridgeTable) || string.IsNullOrWhiteSpace(summary.CrseTable))
                return summary;

            try
            {
                var expanded = await AnalyseAsync(new Rule18ValidationRequest
                {
                    ClientId = clientId,
                    RunId = summary.SavedRunId,
                    StudTable = summary.StudTable,
                    BridgeTable = summary.BridgeTable,
                    CrseTable = summary.CrseTable,
                    Control1FilterCol = summary.Control1FilterCol,
                    Control1FilterValue = summary.Control1FilterValue,
                    NsfasFilterCol = summary.NsfasFilterCol,
                    NsfasFilterValue = summary.NsfasFilterValue,
                    FoundationFilterCol = summary.FoundationFilterCol,
                    FoundationFilterValue = summary.FoundationFilterValue,
                    DistanceFilterCol = summary.DistanceFilterCol,
                    DistanceFilterValue = summary.DistanceFilterValue,
                    CredJoinCol = summary.CredJoinCol,
                    CredCourseCol = summary.CredCourseCol,
                    CrseCourseCol = summary.CrseCourseCol,
                    CrseNameCol = summary.CrseNameCol
                }, includeAllReviewRows: true);

                expanded.Timestamp = string.IsNullOrWhiteSpace(summary.Timestamp) ? expanded.Timestamp : summary.Timestamp;
                expanded.ClientId = summary.ClientId;
                expanded.SavedRunId = summary.SavedRunId;
                expanded.Warning = string.IsNullOrWhiteSpace(summary.Warning)
                    ? "Saved Rule 18 results were expanded from the stored browser preview to the full result set."
                    : $"{summary.Warning} Full saved results were reloaded from the saved Rule 18 configuration.";

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

        private async Task UpdateStoredSummaryAsync(int runId, Rule18ValidationSummary summary)
        {
            var failRows = summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList();

            await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = summary.ClientId,
                RuleNumber = 18,
                RuleName = "NSFAS Student Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = summary.StudTable,
                DeceasedTable = summary.BridgeTable,
                StudColumn = summary.CrseTable,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(failRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, null, null);
        }

        private static Rule18ValidationSummary CloneSummary(Rule18ValidationSummary summary) => new()
        {
            Success = summary.Success,
            StudRecordCount = summary.StudRecordCount,
            BridgeRecordCount = summary.BridgeRecordCount,
            CrseRecordCount = summary.CrseRecordCount,
            NsfasPopulationCount = summary.NsfasPopulationCount,
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
            Control1FilterCol = summary.Control1FilterCol,
            Control1FilterValue = summary.Control1FilterValue,
            NsfasFilterCol = summary.NsfasFilterCol,
            NsfasFilterValue = summary.NsfasFilterValue,
            FoundationFilterCol = summary.FoundationFilterCol,
            FoundationFilterValue = summary.FoundationFilterValue,
            DistanceFilterCol = summary.DistanceFilterCol,
            DistanceFilterValue = summary.DistanceFilterValue,
            CredJoinCol = summary.CredJoinCol,
            CredCourseCol = summary.CredCourseCol,
            CrseCourseCol = summary.CrseCourseCol,
            CrseNameCol = summary.CrseNameCol,
            TableLinkageText = summary.TableLinkageText,
            RuleModeText = summary.RuleModeText,
            ProcedureSteps = summary.ProcedureSteps.ToList(),
            ClientId = summary.ClientId,
            SavedRunId = summary.SavedRunId,
            ControlSummaries = summary.ControlSummaries.Select(item => new Rule18ControlSummaryItemViewModel
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

        private static Rule18ValidationRowRecord CloneReviewRow(Rule18ValidationRowRecord row) => new()
        {
            ValidationNumber = row.ValidationNumber,
            ControlType = row.ControlType,
            ControlLabel = row.ControlLabel,
            ValidationResult = row.ValidationResult,
            ValidationExplanation = row.ValidationExplanation,
            DisplayValues = new Dictionary<string, string?>(row.DisplayValues, StringComparer.OrdinalIgnoreCase)
        };

        private static Rule18ValidationSummary CreateBrowserPreview(Rule18ValidationSummary summary)
        {
            var perControlLimit = Math.Max(BrowserPreviewRowLimit / 4, 1);
            var previewRows = summary.ReviewRows
                .GroupBy(row => row.ControlType, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => GetControlSort(group.Key))
                .SelectMany(group => group.OrderBy(row => row.ValidationNumber).Take(perControlLimit))
                .Take(BrowserPreviewRowLimit)
                .ToList();

            var clone = CloneSummary(summary);
            clone.DisplayedCount = previewRows.Count;
            clone.IsPreviewOnly = summary.TotalValidated > previewRows.Count;
            clone.PreviewLimit = summary.TotalValidated > previewRows.Count ? previewRows.Count : 0;
            clone.ReviewRows = previewRows;
            return clone;
        }

        private static void ApplyBrowserPreview(Rule18ValidationSummary summary)
        {
            var preview = CreateBrowserPreview(summary);
            summary.DisplayedCount = preview.DisplayedCount;
            summary.IsPreviewOnly = preview.IsPreviewOnly;
            summary.PreviewLimit = preview.PreviewLimit;
            summary.ReviewRows = preview.ReviewRows;
        }

        private static int GetControlSort(string? controlType) => controlType switch
        {
            "Control_1" => 1,
            "Control_2" => 2,
            "Control_3" => 3,
            _ => 99
        };

        private static List<string> BuildProcedureSteps(string studTable, string bridgeTable, string crseTable,
            string credJoinCol, string credCourseCol, string crseCourseCol, string crseNameCol) => new()
        {
            $"Join {studTable}.[{credJoinCol}] to {bridgeTable}.[{credJoinCol}] (Student Qualification Code).",
            $"Join {bridgeTable}.[{credCourseCol}] to {crseTable}.[{crseCourseCol}] (Course Code link).",
            $"Select course name/type from {crseTable}.[{crseNameCol}].",
            "Evaluate the joined STUD, CRED, and CRSE rows using the three control populations.",
            "Return the full matching control result set for Control 1, Control 2, and Control 3."
        };

        private static bool RequestsMatchForPendingSave(Rule18ValidationRequest current, Rule18ValidationRequest pending) =>
            current.ClientId == pending.ClientId &&
            string.Equals(current.StudTable?.Trim(), pending.StudTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.BridgeTable?.Trim(), pending.BridgeTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CrseTable?.Trim(), pending.CrseTable?.Trim(), StringComparison.OrdinalIgnoreCase);

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

        private static void EnrichRule18DisplayValues(Rule18ValidationRowRecord row)
        {
            var values = row.DisplayValues;
            var controlType = ReadValue(values, "Control_Type");
            var nsfasStatus = FormatRule18ColumnValue(ReadValue(values, "NSFAS_Status"));
            var foundationIndicator = FormatRule18ColumnValue(ReadValue(values, "Foundation_Course_Indicator"));
            var attendanceMode = FormatRule18ColumnValue(ReadValue(values, "Attendance_Mode"));

            string controlLabel;
            string validationExplanation;

            switch (controlType)
            {
                case "Control_1":
                    controlLabel = $"NSFAS_Status='{nsfasStatus}' AND Foundation_Course_Indicator='{foundationIndicator}'";
                    validationExplanation = $"NSFAS student (NSFAS_Status='{nsfasStatus}') enrolled in a Foundation course (Foundation_Course_Indicator='{foundationIndicator}').";
                    break;
                case "Control_2":
                    controlLabel = $"NSFAS_Status='{nsfasStatus}' AND Foundation_Course_Indicator='{foundationIndicator}' AND Attendance_Mode='{attendanceMode}'";
                    validationExplanation = $"NSFAS student in a Foundation course studying via Distance (Attendance_Mode='{attendanceMode}').";
                    break;
                case "Control_3":
                    controlLabel = $"NSFAS_Status='{nsfasStatus}' AND Foundation_Course_Indicator<>'{foundationIndicator}' AND Attendance_Mode<>'{attendanceMode}'";
                    validationExplanation = $"NSFAS student NOT in a Foundation course and NOT studying via Distance (Foundation='{foundationIndicator}', Attendance_Mode='{attendanceMode}').";
                    break;
                default:
                    controlLabel = controlType;
                    validationExplanation = "";
                    break;
            }

            row.ControlLabel = controlLabel;
            row.ValidationExplanation = validationExplanation;
            values["Control_Label"] = controlLabel;
            values["Validation_Explanation"] = validationExplanation;
            values["FINAL_RULE_TEXT"] = controlLabel;
        }

        private static string FormatRule18ColumnValue(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "[blank]" : value.Trim();

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

        private static Rule18ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                if (string.IsNullOrWhiteSpace(decoded)) return null;
                return JsonConvert.DeserializeObject<Rule18ValidationSummary>(decoded);
            }
            catch { return null; }
        }
    }
}
