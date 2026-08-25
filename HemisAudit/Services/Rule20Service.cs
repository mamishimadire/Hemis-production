using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 20: Foundation Validation — validates against the engagement's own uploaded Supabase
    // data instead of a live SQL Server connection. Ported from the Rule18/19 pattern. 100%
    // population testing: STUD LEFT JOIN QUAL/CRED(bridge)/CRSE, filtered to foundation students
    // (STUD.FoundationFlag = 'Y') whose bridged course is a foundation course
    // (CRSE.FoundationFlag = 'Y'). Every returned row is PASS by construction — the WHERE clause
    // already enforces both flags, matching the original design exactly (no FAIL rows exist for
    // this rule; ExceptionRate is always 0).
    //
    // The original SQL-Server implementation also had a per-part ("A"/"B"/"C") notebook loading
    // path (LoadNotebookRowsByPartAsync/LoadNotebookPartRowsAsync/BuildNotebookPartQuery) that had
    // no live call sites — only the single "ALL" scope path (BuildNotebookFullQuery/
    // BuildNotebookPreviewAnalysisQuery, both built on BuildUnionQuery) was ever reachable. That
    // dead per-part subsystem is dropped rather than ported, matching the same dead-code finding
    // already made for Rule13's "Notebook A/B/C" subsystem.
    public class Rule20Service : IRule20Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private const string ScopeCode = "ALL";
        private const string ScopeTitle = "All Students";
        private const string ScopeDescription = "";
        private static readonly string[] DefaultPgTypes = ["07", "27", "28", "49", "72", "73", "08", "30", "50", "74", "75"];

        // The BRIDGE (CRED/CREG) join can be CREG-scale on a real institution (450k+ rows), and
        // every matching registration row is enumerated (not deduped — that's the intended "100%
        // population" semantic here). Materializing an unbounded row count into C# once already
        // caused an OutOfMemoryException on a different rule with the same shape (Rule 18) — this
        // cap applies to every "full" load (Run, workspace reload, export) from the start so that
        // mistake isn't repeated. Totals/pass/fail counts are always exact regardless of the cap.
        private const int MaxSafeReviewRows = 5000;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule20Service(
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

        public async Task<Rule20TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule20TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule20TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "dbo_stud", "STUD", "stud"], ["stud"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "dbo_qual", "QUAL", "qual"], ["qual"]),
                    AutoCregTable = FindFirst(tables, ["dbo_CRED", "dbo_cred", "CRED", "cred", "dbo_CREG", "dbo_creg", "CREG", "creg"], ["cred", "creg"]),
                    AutoCrseTable = FindFirst(tables, ["dbo_CRSE", "dbo_crse", "CRSE", "crse"], ["crse"])
                };
            }
            catch (Exception ex)
            {
                return new Rule20TableDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule20ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                return new Rule20ColumnDiscoveryResult { Success = true, Columns = columns };
            }
            catch (Exception ex)
            {
                return new Rule20ColumnDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule20VerifyResult> VerifyTablesAsync(Rule20VerifyRequest request)
        {
            try
            {
                ValidateRequest(request.StudTable, request.QualTable, request.CregTable, request.CrseTable);

                var summary = await AnalyseAsync(ToValidationRequest(request), includeAllReviewRows: false);
                return new Rule20VerifyResult
                {
                    Success = true,
                    FoundationStudentCount = summary.FoundationStudentCount,
                    ValidatedRowCount = summary.TotalValidated
                };
            }
            catch (Exception ex)
            {
                return new Rule20VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule20ValidationSummary> RunValidationAsync(Rule20ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.QualTable, request.CregTable, request.CrseTable);

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
                return new Rule20ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule20ValidationSummary> GetExportSummaryAsync(Rule20ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.QualTable, request.CregTable, request.CrseTable);
            return await AnalyseAsync(request, includeAllReviewRows: true);
        }

        // Cheap population size check - runs the same server-side prep SQL as a full export but
        // stops at a COUNT(*), no result rows loaded. Mirrors Rule12Service.GetPopulationCountAsync.
        public async Task<int> GetPopulationCountAsync(Rule20ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.QualTable, request.CregTable, request.CrseTable);
            var m = NormalizeColumnMapping(request.ColumnMapping);
            var pgTypes = ParsePgTypes(request.PgTypesText);

            await EnsureRule20IndexesAsync(request.ClientId, request.StudTable, request.QualTable, request.CregTable, request.CrseTable, m);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule20PrepSql(schema, request.StudTable, request.QualTable, request.CregTable, request.CrseTable, m, pgTypes);
                await prepCommand.ExecuteNonQueryAsync();
            }

            return await CountAsync(connection, "SELECT COUNT(*) FROM rule20_population;");
        }

        // Bypasses AnalyseAsync/LoadPopulationRowsAsync entirely - those buffer every row as a
        // full Dictionary<string,string?> before anything can be written out, capped at
        // MaxSafeReviewRows for a full export regardless of the real population size. Reads and
        // writes one row at a time, using the query's own column names directly. Mirrors
        // Rule12Service.StreamCsvExportAsync.
        public async Task StreamCsvExportAsync(Rule20ValidationRequest request, Stream outputStream)
        {
            ValidateRequest(request.StudTable, request.QualTable, request.CregTable, request.CrseTable);
            var m = NormalizeColumnMapping(request.ColumnMapping);
            var pgTypes = ParsePgTypes(request.PgTypesText);

            await EnsureRule20IndexesAsync(request.ClientId, request.StudTable, request.QualTable, request.CregTable, request.CrseTable, m);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule20PrepSql(schema, request.StudTable, request.QualTable, request.CregTable, request.CrseTable, m, pgTypes);
                await prepCommand.ExecuteNonQueryAsync();
            }

            await using var writer = new StreamWriter(outputStream, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = false };

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM rule20_population ORDER BY sample_number;";
            await using var reader = await command.ExecuteReaderAsync();

            var headerParts = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i)).ToList();
            await writer.WriteLineAsync(string.Join(",", headerParts.Select(StreamCsvEscape)));

            var rowValues = new List<string>(reader.FieldCount);
            while (await reader.ReadAsync())
            {
                rowValues.Clear();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var val = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                    rowValues.Add(StreamCsvEscape(val));
                }
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

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule20WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 20);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule20WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable,
                QualTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_QUAL" : row.DeceasedTable,
                CregTable = string.IsNullOrWhiteSpace(row.StudColumn) ? "dbo_CRED" : row.StudColumn,
                CrseTable = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "dbo_CRSE" : row.DeceasedColumn,
                PgTypesText = string.IsNullOrWhiteSpace(deserializedSummary?.PgTypesText)
                    ? string.Join(", ", DefaultPgTypes)
                    : deserializedSummary!.PgTypesText,
                GoverningPartCodes = [ScopeCode],
                ColumnMapping = deserializedSummary?.ColumnMapping ?? new Rule20ColumnMapping(),
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

        public async Task<Rule20RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 20);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            summary.ClientId = row.ClientId;
            if (summary.SavedRunId.GetValueOrDefault() <= 0)
                summary.SavedRunId = runId;

            summary = await ExpandSavedSummaryIfNeededAsync(summary, row.ClientId);

            if (includeFullResults)
            {
                summary.DisplayedCount = summary.ReviewRows.Count;
                summary.IsPreviewOnly = summary.TotalValidated > summary.ReviewRows.Count;
                summary.PreviewLimit = summary.IsPreviewOnly ? summary.ReviewRows.Count : 0;
            }
            else
            {
                ApplyBrowserPreview(summary);
            }

            var review = new Rule20RunReviewViewModel
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

        public async Task<Rule20WorkspaceSaveResult> SaveWorkspaceAsync(Rule20ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0)
                    return new Rule20WorkspaceSaveResult { Success = false, Error = "Run the validation first so the workspace can be saved." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule20WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.QualTable,
                    StudColumn = request.CregTable,
                    DeceasedColumn = request.CrseTable
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule20WorkspaceSaveResult
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
                return new Rule20WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule20WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule20WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule20WorkspaceSaveResult
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
                return new Rule20WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 20 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 20 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 20 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule20ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.QualTable, request.CregTable, request.CrseTable);

            var m = NormalizeColumnMapping(request.ColumnMapping);
            var pgTypes = ParsePgTypes(request.PgTypesText);

            var sql = $@"-- HEMIS RULE 20: FOUNDATION VALIDATION (100% POPULATION)
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Population : {request.StudTable}.{m.StudFoundationFlag} = '{m.StudFoundationValue}'
-- Validation : {request.CrseTable}.{m.CrseFoundationFlag} = '{m.CrseFoundationValue}'
-- Joins      : {request.StudTable}.{m.StudQualCode} = {request.CregTable}.{m.CregQualCode}
--              {request.CregTable}.{m.CregCourseCode} = {request.CrseTable}.{m.CrseCourseCode}
--              {request.StudTable}.{m.StudQualCode} = {request.QualTable}.{m.QualQualCode}

