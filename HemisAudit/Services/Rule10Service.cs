using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // HEMIS Rules 1-10 ("Integrity Scope" family): validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection, and saves results through
    // the shared Postgres-native persistence layer in SystemDatabaseService. Ported from the
    // Rule17 pilot pattern; see Rule17Service.cs for the reference shape.
    public class Rule10Service : IRule10Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly IPendingValidationCacheService _pendingValidationCache;
        private readonly UserManager<ApplicationUser> _userManager;

        private sealed record JoinDatasetTemplate(string DatasetCode, string DatasetLabel, string DefaultTableName, string[] KeyColumns, string[] CompositeKeyFields);

        private static readonly IReadOnlyList<JoinDatasetTemplate> Rule10JoinDatasets =
        [
            new("dbo_CREG", "dbo_CREG", "dbo_CREG", ["_030", "_001"], ["_001+_030"]),
            new("dbo_CRSE", "dbo_CRSE", "dbo_CRSE", ["_030", "_058"], []),
            new("dbo_STUD", "dbo_STUD", "dbo_STUD", ["_007", "_001"], ["_007+_001"]),
            new("dbo_QUAL", "dbo_QUAL", "dbo_QUAL", ["_001"], []),
            new("dbo_CESM", "dbo_CESM", "dbo_CESM", ["_001"], []),
            new("dbo_CRED", "dbo_CRED", "dbo_CRED", ["_030", "_001"], ["_001+_030"]),
            new("Cenus_dates", "Cenus_dates", "Cenus_dates", ["BLOCK_CODE"], []),
            new("Census_dates_design", "Census_dates_design", "Census_dates_design", ["BLOCK_CODE"], []),
            new("2024H16STUD", "2024H16STUD", "2024H16STUD", ["STUDNUM", "QUALCODE"], ["STUDNUM+QUALCODE"]),
            new("Prod_STUD", "Prod_STUD", "Prod_STUD", ["IAGSTNO", "IAGQUAL"], ["IAGSTNO+IAGQUAL"]),
            new("Prod_QUAL", "Prod_QUAL", "Prod_QUAL", ["IAIQUAL"], []),
            new("2024H16QUAL", "2024H16QUAL", "2024H16QUAL", ["QUALCODE"], []),
            new("Prod_CRSE", "Prod_CRSE", "Prod_CRSE", ["IALSUBJ"], []),
            new("2024H16CRSE", "2024H16CRSE", "2024H16CRSE", ["CRSECODE"], []),
            new("Employee_file", "Employee_file", "Employee_file", ["Personnel_Number"], []),
            new("dbo_PROF", "dbo_PROF", "dbo_PROF", ["_037"], []),
            new("Deceased_Students", "Deceased_Students", "Deceased_Students", ["STUDENT_NUMBER"], [])
        ];

        public Rule10Service(
            IConfiguration configuration,
            IEngagementDatasetService datasets,
            ISystemDatabaseService systemDb,
            IPendingValidationCacheService pendingValidationCache,
            UserManager<ApplicationUser> userManager)
        {
            _configuration = configuration;
            _datasets = datasets;
            _systemDb = systemDb;
            _pendingValidationCache = pendingValidationCache;
            _userManager = userManager;
        }

        // ── Engagement data source (uploaded tables, not a live SQL Server) ────────────────

        public async Task<Rule10TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule10TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule10TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "QUAL"], ["qual"]),
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "STUD"], ["stud"]),
                    AutoCregTable = FindFirst(tables, ["dbo_CREG", "CREG"], ["creg"]),
                    AutoCrseTable = FindFirst(tables, ["dbo_CRSE", "CRSE"], ["crse"])
                };
            }
            catch (Exception ex)
            {
                return new Rule10TableDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule10ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                return new Rule10ColumnSelectionResult
                {
                    Success = true,
                    Columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName)
                };
            }
            catch (Exception ex)
            {
                return new Rule10ColumnSelectionResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule10VerifyResult> VerifyTablesAsync(Rule10VerifyRequest request)
        {
            try
            {
                ValidateRequest(request);

                if (request.RuleNumber == 10)
                {
                    var joinDatasets = await VerifyRule10JoinDatasetsAsync(request.ClientId, request.Rule10JoinConfigJson, throwOnMissingColumns: false);
                    return new Rule10VerifyResult
                    {
                        Success = true,
                        JoinDatasets = joinDatasets,
                        Error = joinDatasets.Any(item => item.MissingColumns.Count > 0)
                            ? "One or more documented key columns are missing from the selected tables."
                            : null
                    };
                }

                await EnsureColumnsExistAsync(new Rule10ValidationRequest
                {
                    ClientId = request.ClientId,
                    RuleNumber = request.RuleNumber,
                    QualTable = request.QualTable,
                    StudTable = request.StudTable,
                    CregTable = request.CregTable,
                    CrseTable = request.CrseTable,
                    QualColumn = request.QualColumn,
                    StudColumn = request.StudColumn,
                    CregColumn = request.CregColumn,
                    CrseColumn = request.CrseColumn,
                    RuleParameterJson = request.RuleParameterJson,
                    Rule10JoinConfigJson = request.Rule10JoinConfigJson
                });

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                return new Rule10VerifyResult
                {
                    Success = true,
                    // Only count the tables this specific rule actually needs — the other
                    // table pickers stay hidden and empty in the UI for rules that don't need
                    // them, and querying FROM "schema"."" is a Postgres syntax error, not a
                    // harmless no-op.
                    QualRecordCount = RequiresTable(request.RuleNumber, "QUAL") ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.QualTable}\";") : 0,
                    StudRecordCount = RequiresTable(request.RuleNumber, "STUD") ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";") : 0,
                    CregRecordCount = RequiresTable(request.RuleNumber, "CREG") ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CregTable}\";") : 0,
                    CrseRecordCount = RequiresTable(request.RuleNumber, "CRSE") ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CrseTable}\";") : 0
                };
            }
            catch (Exception ex)
            {
                return new Rule10VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule10ValidationSummary> RunValidationAsync(Rule10ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request);
                var rule = IntegrityRuleCatalog.Get(request.RuleNumber);
                await EnsureColumnsExistAsync(request);

                var browserSummary = await AnalyseAsync(request, includeAllReviewRows: false);
                if (browserSummary.Success && request.ClientId > 0)
                {
                    try
                    {
                        browserSummary.SavedRunId = await SaveValidationRunAsync(
                            CloneValidationRequest(request),
                            CloneSummary(browserSummary),
                            userEmail,
                            userName);

                        if (!string.IsNullOrWhiteSpace(userEmail))
                            _pendingValidationCache.ClearPending(request.RuleNumber, request.ClientId, userEmail!);
                    }
                    catch (Exception ex)
                    {
                        browserSummary.Warning = $"Analysis completed, but the workspace could not be saved automatically: {ex.Message}";
                    }
                }

                if (!browserSummary.SavedRunId.HasValue)
                {
                    if (browserSummary.Success && request.ClientId > 0 && !string.IsNullOrWhiteSpace(userEmail))
                        _pendingValidationCache.StorePending(request.RuleNumber, request.ClientId, userEmail!, request, CloneSummary(browserSummary), userName);

                    browserSummary.Warning = string.IsNullOrWhiteSpace(browserSummary.Warning)
                        ? $"Counts reflect the full {rule.RuleLabel} integrity result set. Browser exception rows are limited for performance."
                        : browserSummary.Warning;
                }
                else
                {
                    browserSummary.Warning = $"The current {rule.RuleLabel} integrity run has been written to the system database. Click Save Workspace to finalize it for signoff.";
                }

                ApplyBrowserPreview(browserSummary);
                return browserSummary;
            }
            catch (Exception ex)
            {
                return new Rule10ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule10ValidationSummary> GetExportSummaryAsync(Rule10ValidationRequest request)
        {
            ValidateRequest(request);
            await EnsureColumnsExistAsync(request);
            return await AnalyseAsync(request, includeAllReviewRows: true);
        }

        public async Task<int> GetPopulationCountAsync(Rule10ValidationRequest request)
        {
            var summary = await GetExportSummaryAsync(request);
            return summary.TotalValidated;
        }

        public Task<Rule10ValidationSummary?> GetPendingValidationPreviewAsync(int ruleNumber, int clientId, string reviewerEmail)
        {
            var pending = _pendingValidationCache.GetPending<Rule10ValidationRequest, Rule10ValidationSummary>(ruleNumber, clientId, reviewerEmail);
            if (pending == null)
                return Task.FromResult<Rule10ValidationSummary?>(null);

            var preview = CloneSummary(pending.Summary);
            preview.SavedRunId = null;
            preview.Warning = $"This {IntegrityRuleCatalog.Get(ruleNumber).RuleLabel} integrity validation is still pending. Click Save Workspace to write it to the system database.";
            ApplyBrowserPreview(preview);
            return Task.FromResult<Rule10ValidationSummary?>(preview);
        }

        public Task<bool> HasPendingValidationAsync(int ruleNumber, int clientId, string reviewerEmail)
            => Task.FromResult(_pendingValidationCache.HasPending(ruleNumber, clientId, reviewerEmail));

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule10WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int ruleNumber, int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, ruleNumber);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule10WorkspaceStateViewModel
            {
                RuleNumber = ruleNumber,
                ClientId = row.ClientId,
                RunId = row.RunId,
                QualTable = deserializedSummary?.QualTable ?? (string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_QUAL" : row.StudTable),
                StudTable = deserializedSummary?.StudTable ?? (string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_STUD" : row.DeceasedTable),
                CregTable = deserializedSummary?.CregTable ?? (string.IsNullOrWhiteSpace(row.StudColumn) ? "dbo_CREG" : row.StudColumn),
                CrseTable = deserializedSummary?.CrseTable ?? (string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "dbo_CRSE" : row.DeceasedColumn),
                QualColumn = deserializedSummary?.QualColumn ?? GetDefaultQualColumn(ruleNumber),
                StudColumn = deserializedSummary?.StudColumn ?? GetDefaultStudColumn(ruleNumber),
                CregColumn = deserializedSummary?.CregColumn ?? GetDefaultCregColumn(ruleNumber),
                CrseColumn = deserializedSummary?.CrseColumn ?? GetDefaultCrseColumn(ruleNumber),
                RuleParameterJson = deserializedSummary?.RuleParameterJson ?? GetDefaultRuleParameterJson(ruleNumber),
                Rule10JoinConfigJson = deserializedSummary?.Rule10JoinConfigJson ?? "",
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
            workspace.IsWorkspaceSaved = await _systemDb.IsWorkspaceSavedAsync(workspace.RunId.Value);

            if (workspace.Summary != null)
                workspace.Summary.SavedRunId = workspace.RunId;

            return workspace;
        }

        public async Task<Rule10RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var rule = IntegrityRuleCatalog.Get(row.RuleNumber);
            summary.RuleNumber = row.RuleNumber;
            summary.RuleLabel = rule.RuleLabel;
            summary.RuleTitle = rule.RuleTitle;
            summary.ClientId = row.ClientId;
            if (summary.SavedRunId.GetValueOrDefault() <= 0)
                summary.SavedRunId = runId;

            if (includeFullResults)
            {
                summary.DisplayedCount = summary.ReviewRows.Count;
            }
            else
            {
                ApplyBrowserPreview(summary);
            }

            var review = new Rule10RunReviewViewModel
            {
                RunId = row.RunId,
                RuleNumber = row.RuleNumber,
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

        public async Task<Rule10WorkspaceSaveResult> SaveWorkspaceAsync(Rule10ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                ValidateRequiredTables(request);

                if (request.RunId is null || request.RunId <= 0)
                {
                    return new Rule10WorkspaceSaveResult { Success = false, Error = "Run the validation first so the workspace can be saved." };
                }

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                {
                    return new Rule10WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };
                }

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.QualTable,
                    DeceasedTable = request.StudTable,
                    StudColumn = request.CregTable,
                    DeceasedColumn = request.CrseTable
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.RuleNumber, request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule10WorkspaceSaveResult
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
                return new Rule10WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule10WorkspaceSaveResult> BeginWorkspaceEditAsync(int ruleNumber, int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                {
                    return new Rule10WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };
                }

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(ruleNumber, clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule10WorkspaceSaveResult
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
                return new Rule10WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
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

        public Task<string> GenerateSqlAsync(Rule10ValidationRequest request)
        {
            var rule = IntegrityRuleCatalog.Get(request.RuleNumber);

            if (request.RuleNumber == 10)
            {
                var lines = new List<string>
                {
                    $"-- HEMIS {rule.RuleLabel.ToUpperInvariant()}: {rule.RuleTitle.ToUpperInvariant()}",
                    "-- Rule mode: documented join-key existence verification",
                    "-- Source: this engagement's own uploaded tables (schema engagement_{ClientId}), not a live SQL Server.",
                    ""
                };

                foreach (var dataset in ResolveRule10JoinDatasets(request.Rule10JoinConfigJson))
                {
                    var inList = string.Join(", ", dataset.KeyColumns.Select(column => $"'{column.Replace("'", "''")}'"));
                    lines.Add($"-- {dataset.DatasetLabel}: key fields {string.Join(", ", dataset.KeyColumns.Concat(dataset.CompositeKeyFields))}");
                    lines.Add($@"SELECT column_name
FROM information_schema.columns
WHERE table_schema = '{{schema}}' AND table_name = '{dataset.TableName.Replace("'", "''")}'
  AND column_name IN ({inList})
ORDER BY column_name;");
                    lines.Add("");
                }

                return Task.FromResult(string.Join(Environment.NewLine, lines).Trim());
            }

            ValidateRequiredTables(request);
            var definitions = BuildIntegrityRuleDefinitions(request, "{schema}");
            var definition = definitions.FirstOrDefault();
            if (definition == null)
                throw new InvalidOperationException($"Integrity rule {request.RuleNumber} is not supported.");

            var header = $@"-- ============================================================================
-- HEMIS {rule.RuleLabel.ToUpperInvariant()}: {rule.RuleTitle.ToUpperInvariant()}
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Criteria: {definition.CriteriaText}
-- ============================================================================

";
            var body = string.IsNullOrWhiteSpace(definition.PrepSql)
                ? definition.ReviewSql
                : $"{definition.PrepSql}\n\n{definition.ReviewSql}";

            return Task.FromResult((header + body).Trim());
        }

        // ── Analysis (Postgres, against the engagement's uploaded tables) ─────────────────

        private async Task<Rule10ValidationSummary> AnalyseAsync(Rule10ValidationRequest request, bool includeAllReviewRows)
        {
            var rule = IntegrityRuleCatalog.Get(request.RuleNumber);

            if (request.RuleNumber == 10)
                return await AnalyseRule10JoinVerificationAsync(request);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var definitions = BuildIntegrityRuleDefinitions(request, schema);
            var reviewRows = new List<Rule10ValidationRowRecord>();
            var summaries = new List<Rule10ControlSummaryItemViewModel>();
            var detailLimit = includeAllReviewRows ? (int?)null : BrowserPreviewRowLimit;
            var totalValidatedRows = 0;
            var totalFailedRows = 0;

            foreach (var definition in definitions)
            {
                if (!string.IsNullOrWhiteSpace(definition.PrepSql))
                {
                    await ExecuteNonQueryAsync(connection, definition.PrepSql);
                }

                var errorCount = await CountAsync(connection, definition.CountSql);
                var validatedCount = await CountAsync(connection, definition.TotalSql);
                var passCount = Math.Max(validatedCount - errorCount, 0);
                summaries.Add(new Rule10ControlSummaryItemViewModel
                {
                    RuleId = definition.RuleId,
                    ControlType = $"Rule_{definition.RuleId}",
                    ControlLabel = $"Rule {definition.RuleId}",
                    CriteriaText = definition.CriteriaText,
                    TableName = definition.TableName,
                    Severity = definition.Severity,
                    ErrorCount = errorCount,
                    RequestedCount = validatedCount,
                    AvailableCount = validatedCount,
                    AchievedCount = passCount,
                    TotalCount = validatedCount,
                    PassCount = passCount,
                    FailCount = errorCount,
                    Status = errorCount == 0 ? "PASS" : "FAIL"
                });

                totalValidatedRows += validatedCount;
                totalFailedRows += errorCount;
                reviewRows.AddRange(await LoadValidationRowsAsync(connection, definition, detailLimit));
            }

            reviewRows = reviewRows
                .OrderBy(row => row.RuleId)
                .ThenBy(row => row.ValidationNumber)
                .Select((row, index) =>
                {
                    row.ValidationNumber = index + 1;
                    return row;
                })
                .ToList();

            var totalChecks = summaries.Count;
            var passedChecks = summaries.Count(item => string.Equals(item.Status, "PASS", StringComparison.OrdinalIgnoreCase));
            var failedChecks = totalChecks - passedChecks;
            var totalIssues = summaries.Sum(item => item.ErrorCount);
            var highSeverityCount = summaries.Count(item => item.ErrorCount > 0 && string.Equals(item.Severity, "High", StringComparison.OrdinalIgnoreCase));
            var isPreviewOnly = !includeAllReviewRows && totalValidatedRows > reviewRows.Count;

            return new Rule10ValidationSummary
            {
                Success = true,
                RuleNumber = rule.RuleNumber,
                RuleLabel = rule.RuleLabel,
                RuleTitle = rule.RuleTitle,
                QualRecordCount = RequiresTable(request.RuleNumber, "QUAL") ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.QualTable}\";") : 0,
                StudRecordCount = RequiresTable(request.RuleNumber, "STUD") ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";") : 0,
                CregRecordCount = RequiresTable(request.RuleNumber, "CREG") ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CregTable}\";") : 0,
                CrseRecordCount = RequiresTable(request.RuleNumber, "CRSE") ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CrseTable}\";") : 0,
                TotalChecks = totalChecks,
                PassedChecks = passedChecks,
                FailedChecks = failedChecks,
                TotalIssues = totalIssues,
                HighSeverityCount = highSeverityCount,
                TotalRequested = totalChecks,
                TotalValidated = totalValidatedRows,
                DisplayedCount = reviewRows.Count,
                IsPreviewOnly = isPreviewOnly,
                PreviewLimit = isPreviewOnly ? BrowserPreviewRowLimit : 0,
                PassCount = Math.Max(totalValidatedRows - totalFailedRows, 0),
                FailCount = totalFailedRows,
                ExceptionRate = totalValidatedRows == 0 ? 0m : Math.Round(totalFailedRows * 100m / totalValidatedRows, 2),
                Status = failedChecks == 0 ? "PASS" : "FAIL",
                OverallStatusText = failedChecks == 0 ? "EXCELLENT" : passedChecks >= 7 ? "ATTENTION REQUIRED" : "CRITICAL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                QualTable = request.QualTable,
                StudTable = request.StudTable,
                CregTable = request.CregTable,
                CrseTable = request.CrseTable,
                QualColumn = request.QualColumn,
                StudColumn = request.StudColumn,
                CregColumn = request.CregColumn,
                CrseColumn = request.CrseColumn,
                RuleParameterJson = request.RuleParameterJson,
                Rule10JoinConfigJson = request.Rule10JoinConfigJson,
                TableLinkageText = $"{request.StudTable} -> {request.QualTable}, {request.CregTable} -> {request.CrseTable}, {request.CregTable} -> {request.StudTable}",
                RuleModeText = $"{rule.RuleLabel} integrity verification",
                ProcedureSteps = BuildProcedureSteps(request),
                ClientId = request.ClientId,
                ControlSummaries = summaries,
                ReviewRows = reviewRows,
                Warning = includeAllReviewRows
                    ? $"{rule.RuleLabel} completed with the full saved result set."
                    : $"Counts reflect the full {rule.RuleLabel} result set. Browser rows are limited to a 10-row pass/fail sample for performance."
            };
        }

        private async Task<int> SaveValidationRunAsync(Rule10ValidationRequest request, Rule10ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, request.RuleNumber);

            var rule = IntegrityRuleCatalog.Get(request.RuleNumber);
            var persistedSummary = CloneSummary(summary);
            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = request.RuleNumber,
                RuleName = $"{rule.RuleLabel} Integrity Check",
                Status = summary.Status,
                TotalRecords = summary.TotalChecks,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.QualTable,
                DeceasedTable = request.StudTable,
                StudColumn = request.CregTable,
                DeceasedColumn = request.CrseTable,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.ReviewRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(persistedSummary))
            }, userEmail, userName);

            summary.SavedRunId = runId;
            return runId;
        }

        private static List<IntegrityRuleDefinition> BuildIntegrityRuleDefinitions(Rule10ValidationRequest request, string schema)
        {
            var qual = $"\"{schema}\".\"{request.QualTable}\"";
            var stud = $"\"{schema}\".\"{request.StudTable}\"";
            var creg = $"\"{schema}\".\"{request.CregTable}\"";
            var crse = $"\"{schema}\".\"{request.CrseTable}\"";
            var qualColumn = (request.RuleNumber == 1 || request.RuleNumber == 2 || request.RuleNumber == 3 || request.RuleNumber == 7)
                ? ResolveSelectedColumn(request.QualColumn)
                : GetDefaultQualColumn(request.RuleNumber);
            var studColumn = (request.RuleNumber == 5 || request.RuleNumber == 6 || request.RuleNumber == 7 || request.RuleNumber == 9)
                ? ResolveSelectedColumn(request.StudColumn)
                : GetDefaultStudColumn(request.RuleNumber);
            var cregColumn = (request.RuleNumber == 8 || request.RuleNumber == 9)
                ? ResolveSelectedColumn(request.CregColumn)
                : GetDefaultCregColumn(request.RuleNumber);
            var crseColumn = (request.RuleNumber == 4 || request.RuleNumber == 8)
                ? ResolveSelectedColumn(request.CrseColumn)
                : GetDefaultCrseColumn(request.RuleNumber);
            var parameters = ParseRuleParameters(request.RuleParameterJson, request.RuleNumber);
            var rule5PlaceholderValue = ResolveRuleParameterValue(parameters.MatchValue, "9999999");
            var rule5PlaceholderValueSql = ToSqlLiteral(rule5PlaceholderValue);
            var rule7ContextColumn = ResolveSelectedColumn(parameters.ContextColumn, "_007");
            var rule8ContextColumn = ResolveSelectedColumn(parameters.ContextColumn, "_007");
            var qualValue = $"TRIM(q.\"{qualColumn}\"::text)";
            var studValue = $"TRIM(s.\"{studColumn}\"::text)";
            var qualBlank = $"(q.\"{qualColumn}\" IS NULL OR {qualValue} = '')";
            var studBlank = $"(s.\"{studColumn}\" IS NULL OR {studValue} = '')";

            var rule1Criteria = $"ISBLANK({request.QualTable}.{qualColumn})";
            var rule2Criteria = $"ISBLANK({request.QualTable}.{qualColumn})";
            var rule3Criteria = $"Checked for duplicates on {request.QualTable} using the {qualColumn} field.";
            var rule4Criteria = $"Checked for duplicates on {request.CrseTable} using the {crseColumn} field.";
            var rule5Criteria = $"NOT MAP({request.StudTable}.{studColumn}, \"{EscapeCriteriaText(rule5PlaceholderValue)}\")";
            var rule6Criteria = $"ISBLANK({request.StudTable}.{studColumn})";
            var rule7Criteria = $"ISBLANK({request.StudTable}.{studColumn}) OR {request.StudTable}.{studColumn} <> {request.QualTable}.{qualColumn}";
            var rule8Criteria = $"ISBLANK({request.CregTable}.{cregColumn}) OR {request.CregTable}.{cregColumn} <> {request.CrseTable}.{crseColumn}";
            var rule9Criteria = $"ISBLANK({request.CregTable}.{cregColumn}) OR {request.CregTable}.{cregColumn} <> {request.StudTable}.{studColumn}";

            var rule8PrepSql = $@"
DROP TABLE IF EXISTS rule8_creg_tmp;
DROP TABLE IF EXISTS rule8_crse_tmp;

CREATE TEMP TABLE rule8_creg_tmp AS
SELECT
    cr.""{rule8ContextColumn}""::text AS student_column_value,
    cr.""{cregColumn}""::text AS left_column_value,
    UPPER(TRIM(cr.""{cregColumn}""::text)) AS normalized_join_value,
    CASE WHEN cr.""{cregColumn}"" IS NULL OR TRIM(cr.""{cregColumn}""::text) = '' THEN 1 ELSE 0 END AS is_blank
FROM {creg} cr;

CREATE INDEX ON rule8_creg_tmp (normalized_join_value);
ANALYZE rule8_creg_tmp;

CREATE TEMP TABLE rule8_crse_tmp AS
SELECT
    TRIM(cs.""{crseColumn}""::text) AS right_column_value,
    UPPER(TRIM(cs.""{crseColumn}""::text)) AS normalized_join_value
FROM {crse} cs
WHERE cs.""{crseColumn}"" IS NOT NULL
  AND TRIM(cs.""{crseColumn}""::text) <> '';

CREATE INDEX ON rule8_crse_tmp (normalized_join_value);
ANALYZE rule8_crse_tmp;";

            var rule8ReviewProjection = $@"SELECT
CASE WHEN cr.is_blank = 1 OR cs.normalized_join_value IS NULL THEN 'FAIL' ELSE 'PASS' END AS ""Validation_Result"",
CASE
    WHEN cr.is_blank = 1 THEN 'fail because the selected course value is blank in the CREG table'
    WHEN cs.normalized_join_value IS NULL THEN CONCAT('fail because ', cr.left_column_value, ' is not in ', '{request.CrseTable}')
    ELSE CONCAT('pass because ', cr.left_column_value, ' is in both tables')
END AS ""Validation_Explanation"",
CASE WHEN cr.is_blank = 1 OR cs.normalized_join_value IS NULL THEN 'Invalid Course Reference' ELSE 'Valid Course Reference' END AS ""Exception_Type"",
'{request.CregTable}' AS ""STUDENT_TABLE_NAME"", '{rule8ContextColumn}' AS ""STUDENT_COLUMN_NAME"", cr.student_column_value AS ""STUDENT_COLUMN_VALUE"",
'{request.CregTable}' AS ""LEFT_TABLE_NAME"", '{cregColumn}' AS ""LEFT_COLUMN_NAME"", cr.left_column_value AS ""LEFT_COLUMN_VALUE"",
'{request.CrseTable}' AS ""RIGHT_TABLE_NAME"", '{crseColumn}' AS ""RIGHT_COLUMN_NAME"", cs.right_column_value AS ""RIGHT_COLUMN_VALUE"",
CASE
    WHEN cr.is_blank = 1 THEN 'fail because the selected course value is blank in the CREG table'
    WHEN cs.normalized_join_value IS NULL THEN CONCAT('fail because ', cr.left_column_value, ' is not in ', '{request.CrseTable}')
    ELSE CONCAT('pass because ', cr.left_column_value, ' is in both tables')
END AS ""FINAL_RESULT_MESSAGE""
FROM rule8_creg_tmp cr
LEFT JOIN LATERAL (
    SELECT s.normalized_join_value, s.right_column_value
    FROM rule8_crse_tmp s
    WHERE s.normalized_join_value = cr.normalized_join_value
    LIMIT 1
) cs ON true";
            var rule8ReviewSql = $@"{rule8ReviewProjection}
ORDER BY CASE WHEN cr.is_blank = 1 OR cs.normalized_join_value IS NULL THEN 0 ELSE 1 END, cr.student_column_value;";

            var rule9PrepSql = $@"
DROP TABLE IF EXISTS rule9_creg_tmp;
DROP TABLE IF EXISTS rule9_stud_tmp;

CREATE TEMP TABLE rule9_creg_tmp AS
SELECT
    cr.""RowId"" AS row_no,
    TRIM(COALESCE(cr.""{cregColumn}""::text, '')) AS student_no
FROM {creg} cr;

CREATE INDEX ON rule9_creg_tmp (student_no);
ANALYZE rule9_creg_tmp;

CREATE TEMP TABLE rule9_stud_tmp AS
SELECT DISTINCT TRIM(COALESCE(s.""{studColumn}""::text, '')) AS student_no
FROM {stud} s
WHERE s.""{studColumn}"" IS NOT NULL
  AND TRIM(s.""{studColumn}""::text) <> '';

CREATE INDEX ON rule9_stud_tmp (student_no);
ANALYZE rule9_stud_tmp;";

            var rule9ReviewSql = $@"SELECT
'FAIL' AS ""Validation_Result"",
CASE WHEN c.student_no = '' THEN 'Blank Student Number' ELSE 'Student Not in STUD Table' END AS ""Validation_Explanation"",
CASE WHEN c.student_no = '' THEN 'Blank Student Number' ELSE 'Student Not in STUD Table' END AS ""Exception_Type"",
c.row_no AS ""RowNo"",
c.student_no AS ""StudentNo"",
CASE WHEN c.student_no = '' THEN 'Blank Student Number' ELSE 'Student Not in STUD Table' END AS ""FINAL_RESULT_MESSAGE""
FROM rule9_creg_tmp c
LEFT JOIN rule9_stud_tmp s ON s.student_no = c.student_no
WHERE c.student_no = ''
   OR s.student_no IS NULL
ORDER BY ""Exception_Type"", c.student_no;";

            var definitions = new List<IntegrityRuleDefinition>
            {
                new(1, "Qualifications without qualification type", rule1Criteria, request.QualTable, "High",
                    $@"SELECT COUNT(*) FROM {qual} q WHERE {qualBlank};",
                    $@"SELECT COUNT(*) FROM {qual} q;",
                    $@"SELECT
CASE WHEN {qualBlank} THEN 'FAIL' ELSE 'PASS' END AS ""Validation_Result"",
CASE WHEN {qualBlank}
    THEN 'The selected value is blank on the selected table and column.'
    ELSE 'The selected value is populated on the selected table and column.'
END AS ""Validation_Explanation"",
CASE WHEN {qualBlank} THEN 'Missing Qualification Type' ELSE 'Populated Qualification Type' END AS ""Exception_Type"",
'{request.QualTable}' AS ""TABLE_NAME"", '{qualColumn}' AS ""COLUMN_NAME"", q.""{qualColumn}""::text AS ""COLUMN_VALUE""
FROM {qual} q
ORDER BY CASE WHEN {qualBlank} THEN 0 ELSE 1 END, q.""{qualColumn}""::text;"),
                new(2, "Qualifications without approval status", rule2Criteria, request.QualTable, "High",
                    $@"SELECT COUNT(*) FROM {qual} q WHERE {qualBlank};",
                    $@"SELECT COUNT(*) FROM {qual} q;",
                    $@"SELECT
CASE WHEN {qualBlank} THEN 'FAIL' ELSE 'PASS' END AS ""Validation_Result"",
CASE WHEN {qualBlank}
    THEN 'The selected value is blank on the selected table and column.'
    ELSE 'The selected value is populated on the selected table and column.'
END AS ""Validation_Explanation"",
CASE WHEN {qualBlank} THEN 'Missing Approval Status' ELSE 'Populated Approval Status' END AS ""Exception_Type"",
'{request.QualTable}' AS ""TABLE_NAME"", '{qualColumn}' AS ""COLUMN_NAME"", q.""{qualColumn}""::text AS ""COLUMN_VALUE""
FROM {qual} q
ORDER BY CASE WHEN {qualBlank} THEN 0 ELSE 1 END, q.""{qualColumn}""::text;"),
                new(3, "Duplicate qualification codes", rule3Criteria, request.QualTable, "High",
                    $@"SELECT COUNT(*) FROM (SELECT ""{qualColumn}"" FROM {qual} GROUP BY ""{qualColumn}"" HAVING COUNT(*) > 1) d;",
                    $@"SELECT COUNT(*) FROM (SELECT ""{qualColumn}"" FROM {qual} GROUP BY ""{qualColumn}"") d;",
                    $@"SELECT
CASE WHEN COUNT(*) > 1 THEN 'FAIL' ELSE 'PASS' END AS ""Validation_Result"",
CASE WHEN COUNT(*) > 1
    THEN CONCAT('fail because ', ""{qualColumn}""::text, ' appears more than once in the selected column')
    ELSE CONCAT('pass because ', ""{qualColumn}""::text, ' appears once in the selected column')
END AS ""Validation_Explanation"",
CASE WHEN COUNT(*) > 1 THEN 'Duplicate Qualification Code' ELSE 'Unique Qualification Code' END AS ""Exception_Type"",
'{request.QualTable}' AS ""TABLE_NAME"", '{qualColumn}' AS ""COLUMN_NAME"",
""{qualColumn}""::text AS ""COLUMN_VALUE"",
""{qualColumn}""::text AS ""DUPLICATE_VALUE"",
COUNT(*) AS ""DUPLICATE_COUNT""
FROM {qual}
GROUP BY ""{qualColumn}""
ORDER BY COUNT(*) DESC, ""{qualColumn}""::text;"),
                new(4, "Duplicate course codes", rule4Criteria, request.CrseTable, "High",
                    $@"SELECT COUNT(*) FROM (SELECT ""{crseColumn}"" FROM {crse} GROUP BY ""{crseColumn}"" HAVING COUNT(*) > 1) d;",
                    $@"SELECT COUNT(*) FROM (SELECT ""{crseColumn}"" FROM {crse} GROUP BY ""{crseColumn}"") d;",
                    $@"SELECT
CASE WHEN COUNT(*) > 1 THEN 'FAIL' ELSE 'PASS' END AS ""Validation_Result"",
CASE WHEN COUNT(*) > 1
    THEN CONCAT('fail because ', ""{crseColumn}""::text, ' appears more than once in the selected column')
    ELSE CONCAT('pass because ', ""{crseColumn}""::text, ' appears once in the selected column')
END AS ""Validation_Explanation"",
CASE WHEN COUNT(*) > 1 THEN 'Duplicate Course Code' ELSE 'Unique Course Code' END AS ""Exception_Type"",
'{request.CrseTable}' AS ""TABLE_NAME"", '{crseColumn}' AS ""COLUMN_NAME"",
""{crseColumn}""::text AS ""COLUMN_VALUE"",
""{crseColumn}""::text AS ""DUPLICATE_VALUE"",
COUNT(*) AS ""DUPLICATE_COUNT""
FROM {crse}
GROUP BY ""{crseColumn}""
ORDER BY COUNT(*) DESC, ""{crseColumn}""::text;"),
                new(5, "Invalid student numbers", rule5Criteria, request.StudTable, "High",
                    $@"SELECT COUNT(*) FROM {stud} s WHERE s.""{studColumn}""::text = {rule5PlaceholderValueSql};",
                    $@"SELECT COUNT(*) FROM {stud} s;",
                    $@"SELECT
CASE WHEN s.""{studColumn}""::text = {rule5PlaceholderValueSql} THEN 'FAIL' ELSE 'PASS' END AS ""Validation_Result"",
CASE WHEN s.""{studColumn}""::text = {rule5PlaceholderValueSql}
    THEN CONCAT('fail because the selected value matches the configured invalid value ', {rule5PlaceholderValueSql})
    ELSE CONCAT('pass because the selected value does not match the configured invalid value ', {rule5PlaceholderValueSql})
END AS ""Validation_Explanation"",
CASE WHEN s.""{studColumn}""::text = {rule5PlaceholderValueSql} THEN 'Invalid Student Number' ELSE 'Valid Student Number' END AS ""Exception_Type"",
'{request.StudTable}' AS ""TABLE_NAME"", '{studColumn}' AS ""COLUMN_NAME"", s.""{studColumn}""::text AS ""COLUMN_VALUE"", {rule5PlaceholderValueSql} AS ""EXPECTED_VALUE""
FROM {stud} s
ORDER BY CASE WHEN s.""{studColumn}""::text = {rule5PlaceholderValueSql} THEN 0 ELSE 1 END, s.""{studColumn}""::text;"),
                new(6, "Students without foundation indicator", rule6Criteria, request.StudTable, "Medium",
                    $@"SELECT COUNT(*) FROM {stud} s WHERE {studBlank};",
                    $@"SELECT COUNT(*) FROM {stud} s;",
                    $@"SELECT
CASE WHEN {studBlank} THEN 'FAIL' ELSE 'PASS' END AS ""Validation_Result"",
CASE WHEN {studBlank}
    THEN 'The selected value is blank on the selected table and column.'
    ELSE 'The selected value is populated on the selected table and column.'
END AS ""Validation_Explanation"",
CASE WHEN {studBlank} THEN 'Missing Foundation Indicator' ELSE 'Populated Foundation Indicator' END AS ""Exception_Type"",
'{request.StudTable}' AS ""TABLE_NAME"", '{studColumn}' AS ""COLUMN_NAME"", s.""{studColumn}""::text AS ""COLUMN_VALUE""
FROM {stud} s
ORDER BY CASE WHEN {studBlank} THEN 0 ELSE 1 END, s.""{studColumn}""::text;"),
                new(7, "Students with invalid qualifications", rule7Criteria, $"{request.StudTable} -> {request.QualTable}", "High",
                    $@"SELECT COUNT(*) FROM {stud} s
LEFT JOIN {qual} q ON NOT ({studBlank}) AND UPPER({studValue}) = UPPER({qualValue})
WHERE {studBlank} OR q.""{qualColumn}"" IS NULL;",
                    $@"SELECT COUNT(*) FROM {stud} s;",
                    $@"SELECT
CASE WHEN {studBlank} OR q.""{qualColumn}"" IS NULL THEN 'FAIL' ELSE 'PASS' END AS ""Validation_Result"",
CASE
    WHEN {studBlank} THEN 'fail because the selected qualification value is blank in the STUD table'
    WHEN q.""{qualColumn}"" IS NULL THEN CONCAT('fail because ', s.""{studColumn}""::text, ' is not in ', '{request.QualTable}')
    ELSE CONCAT('pass because ', s.""{studColumn}""::text, ' is in both tables')
END AS ""Validation_Explanation"",
CASE WHEN {studBlank} OR q.""{qualColumn}"" IS NULL THEN 'Invalid Qualification Reference' ELSE 'Valid Qualification Reference' END AS ""Exception_Type"",
'{request.StudTable}' AS ""STUDENT_TABLE_NAME"", '{rule7ContextColumn}' AS ""STUDENT_COLUMN_NAME"", s.""{rule7ContextColumn}""::text AS ""STUDENT_COLUMN_VALUE"",
'{request.StudTable}' AS ""LEFT_TABLE_NAME"", '{studColumn}' AS ""LEFT_COLUMN_NAME"", s.""{studColumn}""::text AS ""LEFT_COLUMN_VALUE"",
'{request.QualTable}' AS ""RIGHT_TABLE_NAME"", '{qualColumn}' AS ""RIGHT_COLUMN_NAME"", TRIM(q.""{qualColumn}""::text) AS ""RIGHT_COLUMN_VALUE"",
CASE
    WHEN {studBlank} THEN 'fail because the selected qualification value is blank in the STUD table'
    WHEN q.""{qualColumn}"" IS NULL THEN CONCAT('fail because ', s.""{studColumn}""::text, ' is not in ', '{request.QualTable}')
    ELSE CONCAT('pass because ', s.""{studColumn}""::text, ' is in both tables')
END AS ""FINAL_RESULT_MESSAGE""
FROM {stud} s
LEFT JOIN {qual} q ON NOT ({studBlank}) AND UPPER({studValue}) = UPPER({qualValue})
ORDER BY CASE WHEN {studBlank} OR q.""{qualColumn}"" IS NULL THEN 0 ELSE 1 END, s.""{rule7ContextColumn}""::text;"),
                new(8, "Course registrations for invalid courses", rule8Criteria, $"{request.CregTable} -> {request.CrseTable}", "High",
                    @"SELECT COUNT(*)
FROM rule8_creg_tmp cr
WHERE cr.is_blank = 1
   OR NOT EXISTS (SELECT 1 FROM rule8_crse_tmp cs WHERE cs.normalized_join_value = cr.normalized_join_value);",
                    @"SELECT COUNT(*) FROM rule8_creg_tmp;",
                    rule8ReviewSql,
                    null,
                    rule8PrepSql),
                new(9, "Course registrations for ghost students", rule9Criteria, $"{request.CregTable} -> {request.StudTable}", "High",
                    @"SELECT COUNT(*)
FROM rule9_creg_tmp c
LEFT JOIN rule9_stud_tmp s ON s.student_no = c.student_no
WHERE c.student_no = ''
   OR s.student_no IS NULL;",
                    @"SELECT COUNT(*) FROM rule9_creg_tmp;",
                    rule9ReviewSql,
                    null,
                    rule9PrepSql),
                new(10, "Joining Rules", "The tables were joined on the documented key fields for each dataset.", "Joining Rules", "Medium",
                    "SELECT 0;",
                    "SELECT 0;",
                    @"SELECT
'INFO' AS ""Validation_Result"",
'Joining rules reference only.' AS ""Validation_Explanation"",
'Joining Rules' AS ""Exception_Type""
WHERE false;")
            };

            return definitions
                .Where(definition => definition.RuleId == request.RuleNumber)
                .ToList();
        }

        private async Task<List<Rule10ValidationRowRecord>> LoadValidationRowsAsync(NpgsqlConnection connection, IntegrityRuleDefinition definition, int? maxRows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = maxRows.HasValue && maxRows.Value > 0 && !string.IsNullOrWhiteSpace(definition.SampleReviewSql)
                ? definition.SampleReviewSql
                : BuildReviewSql(definition.ReviewSql, maxRows);

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule10ValidationRowRecord>();
            while (await reader.ReadAsync())
            {
                var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    displayValues[reader.GetName(i)] = reader.IsDBNull(i)
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }

                var row = new Rule10ValidationRowRecord
                {
                    ValidationNumber = rows.Count + 1,
                    RuleId = definition.RuleId,
                    ControlType = $"Rule_{definition.RuleId}",
                    ControlLabel = $"Rule {definition.RuleId}",
                    ValidationResult = ReadValue(displayValues, "Validation_Result"),
                    ValidationExplanation = ReadValue(displayValues, "Validation_Explanation"),
                    DisplayValues = displayValues
                };
                EnrichRule10DisplayValues(row);
                rows.Add(row);
            }

            return rows;
        }

        private static string BuildReviewSql(string reviewSql, int? maxRows)
        {
            if (!maxRows.HasValue || maxRows.Value <= 0)
                return reviewSql;

            var trimmed = reviewSql.Trim();
            var orderByIndex = trimmed.LastIndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
            if (orderByIndex >= 0)
                trimmed = trimmed[..orderByIndex].TrimEnd().TrimEnd(';');

            var failTake = Math.Max(maxRows.Value / 2, 1);
            var passTake = Math.Max(maxRows.Value - failTake, 1);

            return $@"
SELECT * FROM (
    SELECT * FROM ( {trimmed} ) fail_sample
    WHERE UPPER(COALESCE(""Validation_Result"", '')) = 'FAIL'
    LIMIT {failTake}
) fails
UNION ALL
SELECT * FROM (
    SELECT * FROM ( {trimmed} ) pass_sample
    WHERE UPPER(COALESCE(""Validation_Result"", '')) = 'PASS'
    LIMIT {passTake}
) passes;";
        }

        private async Task EnsureColumnsExistAsync(Rule10ValidationRequest request)
        {
            switch (request.RuleNumber)
            {
                case 1:
                case 2:
                case 3:
                    EnsureHasColumns(
                        request.QualTable,
                        await _datasets.GetValidatedColumnsAsync(request.ClientId, request.QualTable),
                        ResolveSelectedColumn(request.QualColumn));
                    break;
                case 4:
                    EnsureHasColumns(
                        request.CrseTable,
                        await _datasets.GetValidatedColumnsAsync(request.ClientId, request.CrseTable),
                        ResolveSelectedColumn(request.CrseColumn));
                    break;
                case 5:
                case 6:
                    EnsureHasColumns(
                        request.StudTable,
                        await _datasets.GetValidatedColumnsAsync(request.ClientId, request.StudTable),
                        ResolveSelectedColumn(request.StudColumn));
                    break;
                case 7:
                    EnsureHasColumns(
                        request.StudTable,
                        await _datasets.GetValidatedColumnsAsync(request.ClientId, request.StudTable),
                        ResolveSelectedColumn(request.StudColumn),
                        ResolveSelectedColumn(ParseRuleParameters(request.RuleParameterJson, request.RuleNumber).ContextColumn, "_007"));
                    EnsureHasColumns(
                        request.QualTable,
                        await _datasets.GetValidatedColumnsAsync(request.ClientId, request.QualTable),
                        ResolveSelectedColumn(request.QualColumn));
                    break;
                case 8:
                    EnsureHasColumns(
                        request.CregTable,
                        await _datasets.GetValidatedColumnsAsync(request.ClientId, request.CregTable),
                        ResolveSelectedColumn(request.CregColumn),
                        ResolveSelectedColumn(ParseRuleParameters(request.RuleParameterJson, request.RuleNumber).ContextColumn, "_007"));
                    EnsureHasColumns(
                        request.CrseTable,
                        await _datasets.GetValidatedColumnsAsync(request.ClientId, request.CrseTable),
                        ResolveSelectedColumn(request.CrseColumn));
                    break;
                case 9:
                    EnsureHasColumns(
                        request.CregTable,
                        await _datasets.GetValidatedColumnsAsync(request.ClientId, request.CregTable),
                        ResolveSelectedColumn(request.CregColumn));
                    EnsureHasColumns(
                        request.StudTable,
                        await _datasets.GetValidatedColumnsAsync(request.ClientId, request.StudTable),
                        ResolveSelectedColumn(request.StudColumn));
                    break;
                case 10:
                    await VerifyRule10JoinDatasetsAsync(request.ClientId, request.Rule10JoinConfigJson, throwOnMissingColumns: true);
                    break;
                default:
                    throw new InvalidOperationException($"Integrity rule {request.RuleNumber} is not supported.");
            }
        }

        private async Task<List<Rule10JoinDatasetVerificationItem>> VerifyRule10JoinDatasetsAsync(int clientId, string? joinConfigJson, bool throwOnMissingColumns)
        {
            var results = new List<Rule10JoinDatasetVerificationItem>();
            var uploadedTables = await _datasets.ListTableNamesAsync(clientId);

            foreach (var dataset in ResolveRule10JoinDatasets(joinConfigJson))
            {
                var tableExists = uploadedTables.Contains(dataset.TableName, StringComparer.OrdinalIgnoreCase);
                List<string> columns;
                try
                {
                    columns = tableExists ? await _datasets.GetValidatedColumnsAsync(clientId, dataset.TableName) : new List<string>();
                }
                catch
                {
                    columns = new List<string>();
                }

                var missingColumns = dataset.KeyColumns
                    .Where(column => !columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                var recordCount = 0;
                if (missingColumns.Count == 0 && tableExists)
                {
                    var (conn, schema) = await OpenEngagementConnectionAsync(clientId);
                    await using var connection = conn;
                    recordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{dataset.TableName}\";");
                }

                results.Add(new Rule10JoinDatasetVerificationItem
                {
                    DatasetCode = dataset.DatasetCode,
                    DatasetLabel = dataset.DatasetLabel,
                    TableName = dataset.TableName,
                    RecordCount = recordCount,
                    RequiredColumns = dataset.KeyColumns.ToList(),
                    MissingColumns = missingColumns,
                    Status = missingColumns.Count == 0 && tableExists ? "PASS" : "FAIL"
                });
            }

            var firstFailure = results.FirstOrDefault(item => item.MissingColumns.Count > 0);
            if (throwOnMissingColumns && firstFailure != null)
            {
                throw new InvalidOperationException(
                    $"Table {firstFailure.TableName} is missing required key column(s): {string.Join(", ", firstFailure.MissingColumns)}.");
            }

            return results;
        }

        private async Task<Rule10ValidationSummary> AnalyseRule10JoinVerificationAsync(Rule10ValidationRequest request)
        {
            var rule = IntegrityRuleCatalog.Get(request.RuleNumber);
            var verifications = await VerifyRule10JoinDatasetsAsync(request.ClientId, request.Rule10JoinConfigJson, throwOnMissingColumns: false);

            var summaries = verifications.Select((item, index) => new Rule10ControlSummaryItemViewModel
            {
                RuleId = index + 1,
                ControlType = item.DatasetCode,
                ControlLabel = item.DatasetLabel,
                CriteriaText = $"Key fields: {string.Join(", ", item.RequiredColumns)}{(item.TableName.Length > 0 ? $" | Selected table: {item.TableName}" : string.Empty)}",
                TableName = item.TableName,
                Severity = "Medium",
                ErrorCount = item.MissingColumns.Count,
                RequestedCount = item.RequiredColumns.Count,
                AvailableCount = item.RequiredColumns.Count - item.MissingColumns.Count,
                AchievedCount = item.MissingColumns.Count == 0 ? 1 : 0,
                TotalCount = 1,
                PassCount = item.MissingColumns.Count == 0 ? 1 : 0,
                FailCount = item.MissingColumns.Count > 0 ? 1 : 0,
                Status = item.MissingColumns.Count == 0 ? "PASS" : "FAIL"
            }).ToList();

            var reviewRows = new List<Rule10ValidationRowRecord>();
            for (var i = 0; i < verifications.Count; i++)
            {
                var item = verifications[i];
                var isFailure = item.MissingColumns.Count > 0;
                reviewRows.Add(new Rule10ValidationRowRecord
                {
                    ValidationNumber = i + 1,
                    RuleId = 10,
                    ControlType = item.DatasetCode,
                    ControlLabel = item.DatasetLabel,
                    ValidationResult = isFailure ? "FAIL" : "PASS",
                    ValidationExplanation = isFailure
                        ? "The documented key columns do not all exist on the selected table."
                        : "All documented key columns exist on the selected table.",
                    DisplayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["DATASET"] = item.DatasetLabel,
                        ["TABLE_NAME"] = item.TableName,
                        ["REQUIRED_KEY_COLUMNS"] = string.Join(", ", item.RequiredColumns),
                        ["MISSING_COLUMNS"] = string.Join(", ", item.MissingColumns),
                        ["FINAL_RESULT_MESSAGE"] = isFailure
                            ? $"fail because {string.Join(", ", item.MissingColumns)} {(item.MissingColumns.Count == 1 ? "is" : "are")} missing on {item.TableName}"
                            : $"pass because all documented key columns exist on {item.TableName}"
                    }
                });
            }

            var passedChecks = summaries.Count(item => string.Equals(item.Status, "PASS", StringComparison.OrdinalIgnoreCase));
            var failedChecks = summaries.Count - passedChecks;
            var totalIssues = summaries.Sum(item => item.ErrorCount);

            return new Rule10ValidationSummary
            {
                Success = true,
                RuleNumber = rule.RuleNumber,
                RuleLabel = rule.RuleLabel,
                RuleTitle = rule.RuleTitle,
                TotalChecks = summaries.Count,
                PassedChecks = passedChecks,
                FailedChecks = failedChecks,
                TotalIssues = totalIssues,
                HighSeverityCount = 0,
                TotalRequested = summaries.Count,
                TotalValidated = summaries.Count,
                DisplayedCount = reviewRows.Count,
                IsPreviewOnly = false,
                PreviewLimit = 0,
                PassCount = passedChecks,
                FailCount = failedChecks,
                ExceptionRate = summaries.Count == 0 ? 0m : Math.Round(failedChecks * 100m / summaries.Count, 2),
                Status = failedChecks == 0 ? "PASS" : "FAIL",
                OverallStatusText = failedChecks == 0 ? "EXCELLENT" : "ATTENTION REQUIRED",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                QualTable = request.QualTable,
                StudTable = request.StudTable,
                CregTable = request.CregTable,
                CrseTable = request.CrseTable,
                QualColumn = request.QualColumn,
                StudColumn = request.StudColumn,
                CregColumn = request.CregColumn,
                CrseColumn = request.CrseColumn,
                RuleParameterJson = request.RuleParameterJson,
                Rule10JoinConfigJson = request.Rule10JoinConfigJson,
                TableLinkageText = "Documented dataset key-column existence check",
                RuleModeText = $"{rule.RuleLabel} join-key verification",
                ProcedureSteps = BuildProcedureSteps(request),
                ClientId = request.ClientId,
                ControlSummaries = summaries,
                ReviewRows = reviewRows,
                Warning = failedChecks == 0
                    ? "All documented dataset key columns were found on the selected tables."
                    : "One or more documented dataset key columns are missing from the selected tables."
            };
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
            // A handful of engagements' database rows predate the SchemaName column and still
            // have it blank — engagement_{clientId} is always the actual schema regardless, so
            // fall back to it rather than building "".table queries.
            var schema = string.IsNullOrWhiteSpace(database.SchemaName) ? $"engagement_{clientId}" : database.SchemaName;
            return (connection, schema);
        }

        private static List<string> BuildProcedureSteps(Rule10ValidationRequest request)
        {
            var rule = IntegrityRuleCatalog.Get(request.RuleNumber);

            if (request.RuleNumber == 10)
            {
                return
                [
                    "Review the configured join datasets for this rule.",
                    "Confirm that each selected table exists in this engagement's uploaded data.",
                    "Confirm that every documented key column exists on the selected table.",
                    "Flag any missing tables or missing key columns as failing exceptions.",
                    $"{rule.RuleLabel} passes only when every documented dataset has all required key columns."
                ];
            }

            return
            [
                $"Verify {rule.RuleLabel} using the required HEMIS tables uploaded for this engagement.",
                rule.DetailedDescription,
                $"Apply the audit criteria: {rule.CriteriaText}.",
                "Capture every failing row for review and export.",
                $"{rule.RuleLabel} passes only when the check returns zero issues."
            ];
        }

        private static bool RequiresTable(int ruleNumber, string tableCode) =>
            IntegrityRuleCatalog.Get(ruleNumber).RequiredTables.Contains(tableCode, StringComparer.OrdinalIgnoreCase);

        private static string GetDefaultQualColumn(int ruleNumber) => ruleNumber switch
        {
            1 => "_005",
            2 => "_004",
            3 => "_001",
            7 => "_001",
            _ => "_005"
        };

        private static string GetDefaultStudColumn(int ruleNumber) => ruleNumber switch
        {
            5 => "_007",
            6 => "_106",
            7 => "_001",
            9 => "_007",
            _ => "_007"
        };

        private static string GetDefaultCregColumn(int ruleNumber) => ruleNumber switch
        {
            8 => "_030",
            9 => "_007",
            _ => "_030"
        };

        private static string GetDefaultCrseColumn(int ruleNumber) => "_030";

        private static string GetDefaultRuleParameterJson(int ruleNumber) => ruleNumber switch
        {
            5 => JsonConvert.SerializeObject(new IntegrityRuleParameterSet { MatchValue = "9999999" }),
            7 => JsonConvert.SerializeObject(new IntegrityRuleParameterSet { ContextColumn = "_007" }),
            8 => JsonConvert.SerializeObject(new IntegrityRuleParameterSet { ContextColumn = "_007" }),
            _ => ""
        };

        private static string ResolveSelectedColumn(string? selectedColumn, string fallbackColumn = "")
        {
            var resolved = string.IsNullOrWhiteSpace(selectedColumn) ? fallbackColumn : selectedColumn.Trim();
            if (string.IsNullOrWhiteSpace(resolved))
                throw new InvalidOperationException("Table or column name is required.");

            ValidateObjectName(resolved);
            return resolved;
        }

        private static IntegrityRuleParameterSet ParseRuleParameters(string? ruleParameterJson, int ruleNumber)
        {
            var fallback = ruleNumber switch
            {
                5 => new IntegrityRuleParameterSet { MatchValue = "9999999" },
                7 => new IntegrityRuleParameterSet { ContextColumn = "_007" },
                8 => new IntegrityRuleParameterSet { ContextColumn = "_007" },
                _ => new IntegrityRuleParameterSet()
            };

            if (string.IsNullOrWhiteSpace(ruleParameterJson))
                return fallback;

            try
            {
                var parsed = JsonConvert.DeserializeObject<IntegrityRuleParameterSet>(ruleParameterJson);
                if (parsed == null)
                    return fallback;

                return new IntegrityRuleParameterSet
                {
                    MatchValue = string.IsNullOrWhiteSpace(parsed.MatchValue) ? fallback.MatchValue : parsed.MatchValue.Trim(),
                    ContextColumn = string.IsNullOrWhiteSpace(parsed.ContextColumn) ? fallback.ContextColumn : parsed.ContextColumn.Trim(),
                    SecondaryContextColumn = string.IsNullOrWhiteSpace(parsed.SecondaryContextColumn) ? fallback.SecondaryContextColumn : parsed.SecondaryContextColumn.Trim()
                };
            }
            catch
            {
                return fallback;
            }
        }

        private static string ResolveRuleParameterValue(string? selectedValue, string fallbackValue)
            => string.IsNullOrWhiteSpace(selectedValue) ? fallbackValue : selectedValue.Trim();

        private static string ToSqlLiteral(string value)
            => $"'{value.Replace("'", "''")}'";

        private static string EscapeCriteriaText(string value)
            => value.Replace("\"", "\"\"");

        private sealed class IntegrityRuleParameterSet
        {
            public string? MatchValue { get; set; }
            public string? ContextColumn { get; set; }
            public string? SecondaryContextColumn { get; set; }
        }

        private static List<Rule10JoinDatasetConfigItem> ResolveRule10JoinDatasets(string? joinConfigJson)
        {
            if (!string.IsNullOrWhiteSpace(joinConfigJson))
            {
                try
                {
                    var configured = JsonConvert.DeserializeObject<List<Rule10JoinDatasetConfigItem>>(joinConfigJson);
                    if (configured != null && configured.Count > 0)
                    {
                        return configured
                            .Where(item => !string.IsNullOrWhiteSpace(item.TableName))
                            .Select(item => new Rule10JoinDatasetConfigItem
                            {
                                DatasetCode = item.DatasetCode,
                                DatasetLabel = string.IsNullOrWhiteSpace(item.DatasetLabel) ? item.DatasetCode : item.DatasetLabel,
                                TableName = item.TableName.Trim(),
                                KeyColumns = item.KeyColumns.Where(column => !string.IsNullOrWhiteSpace(column)).Select(column => column.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                                CompositeKeyFields = item.CompositeKeyFields.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToList()
                            })
                            .ToList();
                    }
                }
                catch
                {
                }
            }

            return Rule10JoinDatasets
                .Select(item => new Rule10JoinDatasetConfigItem
                {
                    DatasetCode = item.DatasetCode,
                    DatasetLabel = item.DatasetLabel,
                    TableName = item.DefaultTableName,
                    KeyColumns = item.KeyColumns.ToList(),
                    CompositeKeyFields = item.CompositeKeyFields.ToList()
                })
                .ToList();
        }

        private static void EnsureHasColumns(string tableName, IReadOnlyCollection<string> availableColumns, params string[] requiredColumns)
        {
            var missing = requiredColumns
                .Where(required => !string.IsNullOrWhiteSpace(required))
                .Select(required => ResolveSelectedColumn(required))
                .Where(required => !availableColumns.Contains(required, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (missing.Count > 0)
                throw new InvalidOperationException($"Table {tableName} is missing required column(s): {string.Join(", ", missing)}.");
        }

        private static void ValidateRequest(Rule10VerifyRequest request)
        {
            if (!IntegrityRuleCatalog.IsSupported(request.RuleNumber))
                throw new InvalidOperationException($"Integrity rule {request.RuleNumber} is not supported.");
            if (request.ClientId <= 0)
                throw new InvalidOperationException("Select an approved engagement before verifying tables.");

            ValidateRequiredTables(new Rule10ValidationRequest
            {
                ClientId = request.ClientId,
                RuleNumber = request.RuleNumber,
                QualTable = request.QualTable,
                StudTable = request.StudTable,
                CregTable = request.CregTable,
                CrseTable = request.CrseTable,
                QualColumn = request.QualColumn,
                StudColumn = request.StudColumn,
                CregColumn = request.CregColumn,
                CrseColumn = request.CrseColumn,
                Rule10JoinConfigJson = request.Rule10JoinConfigJson
            });
        }

        private static void ValidateRequest(Rule10ValidationRequest request)
        {
            if (!IntegrityRuleCatalog.IsSupported(request.RuleNumber))
                throw new InvalidOperationException($"Integrity rule {request.RuleNumber} is not supported.");
            if (request.ClientId <= 0)
                throw new InvalidOperationException("Select an approved engagement before running this rule.");

            ValidateRequiredTables(request);
        }

        private static void ValidateRequiredTables(Rule10ValidationRequest request)
        {
            switch (request.RuleNumber)
            {
                case 1:
                    ValidateObjectName(request.QualTable);
                    ValidateObjectName(ResolveSelectedColumn(request.QualColumn));
                    break;
                case 2:
                case 3:
                    ValidateObjectName(request.QualTable);
                    ValidateObjectName(ResolveSelectedColumn(request.QualColumn));
                    break;
                case 4:
                    ValidateObjectName(request.CrseTable);
                    ValidateObjectName(ResolveSelectedColumn(request.CrseColumn));
                    break;
                case 5:
                    ValidateObjectName(request.StudTable);
                    ValidateObjectName(ResolveSelectedColumn(request.StudColumn));
                    break;
                case 6:
                    ValidateObjectName(request.StudTable);
                    ValidateObjectName(ResolveSelectedColumn(request.StudColumn));
                    break;
                case 7:
                    ValidateObjectName(request.StudTable);
                    ValidateObjectName(request.QualTable);
                    ValidateObjectName(ResolveSelectedColumn(request.StudColumn));
                    ValidateObjectName(ResolveSelectedColumn(request.QualColumn));
                    ValidateObjectName(ResolveSelectedColumn(ParseRuleParameters(request.RuleParameterJson, request.RuleNumber).ContextColumn, "_007"));
                    break;
                case 8:
                    ValidateObjectName(request.CregTable);
                    ValidateObjectName(request.CrseTable);
                    ValidateObjectName(ResolveSelectedColumn(request.CregColumn));
                    ValidateObjectName(ResolveSelectedColumn(request.CrseColumn));
                    ValidateObjectName(ResolveSelectedColumn(ParseRuleParameters(request.RuleParameterJson, request.RuleNumber).ContextColumn, "_007"));
                    break;
                case 9:
                    ValidateObjectName(request.CregTable);
                    ValidateObjectName(request.StudTable);
                    ValidateObjectName(ResolveSelectedColumn(request.CregColumn));
                    ValidateObjectName(ResolveSelectedColumn(request.StudColumn));
                    break;
                case 10:
                    foreach (var dataset in ResolveRule10JoinDatasets(request.Rule10JoinConfigJson))
                    {
                        ValidateObjectName(dataset.TableName);
                        foreach (var keyColumn in dataset.KeyColumns)
                            ValidateObjectName(keyColumn);
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Integrity rule {request.RuleNumber} is not supported.");
            }
        }

        private static void ValidateObjectName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Table or column name is required.");

            foreach (var bad in new[] { ";", "'", "\"", "--", "/*", "*/" })
            {
                if (value.Contains(bad, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unsafe table or column name was provided.");
            }
        }

        private static string? FindFirst(IEnumerable<string> values, string[] exactMatches, string[] containsMatches)
        {
            foreach (var exact in exactMatches)
            {
                var match = values.FirstOrDefault(c => c.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }

            foreach (var fragment in containsMatches)
            {
                var match = values.FirstOrDefault(c => c.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }

            return values.FirstOrDefault();
        }

        private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private static Rule10ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule10ValidationSummary>(decoded);
            }
            catch { return null; }
        }

        private static Rule10ValidationSummary CloneSummary(Rule10ValidationSummary summary)
        {
            var json = JsonConvert.SerializeObject(summary);
            return JsonConvert.DeserializeObject<Rule10ValidationSummary>(json) ?? summary;
        }

        private static Rule10ValidationRequest CloneValidationRequest(Rule10ValidationRequest request)
        {
            var json = JsonConvert.SerializeObject(request);
            return JsonConvert.DeserializeObject<Rule10ValidationRequest>(json) ?? request;
        }

        private static Rule10ValidationSummary CreateBrowserPreview(Rule10ValidationSummary summary)
        {
            var browserRows = summary.ReviewRows.Take(BrowserPreviewRowLimit).ToList();
            var clone = CloneSummary(summary);
            clone.ReviewRows = browserRows;
            clone.DisplayedCount = browserRows.Count;
            clone.IsPreviewOnly = summary.IsPreviewOnly || summary.ReviewRows.Count > browserRows.Count;
            clone.PreviewLimit = clone.IsPreviewOnly ? browserRows.Count : 0;
            return clone;
        }

        private static void ApplyBrowserPreview(Rule10ValidationSummary summary)
        {
            var preview = CreateBrowserPreview(summary);
            summary.DisplayedCount = preview.DisplayedCount;
            summary.IsPreviewOnly = preview.IsPreviewOnly;
            summary.PreviewLimit = preview.PreviewLimit;
            summary.ReviewRows = preview.ReviewRows;
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase);

        private static void EnrichRule10DisplayValues(Rule10ValidationRowRecord row)
        {
            if (!row.DisplayValues.ContainsKey("RULE_LABEL"))
                row.DisplayValues["RULE_LABEL"] = row.ControlLabel;
        }

        private static string ReadValue(IReadOnlyDictionary<string, string?> values, string key) =>
            values.TryGetValue(key, out var value) ? value ?? "" : "";

        private sealed record IntegrityRuleDefinition(
            int RuleId,
            string RuleLabel,
            string CriteriaText,
            string TableName,
            string Severity,
            string CountSql,
            string TotalSql,
            string ReviewSql,
            string? SampleReviewSql = null,
            string? PrepSql = null);
    }
}
