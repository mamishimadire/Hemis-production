using System.Globalization;
using System.Text;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 12: "Active Students" — validates against the engagement's own uploaded Supabase
    // data instead of a live SQL Server connection. Ported from the Rule14/15 pattern.
    // 3-table rule: CREG (registrations), CRES (course status — filters to active/approved
    // courses), QUAL (qualification master). A student registration counts if its course is
    // currently active in CRES; PASS/FAIL is whether that registration's qualification code
    // exists in QUAL.
    public class Rule12Service : IRule12Service
    {
        private const int BrowserPreviewRowLimit = 10;
        // Safety ceiling on the full (export) load only - not a preview limit. Set to Excel's own
        // hard per-worksheet row limit (2^20), so nothing is ever artificially truncated below what
        // an .xlsx file could hold anyway. This does NOT fix the underlying issue: rows are still
        // buffered fully in memory (each as a Dictionary<string,string?> of every column) before
        // being written out, so a dataset anywhere close to this ceiling can still exhaust this
        // container's memory and crash the app for every user, not just whoever ran the export -
        // exactly what caused the original OutOfMemoryException, just at a higher threshold now.
        // The real fix is streaming rows directly into the output file as they're read from the
        // database instead of buffering them first; deferred for now in favor of raising this cap.
        private const int ExportRowSafetyLimit = 1_048_576;
        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IPendingValidationCacheService _pendingValidationCache;

        public Rule12Service(
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

        public async Task<Rule12TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule12TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule12TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoCregTable = FindFirst(tables, ["dbo_CREG", "dbo_creg", "CREG", "creg"], ["creg"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "dbo_qual", "QUAL", "qual"], ["qual"]),
                    AutoCresTable = FindFirst(tables, ["dbo_CRES", "dbo_cres", "CRES", "cres"], ["cres"])
                };
            }
            catch (Exception ex)
            {
                return new Rule12TableDiscoveryResult { Success = false, Error = ex.Message };
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

        public async Task<Rule12VerifyResult> VerifyTablesAsync(Rule12VerifyRequest request)
        {
            try
            {
                ValidateRequest(request.CregTable, request.QualTable, request.CresTable);

                var cfg = await ResolveColumnConfigAsync(
                    request.ClientId, request.CregTable, request.QualTable, request.CresTable,
                    request.CregStudentCol, request.CregQualCol, request.CregCourseCol,
                    request.QualJoinCol, request.QualDescCol,
                    request.CresCourseCol, request.CresStatusCol, request.CresStatusFilter,
                    request.CregExtra1Col, request.CregExtra2Col, request.CregFilterCol, request.CregFilterValues,
                    request.CregExtra3Col, request.CresExtra1Col);

                await EnsureRule12IndexesAsync(request.ClientId, request.CregTable, request.QualTable, request.CresTable, cfg);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                await using (var prepCommand = connection.CreateCommand())
                {
                    prepCommand.CommandText = BuildRule12PrepSql(schema, request.CregTable, request.QualTable, request.CresTable, cfg);
                    await prepCommand.ExecuteNonQueryAsync();
                }

                var (cregCount, qualCount, cresActiveCount, totalActiveStudents, matchedQuals, missingQuals) =
                    await GetCountsAsync(connection, schema, request.CregTable, request.QualTable);

                return new Rule12VerifyResult
                {
                    Success = true,
                    CregRecordCount = cregCount,
                    QualRecordCount = qualCount,
                    CresActiveCount = cresActiveCount,
                    TotalActiveStudents = totalActiveStudents,
                    MatchedQualCount = matchedQuals,
                    MissingQualCount = missingQuals
                };
            }
            catch (Exception ex)
            {
                return new Rule12VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule12ValidationSummary> RunValidationAsync(Rule12ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request.CregTable, request.QualTable, request.CresTable);

                var summary = await AnalyseAsync(request, includeAllReviewRows: true, failRowsOnlyForFullLoad: true);

                if (summary.Success && request.ClientId > 0)
                {
                    summary.SavedRunId = null;
                    var runId = await SaveValidationRunAsync(CloneValidationRequest(request), summary, userEmail, userName);
                    summary.SavedRunId = runId;

                    if (!string.IsNullOrWhiteSpace(userEmail))
                        _pendingValidationCache.ClearPending(12, request.ClientId, userEmail!);

                    summary.Warning = "Rule 12 analysis complete. Click Save Workspace to finalise for signoff.";
                }
                else if (summary.Success)
                {
                    summary.Warning = "Rule 12 analysis complete. Select a client to save the workspace.";
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule12ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule12ValidationSummary> GetExportSummaryAsync(Rule12ValidationRequest request)
        {
            ValidateRequest(request.CregTable, request.QualTable, request.CresTable);
            return await AnalyseAsync(request, includeAllReviewRows: true);
        }

        // Bypasses AnalyseAsync/LoadControlRowsAsync entirely - those buffer every row as a full
        // Dictionary<string,string?> before anything can be written out, which is fine for the
        // ~10-row browser preview but exhausts this container's memory on a real, large engagement
        // (confirmed OutOfMemoryException on a client with 450k+ rows even after raising the
        // in-memory cap to Excel's own row ceiling). This reads and writes one row at a time, so
        // memory use stays roughly constant no matter how large the population is.
        public async Task StreamCsvExportAsync(Rule12ValidationRequest request, Stream outputStream)
        {
            ValidateRequest(request.CregTable, request.QualTable, request.CresTable);

            var cfg = await ResolveColumnConfigAsync(
                request.ClientId, request.CregTable, request.QualTable, request.CresTable,
                request.CregStudentCol, request.CregQualCol, request.CregCourseCol,
                request.QualJoinCol, request.QualDescCol,
                request.CresCourseCol, request.CresStatusCol, request.CresStatusFilter,
                request.CregExtra1Col, request.CregExtra2Col, request.CregFilterCol, request.CregFilterValues,
                request.CregExtra3Col, request.CresExtra1Col);

            await EnsureRule12IndexesAsync(request.ClientId, request.CregTable, request.QualTable, request.CresTable, cfg);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule12PrepSql(schema, request.CregTable, request.QualTable, request.CresTable, cfg);
                await prepCommand.ExecuteNonQueryAsync();
            }

            await using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = false };

            // Column order here must track rule12_validation's actual output columns (see
            // BuildRule12PrepSql) - Course_Code is the already-matched CREG/CRES course code
            // (there's no separate raw CRES-side course code once joined), and CresStatusCol/
            // CresExtra1Col map to Course_Approval_Status/Course_Name respectively.
            var (extra1Alias, extra2Alias, filterAlias, extra3Alias) = ComputeRule12Aliases(cfg);

            var headerParts = new List<string> { cfg.CregStudentCol, cfg.CregQualCol, cfg.CregCourseCol };
            if (cfg.HasCregExtra1) headerParts.Add(cfg.CregExtra1Col);
            if (cfg.HasCregExtra2) headerParts.Add(cfg.CregExtra2Col);
            if (cfg.HasCregFilter) headerParts.Add(cfg.CregFilterCol);
            if (cfg.HasCregExtra3) headerParts.Add(cfg.CregExtra3Col);
            headerParts.Add(cfg.QualJoinCol);
            headerParts.Add(cfg.QualDescCol);
            headerParts.Add(cfg.CresStatusCol);
            if (cfg.HasCresExtra1) headerParts.Add(cfg.CresExtra1Col);
            headerParts.Add("Validation Result");
            headerParts.Add("Validation Explanation");
            await writer.WriteLineAsync(string.Join(",", headerParts.Select(StreamCsvEscape)));

            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT v.* FROM rule12_validation v ORDER BY ""Extract_Number"";";

            await using var reader = await command.ExecuteReaderAsync();

            var ordStudentNumber = reader.GetOrdinal("Student_Number");
            var ordQualificationCode = reader.GetOrdinal("Qualification_Code");
            var ordCourseCode = reader.GetOrdinal("Course_Code");
            var ordCregExtra1 = cfg.HasCregExtra1 ? reader.GetOrdinal(extra1Alias) : -1;
            var ordCregExtra2 = cfg.HasCregExtra2 ? reader.GetOrdinal(extra2Alias) : -1;
            var ordCregFilter = cfg.HasCregFilter ? reader.GetOrdinal(filterAlias) : -1;
            var ordCregExtra3 = cfg.HasCregExtra3 ? reader.GetOrdinal(extra3Alias) : -1;
            var ordQualCode = reader.GetOrdinal("QUAL_Qualification_Code");
            var ordQualName = reader.GetOrdinal("QUAL_Qualification_Name");
            var ordCourseApprovalStatus = reader.GetOrdinal("Course_Approval_Status");
            var ordCresExtra1 = cfg.HasCresExtra1 ? reader.GetOrdinal("Course_Name") : -1;
            var ordValidationResult = reader.GetOrdinal("Validation_Result");
            var ordValidationReason = reader.GetOrdinal("Validation_Reason");

            string GetVal(int ord) => ord < 0 || reader.IsDBNull(ord)
                ? ""
                : Convert.ToString(reader.GetValue(ord), CultureInfo.InvariantCulture) ?? "";

            var rowValues = new List<string>(12);
            while (await reader.ReadAsync())
            {
                rowValues.Clear();
                rowValues.Add(StreamCsvEscape(GetVal(ordStudentNumber)));
                rowValues.Add(StreamCsvEscape(GetVal(ordQualificationCode)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCourseCode)));
                if (cfg.HasCregExtra1) rowValues.Add(StreamCsvEscape(GetVal(ordCregExtra1)));
                if (cfg.HasCregExtra2) rowValues.Add(StreamCsvEscape(GetVal(ordCregExtra2)));
                if (cfg.HasCregFilter) rowValues.Add(StreamCsvEscape(GetVal(ordCregFilter)));
                if (cfg.HasCregExtra3) rowValues.Add(StreamCsvEscape(GetVal(ordCregExtra3)));
                rowValues.Add(StreamCsvEscape(GetVal(ordQualCode)));
                rowValues.Add(StreamCsvEscape(GetVal(ordQualName)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCourseApprovalStatus)));
                if (cfg.HasCresExtra1) rowValues.Add(StreamCsvEscape(GetVal(ordCresExtra1)));
                rowValues.Add(StreamCsvEscape(GetVal(ordValidationResult)));
                rowValues.Add(StreamCsvEscape(GetVal(ordValidationReason)));
                await writer.WriteLineAsync(string.Join(",", rowValues));
            }

            await writer.FlushAsync();
        }

        private static string StreamCsvEscape(string? val)
        {
            if (string.IsNullOrEmpty(val))
                return "";
            if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
                return $"\"{val.Replace("\"", "\"\"")}\"";
            return val;
        }

        public Task<Rule12ValidationSummary?> GetPendingValidationPreviewAsync(int clientId, string reviewerEmail)
        {
            var pending = _pendingValidationCache.GetPending<Rule12ValidationRequest, Rule12ValidationSummary>(12, clientId, reviewerEmail);
            if (pending == null)
                return Task.FromResult<Rule12ValidationSummary?>(null);

            var preview = CloneSummary(pending.Summary);
            preview.SavedRunId = null;
            preview.Warning = "This Rule 12 validation is still pending. Click Save Workspace to write it to the system database.";
            ApplyBrowserPreview(preview);
            return Task.FromResult<Rule12ValidationSummary?>(preview);
        }

        public Task<bool> HasPendingValidationAsync(int clientId, string reviewerEmail)
            => Task.FromResult(_pendingValidationCache.HasPending(12, clientId, reviewerEmail));

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule12WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 12);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (deserializedSummary != null && includeSummary)
            {
                deserializedSummary = await ExpandAndPersistSavedSummaryIfNeededAsync(row.RunId, deserializedSummary, clientId);
                ApplyBrowserPreview(deserializedSummary);
            }
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule12WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                CregTable = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_CREG" : row.StudTable,
                QualTable = deserializedSummary?.QualTable ?? (string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_QUAL" : row.DeceasedTable),
                CresTable = deserializedSummary?.CresTable ?? (string.IsNullOrWhiteSpace(row.StudColumn) ? "dbo_CRES" : row.StudColumn),
                CregStudentCol = deserializedSummary?.CregStudentCol ?? "_007",
                CregQualCol = deserializedSummary?.CregQualCol ?? "_001",
                CregCourseCol = deserializedSummary?.CregCourseCol ?? "_030",
                QualJoinCol = deserializedSummary?.QualJoinCol ?? "_001",
                QualDescCol = deserializedSummary?.QualDescCol ?? "_003",
                CresCourseCol = deserializedSummary?.CresCourseCol ?? "_030",
                CresStatusCol = deserializedSummary?.CresStatusCol ?? "_031",
                CresStatusFilter = deserializedSummary?.CresStatusFilter ?? "A",
                CregExtra1Col = deserializedSummary?.CregExtra1Col ?? "_064",
                CregExtra2Col = deserializedSummary?.CregExtra2Col ?? "_032",
                CregFilterCol = deserializedSummary?.CregFilterCol ?? "_051",
                CregFilterValues = deserializedSummary?.CregFilterValues ?? "",
                CregExtra3Col = deserializedSummary?.CregExtra3Col ?? "_018",
                CresExtra1Col = deserializedSummary?.CresExtra1Col ?? "_058",
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

        public async Task<Rule12RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 12);
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

            var review = new Rule12RunReviewViewModel
            {
                RunId = row.RunId,
                ClientId = row.ClientId,
                IsCurrentRun = row.IsCurrent,
                EngagementName = row.EngagementName,
                MaconomyNumber = row.MaconomyNumber,
                IsWorkspaceSaved = await _systemDb.IsWorkspaceSavedAsync(runId),
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

        public async Task<Rule12WorkspaceSaveResult> SaveWorkspaceAsync(Rule12ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                ValidateRequest(request.CregTable, request.QualTable, request.CresTable);

                if (request.RunId.HasValue && request.RunId.Value > 0)
                {
                    var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                    if (!clientId.HasValue || clientId.Value != request.ClientId)
                        return new Rule12WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                    await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                    var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                    await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                    {
                        RunId = request.RunId.Value,
                        ClientId = request.ClientId,
                        StudTable = request.CregTable,
                        DeceasedTable = request.QualTable,
                        StudColumn = request.CresTable,
                        DeceasedColumn = ""
                    }, reviewerName ?? reviewerEmail);

                    if (!string.IsNullOrWhiteSpace(reviewerEmail))
                        _pendingValidationCache.ClearPending(12, request.ClientId, reviewerEmail);

                    var currentWorkspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                    return new Rule12WorkspaceSaveResult
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

                var pending = _pendingValidationCache.GetPending<Rule12ValidationRequest, Rule12ValidationSummary>(12, request.ClientId, reviewerEmail);
                if (pending == null)
                    return new Rule12WorkspaceSaveResult { Success = false, Error = "Run Rule 12 first so the current workspace is written to the system database." };

                if (!RequestsMatchForPendingSave(request, pending.Request))
                    return new Rule12WorkspaceSaveResult { Success = false, Error = "Workspace settings changed after validation. Run Rule 12 again before saving." };

                var summaryToSave = CloneSummary(pending.Summary);
                if (summaryToSave.IsPreviewOnly || summaryToSave.ReviewRows.Count < summaryToSave.TotalValidated)
                    summaryToSave = await AnalyseAsync(pending.Request, includeAllReviewRows: true, failRowsOnlyForFullLoad: true);

                summaryToSave.SavedRunId = null;
                var savedRunId = await SaveValidationRunAsync(pending.Request, summaryToSave, reviewerEmail, reviewerName);
                _pendingValidationCache.ClearPending(12, request.ClientId, reviewerEmail);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = savedRunId,
                    ClientId = request.ClientId,
                    StudTable = request.CregTable,
                    DeceasedTable = request.QualTable,
                    StudColumn = request.CresTable,
                    DeceasedColumn = ""
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule12WorkspaceSaveResult
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
                return new Rule12WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule12WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule12WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                if (!string.IsNullOrWhiteSpace(reviewerEmail))
                    _pendingValidationCache.ClearPending(12, clientId.Value, reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule12WorkspaceSaveResult
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
                return new Rule12WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 12 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 12 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 12 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule12ValidationRequest request)
        {
            ValidateRequest(request.CregTable, request.QualTable, request.CresTable);

            var cfg = await ResolveColumnConfigAsync(
                request.ClientId, request.CregTable, request.QualTable, request.CresTable,
                request.CregStudentCol, request.CregQualCol, request.CregCourseCol,
                request.QualJoinCol, request.QualDescCol,
                request.CresCourseCol, request.CresStatusCol, request.CresStatusFilter,
                request.CregExtra1Col, request.CregExtra2Col, request.CregFilterCol, request.CregFilterValues,
                request.CregExtra3Col, request.CresExtra1Col);

            var sql = $@"-- HEMIS RULE 12: ACTIVE STUDENTS - 100% POPULATION
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Rule:
--   Active courses = {request.CresTable} where {cfg.CresStatusCol} = '{cfg.CresStatusFilter}'
--   Active students = {request.CregTable} rows whose course matches an active course
--   PASS = active student's qualification code exists in {request.QualTable}
--   FAIL = active student's qualification code does not exist in {request.QualTable}

{BuildRule12PrepSql("{schema}", request.CregTable, request.QualTable, request.CresTable, cfg)}

-- Full extracted population result
SELECT * FROM rule12_validation ORDER BY ""Extract_Number"";

-- Summary
SELECT
    COUNT(*) AS total_active_students,
    SUM(CASE WHEN ""Validation_Result"" = 'PASS' THEN 1 ELSE 0 END) AS pass_count,
    SUM(CASE WHEN ""Validation_Result"" = 'FAIL' THEN 1 ELSE 0 END) AS fail_count,
    ROUND(SUM(CASE WHEN ""Validation_Result"" = 'FAIL' THEN 1 ELSE 0 END) * 100.0
         / NULLIF(COUNT(*), 0), 2) AS exception_rate_pct
FROM rule12_validation;";

            return sql.Trim();
        }

        // failRowsOnlyForFullLoad: when includeAllReviewRows is true, load only FAIL rows (plus
        // the small browser preview) instead of the entire population. Reading and materializing
        // every row into a Dictionary costs ~100s+ on a real 450k-row engagement (measured) even
        // though the SQL itself completes in a few seconds — almost all of that cost is wasted
        // because only the FAIL rows get persisted (SaveValidationRunAsync) and only the first
        // page gets shown to the browser. Used by the interactive "Run Validation"/"Save
        // Workspace" paths. Full-population loads (exports, explicit "expand to full") still pass
        // failRowsOnlyForFullLoad: false and get every row, since that's genuinely what they need.
        private async Task<Rule12ValidationSummary> AnalyseAsync(Rule12ValidationRequest request, bool includeAllReviewRows, bool failRowsOnlyForFullLoad = false)
        {
            var cfg = await ResolveColumnConfigAsync(
                request.ClientId, request.CregTable, request.QualTable, request.CresTable,
                request.CregStudentCol, request.CregQualCol, request.CregCourseCol,
                request.QualJoinCol, request.QualDescCol,
                request.CresCourseCol, request.CresStatusCol, request.CresStatusFilter,
                request.CregExtra1Col, request.CregExtra2Col, request.CregFilterCol, request.CregFilterValues,
                request.CregExtra3Col, request.CresExtra1Col);

            await EnsureRule12IndexesAsync(request.ClientId, request.CregTable, request.QualTable, request.CresTable, cfg);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule12PrepSql(schema, request.CregTable, request.QualTable, request.CresTable, cfg);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var (cregCount, qualCount, cresActiveCount, totalActiveStudents, matchedQuals, missingQuals) =
                await GetCountsAsync(connection, schema, request.CregTable, request.QualTable);

            List<Rule12ValidationRowRecord> reviewRows;
            if (includeAllReviewRows && failRowsOnlyForFullLoad)
            {
                reviewRows = await LoadControlRowsAsync(connection, ExportRowSafetyLimit,
                    request.CregTable, request.QualTable, request.CresTable, cfg.CregQualCol, cfg.QualJoinCol, cfg.CresStatusCol, cfg.CresStatusFilter,
                    resultFilter: "FAIL");
            }
            else
            {
                reviewRows = await LoadControlRowsAsync(connection, includeAllReviewRows ? ExportRowSafetyLimit : BrowserPreviewRowLimit,
                    request.CregTable, request.QualTable, request.CresTable, cfg.CregQualCol, cfg.QualJoinCol, cfg.CresStatusCol, cfg.CresStatusFilter);
            }
            reviewRows = NormalizeReviewRows(reviewRows);

            var controlSummaries = BuildControlSummaries(totalActiveStudents, matchedQuals, request.CregTable, request.QualTable, request.CresTable, cfg.CregQualCol, cfg.QualJoinCol, cfg.CresStatusCol, cfg.CresStatusFilter);
            var totalValidated = controlSummaries.Sum(x => x.TotalCount);
            var passCount = controlSummaries.Sum(x => x.PassCount);
            var failCount = controlSummaries.Sum(x => x.FailCount);
            var isPreviewOnly = !includeAllReviewRows && totalValidated > reviewRows.Count;

            return new Rule12ValidationSummary
            {
                Success = true,
                CregRecordCount = cregCount,
                QualRecordCount = qualCount,
                CresActiveCount = cresActiveCount,
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
                CregTable = request.CregTable,
                QualTable = request.QualTable,
                CresTable = request.CresTable,
                CregStudentCol = cfg.CregStudentCol,
                CregQualCol = cfg.CregQualCol,
                CregCourseCol = cfg.CregCourseCol,
                QualJoinCol = cfg.QualJoinCol,
                QualDescCol = cfg.QualDescCol,
                CresCourseCol = cfg.CresCourseCol,
                CresStatusCol = cfg.CresStatusCol,
                CresStatusFilter = cfg.CresStatusFilter,
                CregExtra1Col = cfg.HasCregExtra1 ? cfg.CregExtra1Col : "",
                CregExtra2Col = cfg.HasCregExtra2 ? cfg.CregExtra2Col : "",
                CregFilterCol = cfg.HasCregFilter ? cfg.CregFilterCol : "",
                CregFilterValues = cfg.CregFilterValues,
                CregExtra3Col = cfg.HasCregExtra3 ? cfg.CregExtra3Col : "",
                CresExtra1Col = cfg.HasCresExtra1 ? cfg.CresExtra1Col : "",
                TableLinkageText = $"{request.CregTable}.{cfg.CregQualCol} = {request.QualTable}.{cfg.QualJoinCol} | {request.CregTable}.{cfg.CregCourseCol} = {request.CresTable}.{cfg.CresCourseCol} WHERE {request.CresTable}.{cfg.CresStatusCol} = '{cfg.CresStatusFilter}'",
                RuleModeText = $"Active students from {request.CregTable} (CRES.{cfg.CresStatusCol}='{cfg.CresStatusFilter}') qualification codes tested against {request.QualTable}.{cfg.QualJoinCol}",
                ProcedureSteps = BuildProcedureSteps(request.CregTable, request.QualTable, request.CresTable, cfg.CresStatusCol, cfg.CresStatusFilter),
                ClientId = request.ClientId,
                ControlSummaries = controlSummaries,
                ReviewRows = reviewRows,
                Warning = failRowsOnlyForFullLoad
                    ? "Rule 12 completed against the full active student population. Counts reflect every record; only exception (FAIL) rows are retained for review and signoff."
                    : includeAllReviewRows
                        ? "Rule 12 completed with the full active student population result set."
                        : "Counts reflect the full active student population result set. Browser review rows are limited for performance."
            };
        }

        private async Task<int> SaveValidationRunAsync(Rule12ValidationRequest request, Rule12ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 12);

            var failRows = summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList();

            // Persist only the fail rows in ExceptionsJSON/ResultsJSON — storing all PASS rows for
            // large datasets (hundreds of thousands of records) would bloat the DB row unmanageably.
            var persistedSummary = CloneSummary(summary);
            persistedSummary.ReviewRows = failRows.ToList();
            persistedSummary.DisplayedCount = failRows.Count;
            persistedSummary.IsPreviewOnly = false;

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 12,
                RuleName = "Course Selection from dbo_CREG",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.CregTable,
                DeceasedTable = request.QualTable,
                StudColumn = request.CresTable,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(failRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(persistedSummary))
            }, userEmail, userName);

            summary.SavedRunId = runId;
            return runId;
        }

        // ── Column configuration resolution (degrades optional columns to NULL when the
        //    uploaded table doesn't have them, instead of hard-failing the whole rule) ──────

        private sealed class Rule12ColumnConfig
        {
            public string CregStudentCol = "_007";
            public string CregQualCol = "_001";
            public string CregCourseCol = "_030";
            public string QualJoinCol = "_001";
            public string QualDescCol = "_003";
            public string CresCourseCol = "_030";
            public string CresStatusCol = "_031";
            public string CresStatusFilter = "A";
            public string CregExtra1Col = "";
            public string CregExtra2Col = "";
            public string CregFilterCol = "";
            public string CregFilterValues = "";
            public string CregExtra3Col = "";
            public string CresExtra1Col = "";
            public bool HasCregExtra1;
            public bool HasCregExtra2;
            public bool HasCregFilter;
            public bool HasCregExtra3;
            public bool HasCresExtra1;
        }

        private async Task<Rule12ColumnConfig> ResolveColumnConfigAsync(
            int clientId, string cregTable, string qualTable, string cresTable,
            string? cregStudentCol, string? cregQualCol, string? cregCourseCol,
            string? qualJoinCol, string? qualDescCol,
            string? cresCourseCol, string? cresStatusCol, string? cresStatusFilter,
            string? cregExtra1Col, string? cregExtra2Col, string? cregFilterCol, string? cregFilterValues,
            string? cregExtra3Col, string? cresExtra1Col)
        {
            var cfg = new Rule12ColumnConfig
            {
                CregStudentCol = string.IsNullOrWhiteSpace(cregStudentCol) ? "_007" : cregStudentCol,
                CregQualCol = string.IsNullOrWhiteSpace(cregQualCol) ? "_001" : cregQualCol,
                CregCourseCol = string.IsNullOrWhiteSpace(cregCourseCol) ? "_030" : cregCourseCol,
                QualJoinCol = string.IsNullOrWhiteSpace(qualJoinCol) ? "_001" : qualJoinCol,
                QualDescCol = string.IsNullOrWhiteSpace(qualDescCol) ? "_003" : qualDescCol,
                CresCourseCol = string.IsNullOrWhiteSpace(cresCourseCol) ? "_030" : cresCourseCol,
                CresStatusCol = string.IsNullOrWhiteSpace(cresStatusCol) ? "_031" : cresStatusCol,
                CresStatusFilter = string.IsNullOrWhiteSpace(cresStatusFilter) ? "A" : cresStatusFilter.Trim(),
                CregExtra1Col = cregExtra1Col ?? "",
                CregExtra2Col = cregExtra2Col ?? "",
                CregFilterCol = cregFilterCol ?? "",
                CregFilterValues = cregFilterValues?.Trim() ?? "",
                CregExtra3Col = cregExtra3Col ?? "",
                CresExtra1Col = cresExtra1Col ?? ""
            };

            var cregColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(clientId, cregTable), StringComparer.OrdinalIgnoreCase);
            var qualColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(clientId, qualTable), StringComparer.OrdinalIgnoreCase);
            var cresColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(clientId, cresTable), StringComparer.OrdinalIgnoreCase);

            EnsureHasColumns(cregTable, cregColumns, cfg.CregStudentCol, cfg.CregQualCol, cfg.CregCourseCol);
            EnsureHasColumns(qualTable, qualColumns, cfg.QualJoinCol, cfg.QualDescCol);
            EnsureHasColumns(cresTable, cresColumns, cfg.CresCourseCol, cfg.CresStatusCol);

            cfg.HasCregExtra1 = !string.IsNullOrWhiteSpace(cfg.CregExtra1Col) && cregColumns.Contains(cfg.CregExtra1Col);
            cfg.HasCregExtra2 = !string.IsNullOrWhiteSpace(cfg.CregExtra2Col) && cregColumns.Contains(cfg.CregExtra2Col);
            cfg.HasCregFilter = !string.IsNullOrWhiteSpace(cfg.CregFilterCol) && cregColumns.Contains(cfg.CregFilterCol);
            cfg.HasCregExtra3 = !string.IsNullOrWhiteSpace(cfg.CregExtra3Col) && cregColumns.Contains(cfg.CregExtra3Col);
            cfg.HasCresExtra1 = !string.IsNullOrWhiteSpace(cfg.CresExtra1Col) && cresColumns.Contains(cfg.CresExtra1Col);

            return cfg;
        }

        private static void EnsureHasColumns(string tableName, IReadOnlyCollection<string> availableColumns, params string[] requiredColumns)
        {
            var missing = requiredColumns.Where(required => !availableColumns.Contains(required, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"Table {tableName} is missing required column(s): {string.Join(", ", missing)}.");
        }

        // Uploaded engagement tables have no indexes beyond their primary key, and the
        // join/filter columns are only known once an analyst configures the rule — so a large
        // CREG/QUAL/CRES table (real engagements have seen 450k+ row CREG tables) forces a full
        // sequential scan with a function call per row on every single run. Building the
        // expression index once, up front, means every run after the first is fast.
        private async Task EnsureRule12IndexesAsync(int clientId, string cregTable, string qualTable, string cresTable, Rule12ColumnConfig cfg)
        {
            await _datasets.EnsureJoinIndexAsync(clientId, cresTable, cfg.CresCourseCol);
            await _datasets.EnsureJoinIndexAsync(clientId, cresTable, cfg.CresStatusCol);
            await _datasets.EnsureJoinIndexAsync(clientId, cregTable, cfg.CregCourseCol);
            await _datasets.EnsureJoinIndexAsync(clientId, qualTable, cfg.QualJoinCol);
        }

        // ── SQL builders (Postgres) ─────────────────────────────────────────────────────

        // Shared by BuildRule12PrepSql (which creates these columns) and StreamCsvExportAsync
        // (which reads them back by name) - computed once so the two can never drift apart.
        private static (string Extra1Alias, string Extra2Alias, string FilterAlias, string Extra3Alias) ComputeRule12Aliases(Rule12ColumnConfig cfg) => (
            cfg.HasCregExtra1 ? $"CREG_{cfg.CregExtra1Col.TrimStart('_')}" : "CREG__EXTRA1",
            cfg.HasCregExtra2 ? $"CREG_{cfg.CregExtra2Col.TrimStart('_')}" : "CREG__EXTRA2",
            cfg.HasCregFilter ? $"CREG_{cfg.CregFilterCol.TrimStart('_')}" : "CREG__FILTER",
            cfg.HasCregExtra3 ? $"CREG_{cfg.CregExtra3Col.TrimStart('_')}" : "CREG__EXTRA3"
        );

        private static string BuildRule12PrepSql(string schema, string cregTable, string qualTable, string cresTable, Rule12ColumnConfig cfg)
        {
            var statusFilter = EscapeSqlString(cfg.CresStatusFilter.ToUpperInvariant());
            var extra1Sql = cfg.HasCregExtra1 ? $@"CAST(cr.""{cfg.CregExtra1Col}"" AS text)" : "NULL::text";
            var extra2Sql = cfg.HasCregExtra2 ? $@"CAST(cr.""{cfg.CregExtra2Col}"" AS text)" : "NULL::text";
            var filterSql = cfg.HasCregFilter ? $@"UPPER(TRIM(CAST(cr.""{cfg.CregFilterCol}"" AS text)))" : "NULL::text";
            var extra3Sql = cfg.HasCregExtra3 ? $@"CAST(cr.""{cfg.CregExtra3Col}"" AS text)" : "NULL::text";
            var courseNameSql = cfg.HasCresExtra1 ? $@"CAST(cres.""{cfg.CresExtra1Col}"" AS text)" : "NULL::text";

            var (extra1Alias, extra2Alias, filterAlias, extra3Alias) = ComputeRule12Aliases(cfg);

            var filterWhere = BuildFilterWhereClause(cfg.HasCregFilter ? cfg.CregFilterCol : "", cfg.CregFilterValues);

            return $@"
DROP TABLE IF EXISTS rule12_active_courses;
DROP TABLE IF EXISTS rule12_extracted_population;
DROP TABLE IF EXISTS rule12_qual_master;
DROP TABLE IF EXISTS rule12_validation;

-- Dedup by course code alone: if CRES has ambiguous duplicate rows for the same course
-- code (differing name/status), keep just one so the join below can't fan out into
-- duplicate registration rows for the same student+course.
CREATE TEMP TABLE rule12_active_courses AS
SELECT DISTINCT ON (course_code)
    course_code, course_name, course_approval_status
FROM (
    SELECT
        UPPER(TRIM(CAST(cres.""{cfg.CresCourseCol}"" AS text))) AS course_code,
        {courseNameSql} AS course_name,
        CAST(cres.""{cfg.CresStatusCol}"" AS text) AS course_approval_status
    FROM ""{schema}"".""{cresTable}"" cres
    WHERE UPPER(TRIM(CAST(cres.""{cfg.CresStatusCol}"" AS text))) = '{statusFilter}'
      AND cres.""{cfg.CresCourseCol}"" IS NOT NULL
) x
ORDER BY course_code;

CREATE INDEX ON rule12_active_courses (course_code);
ANALYZE rule12_active_courses;

CREATE TEMP TABLE rule12_extracted_population AS
SELECT
    ROW_NUMBER() OVER (ORDER BY cr.""{cfg.CregStudentCol}"", cr.""{cfg.CregQualCol}"", cr.""{cfg.CregCourseCol}"") AS extract_number,
    CAST(cr.""{cfg.CregStudentCol}"" AS text) AS student_number,
    UPPER(TRIM(CAST(cr.""{cfg.CregQualCol}"" AS text))) AS qualification_code,
    UPPER(TRIM(CAST(cr.""{cfg.CregCourseCol}"" AS text))) AS course_code,
    {extra1Sql} AS ""{extra1Alias}"",
    {extra2Sql} AS ""{extra2Alias}"",
    {filterSql} AS ""{filterAlias}"",
    {extra3Sql} AS ""{extra3Alias}"",
    ca.course_name,
    ca.course_approval_status
FROM ""{schema}"".""{cregTable}"" cr
INNER JOIN rule12_active_courses ca
    ON ca.course_code = UPPER(TRIM(CAST(cr.""{cfg.CregCourseCol}"" AS text)))
WHERE cr.""{cfg.CregQualCol}"" IS NOT NULL
  AND TRIM(CAST(cr.""{cfg.CregQualCol}"" AS text)) <> ''{filterWhere};

CREATE INDEX ON rule12_extracted_population (qualification_code);
ANALYZE rule12_extracted_population;

-- Dedup by qualification code alone for the same reason as CRES above.
CREATE TEMP TABLE rule12_qual_master AS
SELECT DISTINCT ON (qualification_code)
    qualification_code, qualification_name
FROM (
    SELECT
        UPPER(TRIM(CAST(q.""{cfg.QualJoinCol}"" AS text))) AS qualification_code,
        CAST(q.""{cfg.QualDescCol}"" AS text) AS qualification_name
    FROM ""{schema}"".""{qualTable}"" q
    WHERE q.""{cfg.QualJoinCol}"" IS NOT NULL
      AND TRIM(CAST(q.""{cfg.QualJoinCol}"" AS text)) <> ''
) y
ORDER BY qualification_code;

CREATE INDEX ON rule12_qual_master (qualification_code);
ANALYZE rule12_qual_master;

CREATE TEMP TABLE rule12_validation AS
SELECT
    p.extract_number AS ""Extract_Number"",
    p.student_number AS ""Student_Number"",
    p.qualification_code AS ""Qualification_Code"",
    p.course_code AS ""Course_Code"",
    p.course_name AS ""Course_Name"",
    p.course_approval_status AS ""Course_Approval_Status"",
    p.""{extra1Alias}"" AS ""{extra1Alias}"",
    p.""{extra2Alias}"" AS ""{extra2Alias}"",
    p.""{filterAlias}"" AS ""{filterAlias}"",
    p.""{extra3Alias}"" AS ""{extra3Alias}"",
    q.qualification_code AS ""QUAL_Qualification_Code"",
    q.qualification_name AS ""QUAL_Qualification_Name"",
    CASE WHEN q.qualification_code IS NOT NULL THEN 'PASS' ELSE 'FAIL' END AS ""Validation_Result"",
    CASE
        WHEN q.qualification_code IS NOT NULL THEN 'Active student qualification exists in {qualTable}.'
        ELSE 'Active student qualification does not exist in {qualTable}.'
    END AS ""Validation_Reason""
FROM rule12_extracted_population p
LEFT JOIN rule12_qual_master q ON q.qualification_code = p.qualification_code;

ANALYZE rule12_validation;";
        }

        private static string BuildFilterWhereClause(string filterCol, string filterValues)
        {
            if (string.IsNullOrWhiteSpace(filterCol) || string.IsNullOrWhiteSpace(filterValues))
                return "";

            var values = filterValues
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => EscapeSqlString(v.ToUpperInvariant()))
                .Where(v => v.Length > 0)
                .Select(v => $"'{v}'")
                .ToList();

            return values.Count == 0
                ? ""
                : $@"
  AND UPPER(TRIM(CAST(cr.""{filterCol}"" AS text))) IN ({string.Join(", ", values)})";
        }

        private static async Task<(int cregCount, int qualCount, int cresActiveCount, int totalActiveStudents, int matchedQuals, int missingQuals)>
            GetCountsAsync(NpgsqlConnection connection, string schema, string cregTable, string qualTable)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT
    (SELECT COUNT(*) FROM ""{schema}"".""{cregTable}"") AS creg_count,
    (SELECT COUNT(*) FROM ""{schema}"".""{qualTable}"") AS qual_count,
    (SELECT COUNT(*) FROM rule12_active_courses) AS cres_active_count,
    (SELECT COUNT(*) FROM rule12_validation) AS total_active_students,
    (SELECT COUNT(*) FROM rule12_validation WHERE ""Validation_Result"" = 'PASS') AS matched_quals,
    (SELECT COUNT(*) FROM rule12_validation WHERE ""Validation_Result"" = 'FAIL') AS missing_quals;";
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return (GetInt(reader, 0), GetInt(reader, 1), GetInt(reader, 2), GetInt(reader, 3), GetInt(reader, 4), GetInt(reader, 5));
            return (0, 0, 0, 0, 0, 0);
        }

        private async Task<List<Rule12ValidationRowRecord>> LoadControlRowsAsync(
            NpgsqlConnection connection, int? maxRows,
            string cregTable, string qualTable, string cresTable,
            string cregQualCol, string qualJoinCol, string cresStatusCol, string cresStatusFilter,
            string? resultFilter = null)
        {
            var limitClause = maxRows.HasValue && maxRows.Value > 0 ? $"LIMIT {maxRows.Value}" : "";
            var whereClause = resultFilter == "FAIL" ? @"WHERE v.""Validation_Result"" = 'FAIL'" : "";
            var controlLabel = EscapeSqlString($"CONTROL 1: {cregTable}.{cregQualCol} = {qualTable}.{qualJoinCol} WHERE {cresTable}.{cresStatusCol} = '{cresStatusFilter}'");

            await using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT
    'Control_1' AS ""Control_Type"",
    '{controlLabel}' AS ""Control_Label"",
    v.*
FROM rule12_validation v
{whereClause}
ORDER BY ""Extract_Number""
{limitClause};";

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule12ValidationRowRecord>();
            while (await reader.ReadAsync())
            {
                var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    displayValues[reader.GetName(i)] = reader.IsDBNull(i)
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }

                rows.Add(new Rule12ValidationRowRecord
                {
                    ValidationNumber = rows.Count + 1,
                    ControlType = ReadValue(displayValues, "Control_Type"),
                    ControlLabel = ReadValue(displayValues, "Control_Label"),
                    ValidationResult = ReadValue(displayValues, "Validation_Result"),
                    ValidationExplanation = ReadValue(displayValues, "Validation_Reason"),
                    DisplayValues = displayValues
                });

                EnrichRule12DisplayValues(rows[^1]);
            }

            return rows;
        }

        private static List<Rule12ControlSummaryItemViewModel> BuildControlSummaries(
            int totalActiveStudents, int matchedQuals,
            string cregTable, string qualTable, string cresTable,
            string cregQualCol, string qualJoinCol, string cresStatusCol, string cresStatusFilter)
        {
            return new List<Rule12ControlSummaryItemViewModel>
            {
                BuildControlSummary(
                    "Control_1",
                    "Control 1",
                    $"{cregTable}.{cregQualCol} = {qualTable}.{qualJoinCol} WHERE {cresTable}.{cresStatusCol} = '{cresStatusFilter}'",
                    totalActiveStudents,
                    matchedQuals)
            };
        }

        private static Rule12ControlSummaryItemViewModel BuildControlSummary(string controlType, string controlLabel, string criteriaText, int totalCount, int passCount)
        {
            var failCount = Math.Max(totalCount - passCount, 0);
            return new Rule12ControlSummaryItemViewModel
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

        private static List<Rule12ValidationRowRecord> NormalizeReviewRows(IEnumerable<Rule12ValidationRowRecord>? rows) =>
            (rows ?? Enumerable.Empty<Rule12ValidationRowRecord>())
                .Select((row, index) => { row.ValidationNumber = index + 1; return row; })
                .ToList();

        private async Task<Rule12ValidationSummary> ExpandAndPersistSavedSummaryIfNeededAsync(int runId, Rule12ValidationSummary summary, int clientId)
        {
            // Saved runs persist FAIL rows only (see SaveValidationRunAsync) — PASS rows are
            // never stored, so ReviewRows.Count will be far below TotalValidated by design
            // whenever most of the population passes. That is "complete" for workspace/status
            // purposes, not a truncated preview needing re-expansion. Comparing against
            // FailCount (not TotalValidated) is what actually detects a genuinely incomplete
            // save. Callers that need the full PASS+FAIL population (export) go through
            // GetExportSummaryAsync separately and aren't affected by this early return.
            if (!summary.IsPreviewOnly && summary.ReviewRows.Count >= summary.FailCount)
                return summary;

            if (string.IsNullOrWhiteSpace(summary.CregTable) || string.IsNullOrWhiteSpace(summary.QualTable) || string.IsNullOrWhiteSpace(summary.CresTable))
                return summary;

            try
            {
                var expanded = await AnalyseAsync(new Rule12ValidationRequest
                {
                    ClientId = clientId,
                    RunId = summary.SavedRunId,
                    CregTable = summary.CregTable,
                    QualTable = summary.QualTable,
                    CresTable = summary.CresTable,
                    CregStudentCol = summary.CregStudentCol,
                    CregQualCol = summary.CregQualCol,
                    CregCourseCol = summary.CregCourseCol,
                    QualJoinCol = summary.QualJoinCol,
                    QualDescCol = summary.QualDescCol,
                    CresCourseCol = summary.CresCourseCol,
                    CresStatusCol = summary.CresStatusCol,
                    CresStatusFilter = summary.CresStatusFilter,
                    CregExtra1Col = summary.CregExtra1Col,
                    CregExtra2Col = summary.CregExtra2Col,
                    CregFilterCol = summary.CregFilterCol,
                    CregFilterValues = summary.CregFilterValues,
                    CregExtra3Col = summary.CregExtra3Col,
                    CresExtra1Col = summary.CresExtra1Col
                }, includeAllReviewRows: true);

                expanded.Timestamp = string.IsNullOrWhiteSpace(summary.Timestamp) ? expanded.Timestamp : summary.Timestamp;
                expanded.ClientId = summary.ClientId;
                expanded.SavedRunId = summary.SavedRunId;
                expanded.Warning = string.IsNullOrWhiteSpace(summary.Warning)
                    ? "Saved Rule 12 results were expanded from the stored browser preview to the full result set."
                    : $"{summary.Warning} Full saved results were reloaded from the saved Rule 12 configuration.";

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

        private async Task UpdateStoredSummaryAsync(int runId, Rule12ValidationSummary summary)
        {
            var failRows = summary.ReviewRows.Where(r => string.Equals(r.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase)).ToList();
            var persistedSummary = CloneSummary(summary);
            persistedSummary.ReviewRows = failRows.ToList();
            persistedSummary.DisplayedCount = failRows.Count;
            persistedSummary.IsPreviewOnly = false;

            await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = summary.ClientId,
                RuleNumber = 12,
                RuleName = "Course Selection from dbo_CREG",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = summary.CregTable,
                DeceasedTable = summary.QualTable,
                StudColumn = summary.CresTable,
                DeceasedColumn = "",
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(failRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(persistedSummary))
            }, null, null);
        }

        private static Rule12ValidationSummary CloneSummary(Rule12ValidationSummary summary) => new()
        {
            Success = summary.Success,
            CregRecordCount = summary.CregRecordCount,
            QualRecordCount = summary.QualRecordCount,
            CresActiveCount = summary.CresActiveCount,
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
            CregTable = summary.CregTable,
            QualTable = summary.QualTable,
            CresTable = summary.CresTable,
            CregStudentCol = summary.CregStudentCol,
            CregQualCol = summary.CregQualCol,
            CregCourseCol = summary.CregCourseCol,
            QualJoinCol = summary.QualJoinCol,
            QualDescCol = summary.QualDescCol,
            CresCourseCol = summary.CresCourseCol,
            CresStatusCol = summary.CresStatusCol,
            CresStatusFilter = summary.CresStatusFilter,
            CregExtra1Col = summary.CregExtra1Col,
            CregExtra2Col = summary.CregExtra2Col,
            CregFilterCol = summary.CregFilterCol,
            CregFilterValues = summary.CregFilterValues,
            CregExtra3Col = summary.CregExtra3Col,
            CresExtra1Col = summary.CresExtra1Col,
            TableLinkageText = summary.TableLinkageText,
            RuleModeText = summary.RuleModeText,
            ProcedureSteps = summary.ProcedureSteps.ToList(),
            ClientId = summary.ClientId,
            SavedRunId = summary.SavedRunId,
            ControlSummaries = summary.ControlSummaries.Select(item => new Rule12ControlSummaryItemViewModel
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

        private static Rule12ValidationRowRecord CloneReviewRow(Rule12ValidationRowRecord row) => new()
        {
            ValidationNumber = row.ValidationNumber,
            ControlType = row.ControlType,
            ControlLabel = row.ControlLabel,
            ValidationResult = row.ValidationResult,
            ValidationExplanation = row.ValidationExplanation,
            DisplayValues = new Dictionary<string, string?>(row.DisplayValues, StringComparer.OrdinalIgnoreCase)
        };

        private static Rule12ValidationSummary CreateBrowserPreview(Rule12ValidationSummary summary)
        {
            var previewRows = summary.ReviewRows.OrderBy(row => row.ValidationNumber).Take(BrowserPreviewRowLimit).ToList();
            var clone = CloneSummary(summary);
            clone.DisplayedCount = previewRows.Count;
            clone.IsPreviewOnly = summary.TotalValidated > previewRows.Count;
            clone.PreviewLimit = summary.TotalValidated > previewRows.Count ? previewRows.Count : 0;
            clone.ReviewRows = previewRows;
            return clone;
        }

        private static void ApplyBrowserPreview(Rule12ValidationSummary summary)
        {
            var preview = CreateBrowserPreview(summary);
            summary.DisplayedCount = preview.DisplayedCount;
            summary.IsPreviewOnly = preview.IsPreviewOnly;
            summary.PreviewLimit = preview.PreviewLimit;
            summary.ReviewRows = preview.ReviewRows;
        }

        private static Rule12ValidationRequest CloneValidationRequest(Rule12ValidationRequest request) => new()
        {
            ClientId = request.ClientId,
            RunId = request.RunId,
            CregTable = request.CregTable,
            QualTable = request.QualTable,
            CresTable = request.CresTable,
            CregStudentCol = request.CregStudentCol,
            CregQualCol = request.CregQualCol,
            CregCourseCol = request.CregCourseCol,
            QualJoinCol = request.QualJoinCol,
            QualDescCol = request.QualDescCol,
            CresCourseCol = request.CresCourseCol,
            CresStatusCol = request.CresStatusCol,
            CresStatusFilter = request.CresStatusFilter,
            CregExtra1Col = request.CregExtra1Col,
            CregExtra2Col = request.CregExtra2Col,
            CregFilterCol = request.CregFilterCol,
            CregFilterValues = request.CregFilterValues,
            CregExtra3Col = request.CregExtra3Col,
            CresExtra1Col = request.CresExtra1Col
        };

        private static bool RequestsMatchForPendingSave(Rule12ValidationRequest current, Rule12ValidationRequest pending) =>
            current.ClientId == pending.ClientId &&
            string.Equals(current.CregTable?.Trim(), pending.CregTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.QualTable?.Trim(), pending.QualTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CresTable?.Trim(), pending.CresTable?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.CresStatusFilter?.Trim(), pending.CresStatusFilter?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static List<string> BuildProcedureSteps(string cregTable, string qualTable, string cresTable, string cresStatusCol, string cresStatusFilter) => new()
        {
            $"From {cregTable}, select all students who have an active registration in {cresTable} (where {cresStatusCol} = '{cresStatusFilter}').",
            $"Join {cregTable} to {cresTable} on the course code column to identify active students.",
            $"For each active student, retrieve the qualification code from {cregTable}.",
            $"Test whether the qualification code exists in {qualTable}.",
            $"Mark rows PASS when the qualification exists in {qualTable} and FAIL when it does not.",
            "Return the full active student population with qualification validation results."
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

        private static void ValidateRequest(string cregTable, string qualTable, string cresTable)
        {
            if (string.IsNullOrWhiteSpace(cregTable))
                throw new InvalidOperationException("CREG table is required.");
            if (string.IsNullOrWhiteSpace(qualTable))
                throw new InvalidOperationException("Qualification (QUAL) table is required.");
            if (string.IsNullOrWhiteSpace(cresTable))
                throw new InvalidOperationException("CRES table is required.");
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

        private static void EnrichRule12DisplayValues(Rule12ValidationRowRecord row)
        {
            var values = row.DisplayValues;
            var validationResult = ReadValue(values, "Validation_Result");
            var isPass = string.Equals(validationResult, "PASS", StringComparison.OrdinalIgnoreCase);
            var studentNum = FormatRule12ColumnValue(ReadValue(values, "Student_Number"));
            var qualCode = FormatRule12ColumnValue(ReadValue(values, "Qualification_Code"));
            var qualMatched = FormatRule12ColumnValue(ReadValue(values, "QUAL_Qualification_Code"));
            var qualName = FormatRule12ColumnValue(ReadValue(values, "QUAL_Qualification_Name"));
            var courseAppr = FormatRule12ColumnValue(ReadValue(values, "Course_Approval_Status"));

            const string criteriaText = "Active student: Course_Approval_Status = 'A' | Qualification_Code = QUAL.Qualification_Code";
            var validationExplanation = isPass
                ? $"Active student '{studentNum}' qualification '{qualCode}' exists in QUAL as '{qualMatched}'."
                : $"Active student '{studentNum}' qualification '{qualCode}' does not exist in QUAL.";
            var qualCriteriaMessage = $"Active student (Course_Approval_Status='{courseAppr}'): qualification code '{qualCode}'.";
            var credLinkMessage = isPass
                ? $"Matched QUAL.Qualification_Code = '{qualMatched}' ({qualName})."
                : "No matching QUAL qualification code was found.";
            var registrationMessage = isPass ? "Qualification exists in QUAL." : "Qualification not found in QUAL.";
            var finalResultMessage = isPass
                ? "Passed: active student qualification exists in QUAL."
                : "Failed: active student qualification does not exist in QUAL.";

            values["FINAL_RULE_TEXT"] = criteriaText;
            values["Validation_Explanation"] = validationExplanation;
            values["CRSE_CRITERIA_MESSAGE"] = qualCriteriaMessage;
            values["CRSE_SELECTION_MESSAGE"] = credLinkMessage;
            values["CREG_LINK_MESSAGE"] = registrationMessage;
            values["FINAL_RESULT_MESSAGE"] = finalResultMessage;
            row.ValidationExplanation = validationExplanation;
        }

        private static string FormatRule12ColumnValue(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "[blank]" : value.Trim();

        private static int GetInt(System.Data.Common.DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

        private static string ReadValue(IReadOnlyDictionary<string, string?> values, string key) =>
            values.TryGetValue(key, out var value) ? value ?? "" : "";

        private static string EscapeSqlString(string? value) => (value ?? "").Replace("'", "''");

        private static Rule12ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                if (string.IsNullOrWhiteSpace(decoded)) return null;
                return JsonConvert.DeserializeObject<Rule12ValidationSummary>(decoded);
            }
            catch { return null; }
        }
    }
}
