using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 15: validates against the engagement's own uploaded Supabase data instead of a live
    // SQL Server connection, and saves through the shared Postgres-native persistence layer.
    // Ported from the Rule16 pattern. Data source is 3 uploaded tables — the C# property names
    // are misleading (a pre-existing quirk, preserved for compatibility): StudTable actually
    // holds the CRED table, BridgeTable holds the QUAL (approval filter) table, and CrseTable
    // holds the CREG (registration) table. The UI labels these correctly ("CRED Table (base)",
    // "QUAL Table (approval filter)") and the auto-detect/JS layer already compensates for the
    // property-name mismatch — this migration preserves that contract exactly.
    public class Rule15Service : IRule15Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IPendingValidationCacheService _pendingValidationCache;

        public Rule15Service(
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

        public async Task<Rule15TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule15TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule15TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_QUAL", "dbo_qual", "QUAL", "qual"], ["qual"]),
                    AutoBridgeTable = FindFirst(tables, ["dbo_CRED", "dbo_cred", "CRED", "cred"], ["cred"]),
                    AutoCrseTable = FindFirst(tables, ["dbo_CREG", "dbo_creg", "CREG", "creg"], ["creg"])
                };
            }
            catch (Exception ex)
            {
                return new Rule15TableDiscoveryResult { Success = false, Error = ex.Message };
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

        public async Task<Rule15VerifyResult> VerifyTablesAsync(Rule15VerifyRequest request)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);
                var qualApprovedCol = string.IsNullOrWhiteSpace(request.QualApprovedCol) ? "_004" : request.QualApprovedCol;
                var credQualJoinCol = string.IsNullOrWhiteSpace(request.CredQualJoinCol) ? "_001" : request.CredQualJoinCol;
                var credCregJoinCol1 = string.IsNullOrWhiteSpace(request.CredCregJoinCol1) ? "_001" : request.CredCregJoinCol1;
                var credCregJoinCol2 = string.IsNullOrWhiteSpace(request.CredCregJoinCol2) ? "_030" : request.CredCregJoinCol2;
                var cregStudentCol = string.IsNullOrWhiteSpace(request.CregStudentCol) ? "_007" : request.CregStudentCol;

                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, credQualJoinCol, credCregJoinCol1, credCregJoinCol2);
                await ValidateColumnsExistAsync(request.ClientId, request.BridgeTable, credQualJoinCol, qualApprovedCol);
                await ValidateColumnsExistAsync(request.ClientId, request.CrseTable, credCregJoinCol1, credCregJoinCol2, cregStudentCol);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var credTable = request.StudTable;
                var qualTable = request.BridgeTable;
                var cregTable = request.CrseTable;
                const string qualApprovedVal = "A";

                var studCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{credTable}\";");
                var bridgeCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{qualTable}\";");
                var crseCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{cregTable}\";");

                await using var command = connection.CreateCommand();
                command.CommandText = BuildPopulationCountSql(schema, credTable, qualTable, cregTable, qualApprovedCol, qualApprovedVal, credQualJoinCol, credCregJoinCol1, credCregJoinCol2);
                await using var reader = await command.ExecuteReaderAsync();

                var result = new Rule15VerifyResult
                {
                    Success = true,
                    StudRecordCount = studCount,
                    BridgeRecordCount = bridgeCount,
                    CrseRecordCount = crseCount
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
                return new Rule15VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule15ValidationSummary> RunValidationAsync(Rule15ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);
                var qualApprovedCol = string.IsNullOrWhiteSpace(request.QualApprovedCol) ? "_004" : request.QualApprovedCol;
                var credQualJoinCol = string.IsNullOrWhiteSpace(request.CredQualJoinCol) ? "_001" : request.CredQualJoinCol;
                var credCregJoinCol1 = string.IsNullOrWhiteSpace(request.CredCregJoinCol1) ? "_001" : request.CredCregJoinCol1;
                var credCregJoinCol2 = string.IsNullOrWhiteSpace(request.CredCregJoinCol2) ? "_030" : request.CredCregJoinCol2;
                var cregStudentCol = string.IsNullOrWhiteSpace(request.CregStudentCol) ? "_007" : request.CregStudentCol;

                await ValidateColumnsExistAsync(request.ClientId, request.StudTable, credQualJoinCol, credCregJoinCol1, credCregJoinCol2);
                await ValidateColumnsExistAsync(request.ClientId, request.BridgeTable, credQualJoinCol, qualApprovedCol);
                await ValidateColumnsExistAsync(request.ClientId, request.CrseTable, credCregJoinCol1, credCregJoinCol2, cregStudentCol);

                var summary = await AnalyseAsync(request, includeAllReviewRows: true, failRowsOnlyForFullLoad: true);
                if (summary.Success && request.ClientId > 0)
                {
                    try
                    {
                        var summaryToPersist = CloneSummary(summary);
                        summaryToPersist.SavedRunId = null;
                        summary.SavedRunId = await SaveValidationRunAsync(request, summaryToPersist, userEmail, userName);

                        if (!string.IsNullOrWhiteSpace(userEmail))
                            _pendingValidationCache.ClearPending(15, request.ClientId, userEmail!);
                    }
                    catch (Exception ex)
                    {
                        summary.Warning = $"Analysis completed, but the workspace could not be saved automatically: {ex.Message}";
                    }
                }

                if (!summary.SavedRunId.HasValue)
                {
                    if (summary.Success && request.ClientId > 0 && !string.IsNullOrWhiteSpace(userEmail))
                        _pendingValidationCache.StorePending(15, request.ClientId, userEmail!, request, CloneSummary(summary), userName);

                    summary.Warning = string.IsNullOrWhiteSpace(summary.Warning)
                        ? "Rule 15 validation completed. Click Save Workspace to write this validated result to the system database."
                        : summary.Warning;
                }
                else
                {
                    summary.Warning = "The current Rule 15 run has been written to the system database. Click Save Workspace to finalize it for signoff.";
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule15ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule15ValidationSummary> GetExportSummaryAsync(Rule15ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);
            return await AnalyseAsync(request, includeAllReviewRows: true);
        }

        public async Task<int> GetPopulationCountAsync(Rule15ValidationRequest request)
        {
            var summary = await GetExportSummaryAsync(request);
            return summary.TotalValidated;
        }

        public Task<Rule15ValidationSummary?> GetPendingValidationPreviewAsync(int clientId, string reviewerEmail)
        {
            var pending = _pendingValidationCache.GetPending<Rule15ValidationRequest, Rule15ValidationSummary>(15, clientId, reviewerEmail);
            if (pending == null)
                return Task.FromResult<Rule15ValidationSummary?>(null);

            var preview = CloneSummary(pending.Summary);
            preview.SavedRunId = null;
            preview.Warning = "This Rule 15 validation is still pending. Click Save Workspace to write it to the system database.";
            ApplyBrowserPreview(preview);
            return Task.FromResult<Rule15ValidationSummary?>(preview);
        }

        public Task<bool> HasPendingValidationAsync(int clientId, string reviewerEmail)
            => Task.FromResult(_pendingValidationCache.HasPending(15, clientId, reviewerEmail));

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule15WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 15);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (deserializedSummary != null && includeSummary)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule15WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = row.StudTable,
                BridgeTable = row.DeceasedTable,
                CrseTable = row.StudColumn,
                QualApprovedCol = deserializedSummary?.QualApprovedCol ?? "_004",
                QualApprovedVal = deserializedSummary?.QualApprovedVal ?? "A",
                CredQualJoinCol = deserializedSummary?.CredQualJoinCol ?? "_001",
                CredCregJoinCol1 = deserializedSummary?.CredCregJoinCol1 ?? "_001",
                CredCregJoinCol2 = deserializedSummary?.CredCregJoinCol2 ?? "_030",
                CregStudentCol = deserializedSummary?.CregStudentCol ?? "_007",
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

        public async Task<Rule15RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 15);
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

            var review = new Rule15RunReviewViewModel
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

        public async Task<Rule15WorkspaceSaveResult> SaveWorkspaceAsync(Rule15ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);

                if (request.RunId.HasValue && request.RunId.Value > 0)
                {
                    var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                    if (!clientId.HasValue || clientId.Value != request.ClientId)
                        return new Rule15WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

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
                        _pendingValidationCache.ClearPending(15, request.ClientId, reviewerEmail);

                    var currentWorkspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                    return new Rule15WorkspaceSaveResult
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

                var pending = _pendingValidationCache.GetPending<Rule15ValidationRequest, Rule15ValidationSummary>(15, request.ClientId, reviewerEmail);
                if (pending == null)
                    return new Rule15WorkspaceSaveResult { Success = false, Error = "Run Rule 15 first so the current workspace is written to the system database." };

                if (!RequestsMatchForPendingSave(request, pending.Request))
                    return new Rule15WorkspaceSaveResult { Success = false, Error = "Workspace settings changed after validation. Run Rule 15 again before saving." };

                var summaryToSave = CloneSummary(pending.Summary);
                if (summaryToSave.IsPreviewOnly || summaryToSave.ReviewRows.Count < summaryToSave.TotalValidated)
                    summaryToSave = await AnalyseAsync(pending.Request, includeAllReviewRows: true, failRowsOnlyForFullLoad: true);

                summaryToSave.SavedRunId = null;
                var savedRunId = await SaveValidationRunAsync(pending.Request, summaryToSave, reviewerEmail, reviewerName);
                _pendingValidationCache.ClearPending(15, request.ClientId, reviewerEmail);

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
                return new Rule15WorkspaceSaveResult
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
                return new Rule15WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule15WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule15WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                if (!string.IsNullOrWhiteSpace(reviewerEmail))
                    _pendingValidationCache.ClearPending(15, clientId.Value, reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule15WorkspaceSaveResult
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
                return new Rule15WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 15 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 15 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 15 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public Task<string> GenerateSqlAsync(Rule15ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.BridgeTable, request.CrseTable);

            var credTable = request.StudTable;
            var qualTable = request.BridgeTable;
            var cregTable = request.CrseTable;
            var aCol = string.IsNullOrWhiteSpace(request.QualApprovedCol) ? "_004" : request.QualApprovedCol;
            var aVal = request.QualApprovedVal ?? "A";
            var qjCol = string.IsNullOrWhiteSpace(request.CredQualJoinCol) ? "_001" : request.CredQualJoinCol;
            var cj1 = string.IsNullOrWhiteSpace(request.CredCregJoinCol1) ? "_001" : request.CredCregJoinCol1;
            var cj2 = string.IsNullOrWhiteSpace(request.CredCregJoinCol2) ? "_030" : request.CredCregJoinCol2;
            var sCol = string.IsNullOrWhiteSpace(request.CregStudentCol) ? "_007" : request.CregStudentCol;

            var sql = $@"-- HEMIS RULE 15: COURSE CREDENTIALS VALIDATION
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Step 1: Extract 100% CRED population from ""{credTable}""
-- Step 2: Extract approved qualifications from ""{qualTable}"" where {aCol} = '{aVal}'
-- Step 3: Extract CREG registration population from ""{cregTable}""
-- Step 4: Validate CRED -> QUAL -> CREG linkage (PASS when both links found)

{BuildRule15PrepSql("{schema}", credTable, qualTable, cregTable, aCol, aVal, qjCol, cj1, cj2, sCol, true, true, true, true)}

-- Step 5: Full CRED population
SELECT * FROM rule15_cred_population ORDER BY extract_number;

-- Step 6: Full validation result
SELECT * FROM rule15_validation ORDER BY extract_number;

-- Step 7: Exceptions only
SELECT * FROM rule15_validation WHERE validation_result = 'FAIL' ORDER BY extract_number;

-- Step 8: Summary
SELECT
    (SELECT COUNT(1) FROM rule15_approved_qual)   AS approved_qualifications,
    (SELECT COUNT(1) FROM rule15_cred_population) AS total_course_credentials,
    SUM(CASE WHEN validation_result = 'PASS' THEN 1 ELSE 0 END) AS pass_count,
    SUM(CASE WHEN validation_result = 'FAIL' THEN 1 ELSE 0 END) AS fail_count,
    ROUND(SUM(CASE WHEN validation_result = 'FAIL' THEN 1 ELSE 0 END) * 100.0
        / NULLIF(COUNT(*), 0), 2) AS exception_rate_pct
FROM rule15_validation;";

            return Task.FromResult(sql.Trim());
        }

        private async Task<Rule15ValidationSummary> AnalyseAsync(Rule15ValidationRequest request, bool includeAllReviewRows, bool failRowsOnlyForFullLoad = false)
        {
            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            // StudTable = CRED (primary), BridgeTable = QUAL (filter), CrseTable = CREG (registration)
            var credTable = request.StudTable;
            var qualTable = request.BridgeTable;
            var cregTable = request.CrseTable;
            var qualApprovedCol = string.IsNullOrWhiteSpace(request.QualApprovedCol) ? "_004" : request.QualApprovedCol;
            var qualApprovedVal = request.QualApprovedVal ?? "A";
            var credQualJoinCol = string.IsNullOrWhiteSpace(request.CredQualJoinCol) ? "_001" : request.CredQualJoinCol;
            var credCregJoinCol1 = string.IsNullOrWhiteSpace(request.CredCregJoinCol1) ? "_001" : request.CredCregJoinCol1;
            var credCregJoinCol2 = string.IsNullOrWhiteSpace(request.CredCregJoinCol2) ? "_030" : request.CredCregJoinCol2;
            var cregStudentCol = string.IsNullOrWhiteSpace(request.CregStudentCol) ? "_007" : request.CregStudentCol;

            var credCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{credTable}\";");
            var qualCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{qualTable}\";");
            var cregCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{cregTable}\";");

            // These optional display columns don't always exist on an analyst's uploaded CRED/
            // QUAL table — degrade gracefully to NULL rather than failing the whole validation.
            var credColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(request.ClientId, credTable), StringComparer.OrdinalIgnoreCase);
            var qualColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(request.ClientId, qualTable), StringComparer.OrdinalIgnoreCase);
            var hasCredFiller = credColumns.Contains("_065");
            var hasCredCredit1 = credColumns.Contains("_036");
            var hasCredCredit2 = credColumns.Contains("_050");
            var hasQualName = qualColumns.Contains("_003");

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule15PrepSql(schema, credTable, qualTable, cregTable, qualApprovedCol, qualApprovedVal, credQualJoinCol, credCregJoinCol1, credCregJoinCol2, cregStudentCol, hasCredFiller, hasCredCredit1, hasCredCredit2, hasQualName);
                await prepCommand.ExecuteNonQueryAsync();
            }

            int approvedQualCount, credPopCount, passCount, failCount;
            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = BuildRule15CountAfterPrepSql();
                await using var countReader = await countCommand.ExecuteReaderAsync();
                approvedQualCount = credPopCount = passCount = failCount = 0;
                if (await countReader.ReadAsync())
                {
                    approvedQualCount = GetInt(countReader, 0);
                    credPopCount = GetInt(countReader, 1);
                    passCount = GetInt(countReader, 2);
                    failCount = GetInt(countReader, 3);
                }
            }

            List<Rule15ValidationRowRecord> reviewRows;
            if (includeAllReviewRows && failRowsOnlyForFullLoad)
                reviewRows = await LoadControlRowsFromPrepAsync(connection, null, resultFilter: "FAIL");
            else
                reviewRows = await LoadControlRowsFromPrepAsync(connection, includeAllReviewRows ? (int?)null : BrowserPreviewRowLimit);
            reviewRows = NormalizeReviewRows(reviewRows);

            var totalValidated = credPopCount;
            var isPreviewOnly = !includeAllReviewRows && totalValidated > reviewRows.Count;

            var controlSummaries = BuildControlSummaries(credPopCount, passCount, qualApprovedCol, qualApprovedVal, credQualJoinCol, credCregJoinCol1, credCregJoinCol2);

            return new Rule15ValidationSummary
            {
                Success = true,
                StudRecordCount = qualCount,
                BridgeRecordCount = credCount,
                CrseRecordCount = cregCount,
                ApprovedQualificationCount = approvedQualCount,
                ApprovedCredentialCount = credPopCount,
                RegisteredCredentialCount = passCount,
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
                QualApprovedCol = qualApprovedCol,
                QualApprovedVal = qualApprovedVal,
                CredQualJoinCol = credQualJoinCol,
                CredCregJoinCol1 = credCregJoinCol1,
                CredCregJoinCol2 = credCregJoinCol2,
                CregStudentCol = cregStudentCol,
                TableLinkageText = $"{request.StudTable} -> {request.BridgeTable} -> {request.CrseTable}",
                RuleModeText = "100% population testing of CRED rows (CRED -> QUAL approval filter -> CREG registration check)",
                ProcedureSteps = BuildProcedureSteps(request.BridgeTable, request.StudTable, request.CrseTable),
                ClientId = request.ClientId,
                ControlSummaries = controlSummaries,
                ReviewRows = reviewRows,
                Warning = null
            };
        }

        private async Task<int> SaveValidationRunAsync(Rule15ValidationRequest request, Rule15ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 15);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 15,
                RuleName = "Course Credentials Validation",
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

        private static string BuildPopulationCountSql(
            string schema, string credTable, string qualTable, string cregTable,
            string qualApprovedCol, string qualApprovedVal,
            string credQualJoinCol, string credCregJoinCol1, string credCregJoinCol2) => $@"