{BuildRule20PrepSql("{schema}", request.StudTable, request.QualTable, request.CregTable, request.CrseTable, m, pgTypes)}

-- Full result set
SELECT * FROM rule20_population ORDER BY sample_number;

-- Summary
SELECT COUNT(*) AS total_validated FROM rule20_population;";

            return sql.Trim();
        }

        private async Task<Rule20ValidationSummary> AnalyseAsync(Rule20ValidationRequest request, bool includeAllReviewRows)
        {
            var m = NormalizeColumnMapping(request.ColumnMapping);
            var pgTypes = ParsePgTypes(request.PgTypesText);

            await EnsureColumnsExistAsync(request.ClientId, request.StudTable, request.QualTable, request.CregTable, request.CrseTable, m);
            await EnsureRule20IndexesAsync(request.ClientId, request.StudTable, request.QualTable, request.CregTable, request.CrseTable, m);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var foundationStudentCount = await CountAsync(connection,
                $@"SELECT COUNT(*) FROM ""{schema}"".""{request.StudTable}"" S WHERE UPPER(TRIM(CAST(S.""{m.StudFoundationFlag}"" AS text))) = '{EscapeSqlString(m.StudFoundationValue.ToUpperInvariant())}';");

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule20PrepSql(schema, request.StudTable, request.QualTable, request.CregTable, request.CrseTable, m, pgTypes);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var reviewRowCap = includeAllReviewRows ? MaxSafeReviewRows : BrowserPreviewRowLimit;
            var reviewRows = await LoadPopulationRowsAsync(connection, reviewRowCap);
            reviewRows = NormalizeReviewRows(reviewRows);

            var totalValidated = await CountAsync(connection, "SELECT COUNT(*) FROM rule20_population;");
            var isPreviewOnly = totalValidated > reviewRows.Count;

            var partSummary = new Rule20PartSummaryItemViewModel
            {
                PartCode = ScopeCode,
                PartTitle = ScopeTitle,
                PartDescription = ScopeDescription,
                TotalCount = totalValidated,
                PassCount = totalValidated,
                FailCount = 0,
                Status = "PASS"
            };

            return new Rule20ValidationSummary
            {
                Success = true,
                FoundationStudentCount = foundationStudentCount,
                TotalValidated = totalValidated,
                DisplayedCount = reviewRows.Count,
                IsPreviewOnly = isPreviewOnly,
                PreviewLimit = isPreviewOnly ? reviewRowCap : 0,
                PassCount = totalValidated,
                FailCount = 0,
                ExceptionRate = 0m,
                Status = "PASS",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable = request.StudTable,
                QualTable = request.QualTable,
                CregTable = request.CregTable,
                CrseTable = request.CrseTable,
                ColumnMapping = m,
                PgTypesText = string.Join(", ", pgTypes),
                PgTypes = pgTypes,
                GoverningPartCodes = [ScopeCode],
                GoverningPartCodesText = ScopeTitle,
                OverallStatusRuleText = "Overall PASS requires the filtered STUD -> CRED -> CRSE population to PASS.",
                TableLinkageText = BuildTableLinkageText(request.StudTable, request.QualTable, request.CregTable, request.CrseTable),
                ProcedureSteps = BuildProcedureSteps(request.StudTable, request.QualTable, request.CregTable, request.CrseTable, m),
                ClientId = request.ClientId,
                PartSummaries = [partSummary],
                ReviewRows = reviewRows,
                Warning = !isPreviewOnly
                    ? "Rule 20 completed with the full notebook-equivalent result set."
                    : includeAllReviewRows
                        ? $"Counts reflect the full notebook-equivalent result set. The saved result rows are capped at {MaxSafeReviewRows:N0} to keep the app stable on very large populations; totals are still exact."
                        : "Counts reflect the full notebook-equivalent result set. Browser review rows are limited for performance."
            };
        }

        private async Task<int> SaveValidationRunAsync(Rule20ValidationRequest request, Rule20ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 20);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 20,
                RuleName = "Foundation Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.QualTable,
                StudColumn = request.CregTable,
                DeceasedColumn = request.CrseTable,
                ExceptionsJSON = ValidationPayloadCodec.Encode("[]"),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            summary.SavedRunId = runId;
            return runId;
        }

        private async Task<Rule20ValidationSummary> ExpandSavedSummaryIfNeededAsync(Rule20ValidationSummary summary, int clientId)
        {
            // See MaxSafeReviewRows: a saved run is "as complete as it will ever get" once
            // ReviewRows already holds min(TotalValidated, MaxSafeReviewRows) rows — re-running
            // AnalyseAsync would just recompute the identical capped result. Without this check,
            // any run whose true population exceeds the cap would silently re-run the full
            // analysis on every single workspace load.
            var completenessTarget = Math.Min(summary.TotalValidated, MaxSafeReviewRows);
            var looksLikeStoredPreviewSample =
                summary.ReviewRows.Count > 0 &&
                summary.ReviewRows.Count <= BrowserPreviewRowLimit &&
                summary.TotalValidated > BrowserPreviewRowLimit;

            if (summary.ReviewRows.Count >= completenessTarget && !looksLikeStoredPreviewSample)
                return summary;

            if (string.IsNullOrWhiteSpace(summary.StudTable) || string.IsNullOrWhiteSpace(summary.QualTable) ||
                string.IsNullOrWhiteSpace(summary.CregTable) || string.IsNullOrWhiteSpace(summary.CrseTable))
            {
                return summary;
            }

            try
            {
                var expanded = await AnalyseAsync(new Rule20ValidationRequest
                {
                    ClientId = clientId,
                    RunId = summary.SavedRunId,
                    StudTable = summary.StudTable,
                    QualTable = summary.QualTable,
                    CregTable = summary.CregTable,
                    CrseTable = summary.CrseTable,
                    PgTypesText = summary.PgTypesText,
                    GoverningPartCodes = [ScopeCode],
                    ColumnMapping = summary.ColumnMapping ?? new Rule20ColumnMapping()
                }, includeAllReviewRows: true);

                expanded.Timestamp = string.IsNullOrWhiteSpace(summary.Timestamp) ? expanded.Timestamp : summary.Timestamp;
                expanded.ClientId = summary.ClientId;
                expanded.SavedRunId = summary.SavedRunId;
                expanded.Warning = string.IsNullOrWhiteSpace(summary.Warning)
                    ? "Saved Rule 20 results were expanded from the stored sample to the full result set."
                    : $"{summary.Warning} Full saved results were reloaded from the saved Rule 20 configuration.";

                return expanded;
            }
            catch
            {
                return summary;
            }
        }

        private static Rule20ValidationRequest ToValidationRequest(Rule20VerifyRequest request) =>
            new()
            {
                ClientId = request.ClientId,
                StudTable = request.StudTable,
                QualTable = request.QualTable,
                CregTable = request.CregTable,
                CrseTable = request.CrseTable,
                PgTypesText = request.PgTypesText,
                GoverningPartCodes = [ScopeCode],
                ColumnMapping = request.ColumnMapping ?? new Rule20ColumnMapping()
            };

        // ── Column configuration ────────────────────────────────────────────────────────

        private static Rule20ColumnMapping NormalizeColumnMapping(Rule20ColumnMapping? m)
        {
            m ??= new Rule20ColumnMapping();
            return new Rule20ColumnMapping
            {
                StudStudentNo = ColOrDefault(m.StudStudentNo, "_007"),
                StudColumn008 = ColOrDefault(m.StudColumn008, "_008"),
                StudColumn066 = ColOrDefault(m.StudColumn066, "_066"),
                StudColumn067 = ColOrDefault(m.StudColumn067, "_067"),
                StudColumn068 = ColOrDefault(m.StudColumn068, "_068"),
                StudColumn012 = ColOrDefault(m.StudColumn012, "_012"),
                StudColumn013 = ColOrDefault(m.StudColumn013, "_013"),
                StudColumn014 = ColOrDefault(m.StudColumn014, "_014"),
                StudColumn015 = ColOrDefault(m.StudColumn015, "_015"),
                StudColumn010 = ColOrDefault(m.StudColumn010, "_010"),
                StudColumn026 = ColOrDefault(m.StudColumn026, "_026"),
                StudColumn025 = ColOrDefault(m.StudColumn025, "_025"),
                StudQualCode = ColOrDefault(m.StudQualCode, "_001"),
                StudName = ColOrDefault(m.StudName, "_019"),
                StudIdNo = ColOrDefault(m.StudIdNo, "_024"),
                StudFoundationFlag = ColOrDefault(m.StudFoundationFlag, "_106"),
                StudFoundationValue = ColOrDefault(m.StudFoundationValue, "Y"),
                QualQualCode = ColOrDefault(m.QualQualCode, "_001"),
                QualDescription = ColOrDefault(m.QualDescription, "_003"),
                QualType = ColOrDefault(m.QualType, "_005"),
                CregQualCode = ColOrDefault(m.CregQualCode, "_001"),
                CregCourseCode = ColOrDefault(m.CregCourseCode, "_030"),
                CrseCourseCode = ColOrDefault(m.CrseCourseCode, "_030"),
                CrseCourseName = ColOrDefault(m.CrseCourseName, "_058"),
                CrseFoundationFlag = ColOrDefault(m.CrseFoundationFlag, "_091"),
                CrseFoundationValue = ColOrDefault(m.CrseFoundationValue, "Y")
            };
        }

        private static string ColOrDefault(string? val, string def) => string.IsNullOrWhiteSpace(val) ? def : val.Trim();

        private async Task EnsureColumnsExistAsync(int clientId, string studTable, string qualTable, string cregTable, string crseTable, Rule20ColumnMapping m)
        {
            var studColumns = await _datasets.GetValidatedColumnsAsync(clientId, studTable);
            var qualColumns = await _datasets.GetValidatedColumnsAsync(clientId, qualTable);
            var cregColumns = await _datasets.GetValidatedColumnsAsync(clientId, cregTable);
            var crseColumns = await _datasets.GetValidatedColumnsAsync(clientId, crseTable);

            EnsureRequiredColumns(studTable, studColumns, [m.StudStudentNo, m.StudColumn008, m.StudColumn066, m.StudColumn067, m.StudColumn068, m.StudColumn012, m.StudColumn013, m.StudColumn014, m.StudColumn015, m.StudColumn010, m.StudColumn026, m.StudColumn025, m.StudQualCode, m.StudName, m.StudIdNo, m.StudFoundationFlag]);
            EnsureRequiredColumns(qualTable, qualColumns, [m.QualQualCode, m.QualDescription, m.QualType]);
            EnsureRequiredColumns(cregTable, cregColumns, [m.CregQualCode, m.CregCourseCode]);
            EnsureRequiredColumns(crseTable, crseColumns, [m.CrseCourseCode, m.CrseCourseName, m.CrseFoundationFlag]);
        }

        private static void EnsureRequiredColumns(string tableName, IReadOnlyCollection<string> availableColumns, IEnumerable<string> requiredColumns)
        {
            var missing = requiredColumns.Where(required => !availableColumns.Contains(required, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"Table {tableName} is missing required column(s): {string.Join(", ", missing)}.");
        }

        // Uploaded engagement tables have no indexes beyond their primary key. The BRIDGE
        // (CRED/CREG) join can be CREG-scale on a real institution — building the expression
        // indexes once, up front, keeps the join fast on every run after the first.
        private async Task EnsureRule20IndexesAsync(int clientId, string studTable, string qualTable, string cregTable, string crseTable, Rule20ColumnMapping m)
        {
            await _datasets.EnsureJoinIndexAsync(clientId, studTable, m.StudQualCode);
            await _datasets.EnsureJoinIndexAsync(clientId, qualTable, m.QualQualCode);
            await _datasets.EnsureJoinIndexAsync(clientId, cregTable, m.CregQualCode);
            await _datasets.EnsureJoinIndexAsync(clientId, cregTable, m.CregCourseCode);
            await _datasets.EnsureJoinIndexAsync(clientId, crseTable, m.CrseCourseCode);
        }

        // ── SQL builders (Postgres) ─────────────────────────────────────────────────────

        private static string BuildRule20PrepSql(string schema, string studTable, string qualTable, string cregTable, string crseTable, Rule20ColumnMapping m, List<string> pgTypes)
        {
            var studFoundationVal = EscapeSqlString(m.StudFoundationValue.ToUpperInvariant());
            var crseFoundationVal = EscapeSqlString(m.CrseFoundationValue.ToUpperInvariant());
            var pgTypeList = pgTypes.Select(t => $"'{EscapeSqlString(t.ToUpperInvariant())}'").ToList();
            var pgTypePredicate = pgTypeList.Count > 0
                ? $@"UPPER(TRIM(CAST(Q.""{m.QualType}"" AS text))) IN ({string.Join(", ", pgTypeList)})"
                : "1=0";

            return $@"
DROP TABLE IF EXISTS rule20_population;

CREATE TEMP TABLE rule20_population AS
SELECT
    ROW_NUMBER() OVER (ORDER BY CAST(S.""{m.StudStudentNo}"" AS text)) AS sample_number,
    CAST(S.""{m.StudStudentNo}"" AS text) AS ""StudentNumber007"",
    CAST(S.""{m.StudColumn008}"" AS text) AS ""StudentColumn008"",
    CAST(S.""{m.StudColumn066}"" AS text) AS ""StudentColumn066"",
    CAST(S.""{m.StudColumn067}"" AS text) AS ""StudentColumn067"",
    CAST(S.""{m.StudColumn068}"" AS text) AS ""StudentColumn068"",
    CAST(S.""{m.StudColumn012}"" AS text) AS ""StudentColumn012"",
    CAST(S.""{m.StudColumn013}"" AS text) AS ""StudentColumn013"",
    CAST(S.""{m.StudColumn014}"" AS text) AS ""StudentColumn014"",
    CAST(S.""{m.StudColumn015}"" AS text) AS ""StudentColumn015"",
    CAST(S.""{m.StudColumn010}"" AS text) AS ""StudentColumn010"",
    CAST(S.""{m.StudColumn026}"" AS text) AS ""StudentColumn026"",
    CAST(S.""{m.StudColumn025}"" AS text) AS ""StudentColumn025"",
    CAST(S.""{m.StudQualCode}"" AS text) AS ""QualificationCode001"",
    CAST(S.""{m.StudName}"" AS text) AS ""Name019"",
    CAST(S.""{m.StudIdNo}"" AS text) AS ""IdNumber024"",
    CAST(S.""{m.StudFoundationFlag}"" AS text) AS ""FoundationFlag106"",
    CAST(Q.""{m.QualDescription}"" AS text) AS ""QualificationDescription003"",
    CAST(Q.""{m.QualType}"" AS text) AS ""QualificationType005"",
    CAST(BRIDGE.""{m.CregQualCode}"" AS text) AS ""BridgeQualificationCode001"",
    CAST(BRIDGE.""{m.CregCourseCode}"" AS text) AS ""CourseCode030"",
    CAST(CRSE.""{m.CrseCourseName}"" AS text) AS ""CourseName058"",
    CAST(CRSE.""{m.CrseCourseCode}"" AS text) AS ""CrseCourseCode030"",
    CAST(CRSE.""{m.CrseFoundationFlag}"" AS text) AS ""FoundationCourse091"",
    CASE WHEN {pgTypePredicate} THEN 'Postgraduate' ELSE 'Undergraduate' END AS ""StudentType"",
    'VALID' AS ""NotebookStatus"",
    'PASS' AS ""ValidationResult""
FROM ""{schema}"".""{studTable}"" S
LEFT JOIN ""{schema}"".""{qualTable}"" Q ON UPPER(TRIM(CAST(S.""{m.StudQualCode}"" AS text))) = UPPER(TRIM(CAST(Q.""{m.QualQualCode}"" AS text)))
LEFT JOIN ""{schema}"".""{cregTable}"" BRIDGE ON UPPER(TRIM(CAST(S.""{m.StudQualCode}"" AS text))) = UPPER(TRIM(CAST(BRIDGE.""{m.CregQualCode}"" AS text)))
LEFT JOIN ""{schema}"".""{crseTable}"" CRSE ON UPPER(TRIM(CAST(BRIDGE.""{m.CregCourseCode}"" AS text))) = UPPER(TRIM(CAST(CRSE.""{m.CrseCourseCode}"" AS text)))
WHERE UPPER(TRIM(CAST(S.""{m.StudFoundationFlag}"" AS text))) = '{studFoundationVal}'
  AND UPPER(TRIM(CAST(CRSE.""{m.CrseFoundationFlag}"" AS text))) = '{crseFoundationVal}';

ANALYZE rule20_population;";
        }

        private async Task<List<Rule20ReviewRowViewModel>> LoadPopulationRowsAsync(NpgsqlConnection connection, int maxRows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $@"SELECT * FROM rule20_population ORDER BY sample_number LIMIT {maxRows};";

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule20ReviewRowViewModel>();
            while (await reader.ReadAsync())
                rows.Add(MapRule20ReviewRow(reader));

            return rows;
        }

        private static string ReadString(System.Data.Common.DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
        }

        private static Rule20ReviewRowViewModel MapRule20ReviewRow(System.Data.Common.DbDataReader reader)
        {
            var row = new Rule20ReviewRowViewModel
            {
                PartCode = ScopeCode,
                PartTitle = ScopeTitle,
                PartDescription = ScopeDescription,
                StudentNumber007 = ReadString(reader, "StudentNumber007"),
                StudentColumn008 = ReadString(reader, "StudentColumn008"),
                StudentColumn066 = ReadString(reader, "StudentColumn066"),
                StudentColumn067 = ReadString(reader, "StudentColumn067"),
                StudentColumn068 = ReadString(reader, "StudentColumn068"),
                StudentColumn012 = ReadString(reader, "StudentColumn012"),
                StudentColumn013 = ReadString(reader, "StudentColumn013"),
                StudentColumn014 = ReadString(reader, "StudentColumn014"),
                StudentColumn015 = ReadString(reader, "StudentColumn015"),
                StudentColumn010 = ReadString(reader, "StudentColumn010"),
                StudentColumn026 = ReadString(reader, "StudentColumn026"),
                StudentColumn025 = ReadString(reader, "StudentColumn025"),
                QualificationCode001 = ReadString(reader, "QualificationCode001"),
                Name019 = ReadString(reader, "Name019"),
                IdNumber024 = ReadString(reader, "IdNumber024"),
                FoundationFlag106 = ReadString(reader, "FoundationFlag106"),
                QualificationDescription003 = ReadString(reader, "QualificationDescription003"),
                QualificationType005 = ReadString(reader, "QualificationType005"),
                BridgeQualificationCode001 = ReadString(reader, "BridgeQualificationCode001"),
                CourseCode030 = ReadString(reader, "CourseCode030"),
                CourseName058 = ReadString(reader, "CourseName058"),
                CrseCourseCode030 = ReadString(reader, "CrseCourseCode030"),
                FoundationCourse091 = ReadString(reader, "FoundationCourse091"),
                StudentType = ReadString(reader, "StudentType"),
                NotebookStatus = ReadString(reader, "NotebookStatus"),
                ValidationResult = ReadString(reader, "ValidationResult")
            };

            row.ValidationExplanation = BuildValidationExplanation(row.FoundationFlag106, row.CourseCode030, row.FoundationCourse091);
            return row;
        }

        private static string BuildValidationExplanation(string foundationFlag106, string courseCode030, string foundationCourse091)
        {
            var failedChecks = new List<string>();
            foundationFlag106 = NormalizeRowText(foundationFlag106);
            courseCode030 = NormalizeRowText(courseCode030);
            foundationCourse091 = NormalizeRowText(foundationCourse091);

            if (!string.Equals(foundationFlag106, "Y", StringComparison.OrdinalIgnoreCase))
                failedChecks.Add("STUD foundation flag is not 'Y'");
            if (string.IsNullOrWhiteSpace(courseCode030))
                failedChecks.Add("no CRED bridge course code matched STUD qualification code");
            else if (string.IsNullOrWhiteSpace(foundationCourse091))
                failedChecks.Add($"no CRSE row matched bridge course code '{courseCode030}'");
            if (!string.Equals(foundationCourse091, "Y", StringComparison.OrdinalIgnoreCase))
                failedChecks.Add("CRSE foundation flag is not 'Y'");

            return failedChecks.Count == 0
                ? "PASS: the student is in the Rule 20 filtered population through the STUD -> CRED -> CRSE linkage."
                : $"FAIL: {string.Join("; ", failedChecks)}.";
        }

        private static List<string> BuildProcedureSteps(string studTable, string qualTable, string bridgeTable, string crseTable, Rule20ColumnMapping? m = null) =>
            new()
            {
                $"Step 1: start from {studTable} and keep only foundation students where {studTable}.{ColOrDefault(m?.StudFoundationFlag, "_106")} = '{ColOrDefault(m?.StudFoundationValue, "Y")}'.",
                $"Step 2: match the filtered {studTable} rows to {bridgeTable} (CRED) on {ColOrDefault(m?.StudQualCode, "_001")}/{ColOrDefault(m?.CregQualCode, "_001")} and carry {bridgeTable}.{ColOrDefault(m?.CregCourseCode, "_030")} forward.",
                $"Step 3: join {bridgeTable}.{ColOrDefault(m?.CregCourseCode, "_030")} to {crseTable}.{ColOrDefault(m?.CrseCourseCode, "_030")} and keep only rows where {crseTable}.{ColOrDefault(m?.CrseFoundationFlag, "_091")} = '{ColOrDefault(m?.CrseFoundationValue, "Y")}'.",
                $"Step 4: match {studTable} to {qualTable} on {ColOrDefault(m?.StudQualCode, "_001")}/{ColOrDefault(m?.QualQualCode, "_001")} only to enrich the rows for qualification description.",
                "Step 5: treat rows that remain in the filtered STUD -> CRED -> CRSE linkage as PASS."
            };

        private static string BuildTableLinkageText(string studTable, string qualTable, string bridgeTable, string crseTable) =>
            $"{studTable}._001 -> {bridgeTable} (CRED)._001 -> {crseTable}._030";

        private static List<string> ParsePgTypes(string? pgTypesText)
        {
            var values = (pgTypesText ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return values.Count > 0 ? values : DefaultPgTypes.ToList();
        }

        private static Rule20ValidationSummary CreateBrowserPreview(Rule20ValidationSummary summary)
        {
            var previewRows = NormalizeReviewRows(summary.ReviewRows).Take(BrowserPreviewRowLimit).ToList();
            var isPreviewOnly = summary.TotalValidated > previewRows.Count;

            return new Rule20ValidationSummary
            {
                Success = summary.Success,
                FoundationStudentCount = summary.FoundationStudentCount,
                TotalValidated = summary.TotalValidated,
                DisplayedCount = previewRows.Count,
                IsPreviewOnly = isPreviewOnly,
                PreviewLimit = isPreviewOnly ? BrowserPreviewRowLimit : 0,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                Status = summary.Status,
                Timestamp = summary.Timestamp,
                StudTable = summary.StudTable,
                QualTable = summary.QualTable,
                CregTable = summary.CregTable,
                CrseTable = summary.CrseTable,
                PgTypesText = summary.PgTypesText,
                PgTypes = summary.PgTypes.ToList(),
                GoverningPartCodes = [ScopeCode],
                GoverningPartCodesText = summary.GoverningPartCodesText,
                OverallStatusRuleText = summary.OverallStatusRuleText,
                ClientId = summary.ClientId,
                SavedRunId = summary.SavedRunId,
                ColumnMapping = summary.ColumnMapping ?? new Rule20ColumnMapping(),
                TableLinkageText = summary.TableLinkageText,
                ProcedureSteps = summary.ProcedureSteps?.ToList() ?? new List<string>(),
                PartSummaries = (summary.PartSummaries ?? new List<Rule20PartSummaryItemViewModel>())
                    .Select(item => new Rule20PartSummaryItemViewModel
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
                Error = summary.Error
            };
        }

        private static void ApplyBrowserPreview(Rule20ValidationSummary summary)
        {
            var preview = CreateBrowserPreview(summary);
            summary.DisplayedCount = preview.DisplayedCount;
            summary.IsPreviewOnly = preview.IsPreviewOnly;
            summary.PreviewLimit = preview.PreviewLimit;
            summary.ReviewRows = preview.ReviewRows;
        }

        private static List<Rule20ReviewRowViewModel> NormalizeReviewRows(IEnumerable<Rule20ReviewRowViewModel>? rows)
        {
            var normalizedRows = (rows ?? Enumerable.Empty<Rule20ReviewRowViewModel>()).ToList();
            for (var i = 0; i < normalizedRows.Count; i++)
                normalizedRows[i].ValidationNumber = i + 1;
            return normalizedRows;
        }

        private static string NormalizeRowText(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

        private static Rule20ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                var summary = JsonConvert.DeserializeObject<Rule20ValidationSummary>(decoded, new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
                if (summary == null) return null;

                summary.ColumnMapping ??= new Rule20ColumnMapping();
                summary.GoverningPartCodes = [ScopeCode];
                summary.ReviewRows = NormalizeReviewRows(summary.ReviewRows);
                return summary;
            }
            catch
            {
                return null;
            }
        }

        private static string? FindFirst(IEnumerable<string> columns, IEnumerable<string> exactMatches, IEnumerable<string> partialMatches)
        {
            var list = columns.ToList();
            foreach (var exact in exactMatches)
            {
                var match = list.FirstOrDefault(c => c.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }
            foreach (var partial in partialMatches)
            {
                var match = list.FirstOrDefault(c => c.Contains(partial, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }
            return list.FirstOrDefault();
        }

        private static void ValidateRequest(string studTable, string qualTable, string cregTable, string crseTable)
        {
            if (string.IsNullOrWhiteSpace(studTable) || string.IsNullOrWhiteSpace(qualTable) ||
                string.IsNullOrWhiteSpace(cregTable) || string.IsNullOrWhiteSpace(crseTable))
            {
                throw new InvalidOperationException("All four Rule 20 tables are required.");
            }
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase);

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

        private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static string EscapeSqlString(string? value) => (value ?? "").Replace("'", "''");
    }
}