WITH cred_pop AS (
    SELECT DISTINCT
        CAST(cred.""{credCregJoinCol1}"" AS text) AS link1,
        CAST(cred.""{credCregJoinCol2}"" AS text) AS link2,
        CAST(cred.""{credQualJoinCol}""  AS text) AS qual_key
    FROM ""{schema}"".""{credTable}"" cred
    WHERE cred.""{credCregJoinCol1}"" IS NOT NULL AND cred.""{credCregJoinCol2}"" IS NOT NULL
),
appr_qual AS (
    SELECT DISTINCT CAST(qual.""{credQualJoinCol}"" AS text) AS link
    FROM ""{schema}"".""{qualTable}"" qual
    WHERE COALESCE(CAST(qual.""{qualApprovedCol}"" AS text), '') = '{EscapeSqlString(qualApprovedVal)}'
),
creg_smp AS (
    SELECT CAST(creg.""{credCregJoinCol1}"" AS text) AS link1,
           CAST(creg.""{credCregJoinCol2}"" AS text) AS link2
    FROM ""{schema}"".""{cregTable}"" creg
    WHERE creg.""{credCregJoinCol1}"" IS NOT NULL AND creg.""{credCregJoinCol2}"" IS NOT NULL
    GROUP BY creg.""{credCregJoinCol1}"", creg.""{credCregJoinCol2}""
)
SELECT
    (SELECT COUNT(*) FROM appr_qual) AS approved_qualification_count,
    COUNT(1) AS approved_credential_count,
    SUM(CASE WHEN cs.link1 IS NOT NULL THEN 1 ELSE 0 END) AS registered_credential_count
FROM cred_pop cp
INNER JOIN appr_qual aq ON aq.link = cp.qual_key
LEFT JOIN creg_smp cs ON cs.link1 = cp.link1 AND cs.link2 = cp.link2;";

        private static string BuildRule15PrepSql(
            string schema, string credTable, string qualTable, string cregTable,
            string qualApprovedCol, string qualApprovedVal,
            string credQualJoinCol, string credCregJoinCol1, string credCregJoinCol2, string cregStudentCol,
            bool hasCredFiller, bool hasCredCredit1, bool hasCredCredit2, bool hasQualName)
        {
            var av = EscapeSqlString(qualApprovedVal);
            var ctrlLabel = $"CRED.{credCregJoinCol1} AND QUAL.{qualApprovedCol}=''{qualApprovedVal}'' AND CRED.{credCregJoinCol1}=CREG.{credCregJoinCol1} AND CRED.{credCregJoinCol2}=CREG.{credCregJoinCol2}";
            var credFillerSel = hasCredFiller ? @"CAST(cred.""_065"" AS text)" : "NULL::text";
            var credCredit1Sel = hasCredCredit1 ? @"CAST(cred.""_036"" AS text)" : "NULL::text";
            var credCredit2Sel = hasCredCredit2 ? @"CAST(cred.""_050"" AS text)" : "NULL::text";
            var qualNameSel = hasQualName ? @"CAST(qual.""_003"" AS text)" : "NULL::text";

            return $@"
DROP TABLE IF EXISTS rule15_cred_population;
DROP TABLE IF EXISTS rule15_approved_qual;
DROP TABLE IF EXISTS rule15_creg_population;
DROP TABLE IF EXISTS rule15_validation;

-- STEP 1: Extract 100% CRED population
CREATE TEMP TABLE rule15_cred_population AS
SELECT DISTINCT
    ROW_NUMBER() OVER (ORDER BY
        UPPER(TRIM(CAST(cred.""{credCregJoinCol1}"" AS text))),
        UPPER(TRIM(CAST(cred.""{credCregJoinCol2}"" AS text)))
    ) AS extract_number,
    UPPER(TRIM(CAST(cred.""{credCregJoinCol1}"" AS text))) AS qualification_code,
    UPPER(TRIM(CAST(cred.""{credCregJoinCol2}"" AS text))) AS course_code,
    UPPER(TRIM(CAST(cred.""{credQualJoinCol}""  AS text))) AS cred_qual_key,
    {credFillerSel} AS filler1,
    {credCredit1Sel} AS course_level_credit_value,
    {credCredit2Sel} AS completed_research_course_credit_value
FROM ""{schema}"".""{credTable}"" cred
WHERE cred.""{credCregJoinCol1}"" IS NOT NULL
  AND cred.""{credCregJoinCol2}"" IS NOT NULL
  AND TRIM(CAST(cred.""{credCregJoinCol1}"" AS text)) <> ''
  AND TRIM(CAST(cred.""{credCregJoinCol2}"" AS text)) <> '';

CREATE INDEX ON rule15_cred_population (qualification_code, course_code);
ANALYZE rule15_cred_population;

-- STEP 2: Extract approved QUAL population
CREATE TEMP TABLE rule15_approved_qual AS
SELECT DISTINCT
    UPPER(TRIM(CAST(qual.""{credQualJoinCol}"" AS text))) AS qual_join_key,
    {qualNameSel} AS qualification_name_designator,
    UPPER(TRIM(CAST(qual.""{qualApprovedCol}"" AS text))) AS approval_status
FROM ""{schema}"".""{qualTable}"" qual
WHERE UPPER(TRIM(CAST(qual.""{qualApprovedCol}"" AS text))) = '{av}'
  AND qual.""{credQualJoinCol}"" IS NOT NULL
  AND TRIM(CAST(qual.""{credQualJoinCol}"" AS text)) <> '';

CREATE INDEX ON rule15_approved_qual (qual_join_key);
ANALYZE rule15_approved_qual;

-- STEP 3: Extract CREG registration population
CREATE TEMP TABLE rule15_creg_population AS
SELECT
    UPPER(TRIM(CAST(creg.""{credCregJoinCol1}"" AS text))) AS creg_join_key1,
    UPPER(TRIM(CAST(creg.""{credCregJoinCol2}"" AS text))) AS creg_join_key2,
    MIN(CAST(creg.""{cregStudentCol}"" AS text)) AS first_student_number,
    COUNT(DISTINCT CAST(creg.""{cregStudentCol}"" AS text)) AS registered_student_count
FROM ""{schema}"".""{cregTable}"" creg
WHERE creg.""{credCregJoinCol1}"" IS NOT NULL
  AND creg.""{credCregJoinCol2}"" IS NOT NULL
  AND TRIM(CAST(creg.""{credCregJoinCol1}"" AS text)) <> ''
  AND TRIM(CAST(creg.""{credCregJoinCol2}"" AS text)) <> ''
GROUP BY
    UPPER(TRIM(CAST(creg.""{credCregJoinCol1}"" AS text))),
    UPPER(TRIM(CAST(creg.""{credCregJoinCol2}"" AS text)));

CREATE INDEX ON rule15_creg_population (creg_join_key1, creg_join_key2);
ANALYZE rule15_creg_population;

-- STEP 4: Validate CRED -> QUAL -> CREG
CREATE TEMP TABLE rule15_validation AS
SELECT
    cp.extract_number,
    1 AS control_sort,
    'Control_1' AS control_type,
    'CONTROL 1: {ctrlLabel}' AS control_label,
    cp.qualification_code,
    cp.course_code,
    aq.qualification_name_designator,
    aq.approval_status,
    cp.filler1,
    cp.course_level_credit_value,
    cp.completed_research_course_credit_value,
    creg.first_student_number,
    creg.registered_student_count,
    CASE WHEN creg.creg_join_key1 IS NOT NULL THEN 'FOUND' ELSE 'NOT FOUND' END AS creg_match,
    CASE
        WHEN aq.qual_join_key IS NULL    THEN 'FAIL'
        WHEN creg.creg_join_key1 IS NULL THEN 'FAIL'
        ELSE 'PASS'
    END AS validation_result,
    CASE
        WHEN aq.qual_join_key IS NULL
            THEN 'Qualification code in CRED is not approved or does not exist in QUAL.'
        WHEN creg.creg_join_key1 IS NULL
            THEN 'Credential qualification/course combination does not exist in CREG.'
        ELSE 'Approved qualification and matching CREG registration found.'
    END AS validation_reason
FROM rule15_cred_population cp
LEFT JOIN rule15_approved_qual aq ON aq.qual_join_key = cp.cred_qual_key
LEFT JOIN rule15_creg_population creg
    ON creg.creg_join_key1 = cp.qualification_code
   AND creg.creg_join_key2 = cp.course_code;

ANALYZE rule15_validation;";
        }

        private static string BuildRule15CountAfterPrepSql() => @"
SELECT
    (SELECT COUNT(1) FROM rule15_approved_qual)   AS approved_qual_count,
    (SELECT COUNT(1) FROM rule15_cred_population) AS cred_pop_count,
    SUM(CASE WHEN validation_result = 'PASS' THEN 1 ELSE 0 END) AS pass_count,
    SUM(CASE WHEN validation_result = 'FAIL' THEN 1 ELSE 0 END) AS fail_count
FROM rule15_validation;";

        private static readonly Dictionary<string, string> DisplayNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["extract_number"] = "Extract_Number",
            ["control_sort"] = "Control_Sort",
            ["control_type"] = "Control_Type",
            ["control_label"] = "Control_Label",
            ["qualification_code"] = "Qualification_Code",
            ["course_code"] = "Course_Code",
            ["qualification_name_designator"] = "Qualification_Name_Designator",
            ["approval_status"] = "Approval_Status",
            ["filler1"] = "Filler1",
            ["course_level_credit_value"] = "Course_Level_Credit_Value",
            ["completed_research_course_credit_value"] = "Completed_Research_Course_Credit_Value",
            ["first_student_number"] = "First_Student_Number",
            ["registered_student_count"] = "Registered_Student_Count",
            ["creg_match"] = "CREG__MATCH",
            ["validation_result"] = "Validation_Result",
            ["validation_reason"] = "Validation_Reason"
        };

        private static string ToPascalDisplayName(string columnName) =>
            DisplayNameMap.TryGetValue(columnName, out var mapped) ? mapped : columnName;

        private static async Task<List<Rule15ValidationRowRecord>> LoadControlRowsFromPrepAsync(NpgsqlConnection connection, int? maxRows, string? resultFilter = null)
        {
            var limitClause = maxRows.HasValue && maxRows.Value > 0 ? $"LIMIT {maxRows.Value}" : "";
            var whereClause = resultFilter == "FAIL" ? "WHERE validation_result = 'FAIL'" : "";
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM rule15_validation {whereClause} ORDER BY extract_number {limitClause};";

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule15ValidationRowRecord>();
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

                rows.Add(new Rule15ValidationRowRecord
                {
                    ValidationNumber = rows.Count + 1,
                    ControlType = ReadValue(displayValues, "Control_Type"),
                    ControlLabel = ReadValue(displayValues, "Control_Label"),
                    ValidationResult = ReadValue(displayValues, "Validation_Result"),
                    ValidationExplanation = ReadValue(displayValues, "Validation_Reason"),
                    DisplayValues = displayValues
                });

                EnrichRule15DisplayValues(rows[^1]);
            }
            return rows;
        }

        private static List<Rule15ControlSummaryItemViewModel> BuildControlSummaries(
            int approvedCredentialCount, int registeredCredentialCount,
            string qualApprovedCol, string qualApprovedVal,
            string credQualJoinCol, string credCregJoinCol1, string credCregJoinCol2)
        {
            return new List<Rule15ControlSummaryItemViewModel>
            {
                BuildControlSummary(
                    "Control_1",
                    "Control 1",
                    $"CRED.{credCregJoinCol1} AND QUAL.{qualApprovedCol}='{qualApprovedVal}' AND CRED.{credCregJoinCol1}=CREG.{credCregJoinCol1} AND CRED.{credCregJoinCol2}=CREG.{credCregJoinCol2}",
                    approvedCredentialCount,
                    registeredCredentialCount)
            };
        }

        private static Rule15ControlSummaryItemViewModel BuildControlSummary(string controlType, string controlLabel, string criteriaText, int totalCount, int passCount)
        {
            var failCount = Math.Max(totalCount - passCount, 0);
            return new Rule15ControlSummaryItemViewModel
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

        private static List<Rule15ValidationRowRecord> NormalizeReviewRows(IEnumerable<Rule15ValidationRowRecord>? rows) =>
            (rows ?? Enumerable.Empty<Rule15ValidationRowRecord>())
                .Select((row, index) => { row.ValidationNumber = index + 1; return row; })
                .ToList();

        private async Task<Rule15ValidationSummary> ExpandSavedSummaryIfNeededAsync(Rule15ValidationSummary summary, int clientId)
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
                var expanded = await AnalyseAsync(new Rule15ValidationRequest
                {
                    ClientId = clientId,
                    RunId = summary.SavedRunId,
                    StudTable = summary.StudTable,
                    BridgeTable = summary.BridgeTable,
                    CrseTable = summary.CrseTable,
                    QualApprovedCol = summary.QualApprovedCol,
                    QualApprovedVal = summary.QualApprovedVal,
                    CredQualJoinCol = summary.CredQualJoinCol,
                    CredCregJoinCol1 = summary.CredCregJoinCol1,
                    CredCregJoinCol2 = summary.CredCregJoinCol2,
                    CregStudentCol = summary.CregStudentCol
                }, includeAllReviewRows: true);

                expanded.Timestamp = string.IsNullOrWhiteSpace(summary.Timestamp) ? expanded.Timestamp : summary.Timestamp;
                expanded.ClientId = summary.ClientId;
                expanded.SavedRunId = summary.SavedRunId;
                expanded.Warning = string.IsNullOrWhiteSpace(summary.Warning)
                    ? "Saved Rule 15 results were expanded from the stored browser preview to the full result set."
                    : $"{summary.Warning} Full saved results were reloaded from the saved Rule 15 configuration.";

                return expanded;
            }
            catch
            {
                return summary;
            }
        }

        private static Rule15ValidationSummary CloneSummary(Rule15ValidationSummary summary) => new()
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
            QualApprovedCol = summary.QualApprovedCol,
            QualApprovedVal = summary.QualApprovedVal,
            CredQualJoinCol = summary.CredQualJoinCol,
            CredCregJoinCol1 = summary.CredCregJoinCol1,
            CredCregJoinCol2 = summary.CredCregJoinCol2,
            CregStudentCol = summary.CregStudentCol,
            TableLinkageText = summary.TableLinkageText,
            RuleModeText = summary.RuleModeText,
            ProcedureSteps = summary.ProcedureSteps.ToList(),
            ClientId = summary.ClientId,
            SavedRunId = summary.SavedRunId,
            ControlSummaries = summary.ControlSummaries.Select(item => new Rule15ControlSummaryItemViewModel
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

        private static Rule15ValidationRowRecord CloneReviewRow(Rule15ValidationRowRecord row) => new()
        {
            ValidationNumber = row.ValidationNumber,
            ControlType = row.ControlType,
            ControlLabel = row.ControlLabel,
            ValidationResult = row.ValidationResult,
            ValidationExplanation = row.ValidationExplanation,
            DisplayValues = new Dictionary<string, string?>(row.DisplayValues, StringComparer.OrdinalIgnoreCase)
        };

        private static Rule15ValidationSummary CreateBrowserPreview(Rule15ValidationSummary summary)
        {
            var perControlLimit = Math.Max(BrowserPreviewRowLimit / 3, 1);
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

        private static void ApplyBrowserPreview(Rule15ValidationSummary summary)
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
            _ => 99
        };

        private static List<string> BuildProcedureSteps(string qualTable, string credTable, string cregTable) => new()
        {
            $"Filter {qualTable} to approved qualifications where the configured approval column matches the configured value.",
            $"Join approved qualifications to {credTable} on the configured join column to retrieve every credential course row.",
            $"For each approved credential row, test whether a matching {cregTable} row exists on the configured join columns.",
            "Mark rows PASS when a matching registration exists and FAIL when no registration exists.",
            "Return the full approved-credential population; no sampling is applied."
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
                throw new InvalidOperationException("CRED table is required.");
            if (string.IsNullOrWhiteSpace(bridgeTable))
                throw new InvalidOperationException("QUAL table is required.");
            if (string.IsNullOrWhiteSpace(crseTable))
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

        private static void EnrichRule15DisplayValues(Rule15ValidationRowRecord row)
        {
            var values = row.DisplayValues;
            var isPass = string.Equals(ReadValue(values, "Validation_Result"), "PASS", StringComparison.OrdinalIgnoreCase);
            var approvalStatus = ReadValue(values, "Approval_Status");
            var qualCode = FormatRule15ColumnValue(ReadValue(values, "Qualification_Code"));
            var courseCode = FormatRule15ColumnValue(ReadValue(values, "Course_Code"));
            var firstStudent = FormatRule15ColumnValue(ReadValue(values, "First_Student_Number"));
            var isQualFound = !string.IsNullOrWhiteSpace(approvalStatus);

            string qualCriteriaMessage, registrationMessage, validationExplanation, finalResultMessage;
            if (isPass)
            {
                qualCriteriaMessage = $"'{approvalStatus}': FOUND";
                registrationMessage = $"FOUND — QualCode='{qualCode}', CourseCode='{courseCode}', Student='{firstStudent}'.";
                validationExplanation = $"QUAL Approval_Status='{approvalStatus}': FOUND | CREG: FOUND ('{qualCode}','{courseCode}') | Student: '{firstStudent}'";
                finalResultMessage = "Passed: CRED credential found in approved QUAL and matched in CREG.";
            }
            else if (!isQualFound)
            {
                qualCriteriaMessage = "NOT FOUND in approved QUAL";
                registrationMessage = "No CREG check — qual not approved.";
                validationExplanation = $"QUAL approval: NOT FOUND — CRED Qualification_Code '{qualCode}' not in approved QUAL.";
                finalResultMessage = "Failed: CRED qualification not found in approved QUAL.";
            }
            else
            {
                qualCriteriaMessage = $"'{approvalStatus}': FOUND";
                registrationMessage = $"NOT FOUND — no CREG match for QualCode='{qualCode}', CourseCode='{courseCode}'.";
                validationExplanation = $"QUAL Approval_Status='{approvalStatus}': FOUND | CREG: NOT FOUND for ('{qualCode}','{courseCode}')";
                finalResultMessage = "Failed: approved qualification found but no matching CREG registration.";
            }

            values["QUAL_CRITERIA_MESSAGE"] = qualCriteriaMessage;
            values["REGISTRATION_MESSAGE"] = registrationMessage;
            values["Validation_Explanation"] = validationExplanation;
            values["FINAL_RESULT_MESSAGE"] = finalResultMessage;
            row.ValidationExplanation = validationExplanation;
        }

        private static string FormatRule15ColumnValue(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "[blank]" : value.Trim();

        private static bool RequestsMatchForPendingSave(Rule15ValidationRequest current, Rule15ValidationRequest pending) =>
            current.ClientId == pending.ClientId &&
            string.Equals(current.StudTable?.Trim(), pending.StudTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.BridgeTable?.Trim(), pending.BridgeTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CrseTable?.Trim(), pending.CrseTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.QualApprovedCol?.Trim(), pending.QualApprovedCol?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.QualApprovedVal?.Trim(), pending.QualApprovedVal?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CredQualJoinCol?.Trim(), pending.CredQualJoinCol?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CredCregJoinCol1?.Trim(), pending.CredCregJoinCol1?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CredCregJoinCol2?.Trim(), pending.CredCregJoinCol2?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CregStudentCol?.Trim(), pending.CregStudentCol?.Trim(), StringComparison.OrdinalIgnoreCase);

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

        private static Rule15ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                if (string.IsNullOrWhiteSpace(decoded)) return null;
                return JsonConvert.DeserializeObject<Rule15ValidationSummary>(decoded);
            }
            catch { return null; }
        }
    }
}
