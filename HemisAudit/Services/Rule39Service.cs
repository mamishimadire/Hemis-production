using System.Globalization;
using System.Security.Cryptography;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 39: First-Time Entering Students vs Non-Aligned Qualifications — validates against
    // the engagement's own uploaded Supabase data instead of a live SQL Server connection.
    // Rule 39 is functionally identical to Rule 21 (same STUD/QUAL/NAL cross-match, same
    // ViewModel shape) under a different rule number, so this port mirrors Rule21Service.cs
    // exactly rather than re-deriving the query. STUD is filtered on the first-time entering flag,
    // LEFT JOINed to QUAL (for the qualification name) and then to the Non-Aligned Qualifications
    // reference table (filtered by Category). Any student whose qualification code is found in the
    // NAL list is FLAGGED; everyone else is CLEAR. Unlike Rule18/19/20 this is not a
    // 100%-PASS-by-construction rule — FLAGGED is the genuine exception outcome — so results are
    // capped defensively: all FLAGGED rows are kept (up to a generous safety cap, since that is the
    // actionable exception list), while CLEAR rows are only ever stored as a representative sample
    // (matching what the field has always been named) to keep the saved JSON bounded regardless of
    // how large the first-time-entering population is.
    public class Rule39Service : IRule39Service
    {
        private const int BrowserPreviewPerResultLimit = 10;
        private const int MaxFlaggedRows = 5000;
        private const int ClearSampleSize = 500;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule39Service(
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

        public async Task<Rule39TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule39TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule39TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "dbo_stud", "STUD", "stud"], ["stud"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "dbo_qual", "QUAL", "qual"], ["qual"]),
                    AutoNalTable = FindFirst(tables,
                        ["Non_Aligned_Qualifications", "NonAligned_Qualifications", "NON_ALIGNED_QUALIFICATIONS"],
                        ["non_aligned", "nonaligned", "nal_qual", "nal"])
                };
            }
            catch (Exception ex)
            {
                return new Rule39TableDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule39ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                return new Rule39ColumnDiscoveryResult
                {
                    Success = true,
                    Columns = columns,
                    AutoQualRefColumn    = FindFirst(columns, ["_001"], ["qual_ref", "qualcode", "qual"]),
                    AutoFirstTimeColumn  = FindFirst(columns, ["_010"], ["firsttime", "first_time"]),
                    AutoStud007Column    = FindFirst(columns, ["_007"], ["_007"]),
                    AutoStud008Column    = FindFirst(columns, ["_008"], ["_008"]),
                    AutoStud012Column    = FindFirst(columns, ["_012"], ["_012"]),
                    AutoStud026Column    = FindFirst(columns, ["_026"], ["_026"]),
                    AutoQualCodeColumn   = FindFirst(columns, ["_001"], ["qual_code", "qualification_code"]),
                    AutoQualNameColumn   = FindFirst(columns, ["_003"], ["qual_name", "qualification_name"]),
                    AutoNalRefColumn     = FindFirst(columns, ["Qualification_reference_number"], ["qual_ref", "qualification_ref", "qualref"]),
                    AutoNalNameColumn    = FindFirst(columns, ["Existing_qualification_name"], ["exist_qual", "qual_name", "qualname"]),
                    AutoNalAlignedColumn = FindFirst(columns, ["Aligned_qualification_name"], ["aligned_qual", "aligned"]),
                    AutoNalCategoryColumn = FindFirst(columns, ["Category"], ["category", "cat"])
                };
            }
            catch (Exception ex)
            {
                return new Rule39ColumnDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule39DistinctValuesResult> GetDistinctValuesAsync(int clientId, string tableName, string columnName, string? preferredValue)
        {
            try
            {
                var values = (await _datasets.GetDistinctColumnValuesAsync(clientId, tableName, columnName, take: 100))
                    .Select(v => v.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList();

                var autoValue = !string.IsNullOrWhiteSpace(preferredValue) && values.Any(v => string.Equals(v, preferredValue, StringComparison.OrdinalIgnoreCase))
                    ? values.First(v => string.Equals(v, preferredValue, StringComparison.OrdinalIgnoreCase))
                    : values.FirstOrDefault();

                return new Rule39DistinctValuesResult { Success = true, Values = values, AutoValue = autoValue };
            }
            catch (Exception ex)
            {
                return new Rule39DistinctValuesResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule39VerifyResult> VerifyTablesAsync(Rule39VerifyRequest request)
        {
            try
            {
                var studFirstTimeColumn = Default(request.StudFirstTimeColumn, "_010");
                var studFirstTimeValue = Default(request.StudFirstTimeValue, "F");
                var nalCategoryValue = Default(request.NalCategoryValue, "C");

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var ftv = EscapeSqlString(studFirstTimeValue.ToUpperInvariant());
                var catv = EscapeSqlString(nalCategoryValue.ToUpperInvariant());

                var studTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";");
                var studFiltered = await CountAsync(connection,
                    $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\" WHERE UPPER(TRIM(CAST(\"{studFirstTimeColumn}\" AS text))) = '{ftv}';");
                var qualTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.QualTable}\";");
                var nalTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.NalTable}\";");
                var nalFiltered = await CountAsync(connection,
                    $"SELECT COUNT(*) FROM \"{schema}\".\"{request.NalTable}\" WHERE UPPER(TRIM(CAST(\"{request.NalCategoryColumn}\" AS text))) = '{catv}';");

                return new Rule39VerifyResult
                {
                    Success = true,
                    StudTotalCount = studTotal,
                    StudFilteredCount = studFiltered,
                    QualTotalCount = qualTotal,
                    NalTotalCount = nalTotal,
                    NalFilteredCount = nalFiltered
                };
            }
            catch (Exception ex)
            {
                return new Rule39VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule39ValidationSummary> RunValidationAsync(Rule39ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                var summary = await AnalyseAsync(request);
                if (summary.Success && request.ClientId > 0)
                {
                    try
                    {
                        summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName);
                    }
                    catch (Exception ex)
                    {
                        summary.Success = false;
                        summary.Error = $"Analysis completed, but the saved run could not be written to the system database: {ex.Message}";
                        return summary;
                    }
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule39ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule39WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 39);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (deserializedSummary != null) ApplyBrowserPreview(deserializedSummary);

            var workspace = new Rule39WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable,
                NalTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "Non_Aligned_Qualifications" : row.DeceasedTable,
                StudQualRefColumn = string.IsNullOrWhiteSpace(row.StudColumn) ? "_001" : row.StudColumn,
                StudFirstTimeColumn = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "_010" : row.DeceasedColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = deserializedSummary
            };

            if (deserializedSummary != null)
            {
                workspace.StudFirstTimeValue = deserializedSummary.StudFirstTimeValue;
                workspace.QualTable          = deserializedSummary.QualTable;
                workspace.Stud007Column      = deserializedSummary.Stud007Column;
                workspace.Stud008Column      = deserializedSummary.Stud008Column;
                workspace.Stud012Column      = deserializedSummary.Stud012Column;
                workspace.Stud026Column      = deserializedSummary.Stud026Column;
                workspace.QualCodeColumn     = deserializedSummary.QualCodeColumn;
                workspace.QualNameColumn     = deserializedSummary.QualNameColumn;
                workspace.NalRefColumn       = deserializedSummary.NalRefColumn;
                workspace.NalNameColumn      = deserializedSummary.NalNameColumn;
                workspace.NalAlignedColumn   = deserializedSummary.NalAlignedColumn;
                workspace.NalCategoryColumn  = deserializedSummary.NalCategoryColumn;
                workspace.NalCategoryValue   = deserializedSummary.NalCategoryValue;
                workspace.NalHeqsfRefColumn  = deserializedSummary.NalHeqsfRefColumn;
                workspace.NalSaqaIdColumn    = deserializedSummary.NalSaqaIdColumn;
                workspace.NalNqfColumn       = deserializedSummary.NalNqfColumn;
                workspace.NalCreditsColumn   = deserializedSummary.NalCreditsColumn;
                workspace.NalOutcomeColumn   = deserializedSummary.NalOutcomeColumn;
                workspace.CurrentStatus      = deserializedSummary.Status;
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

        public async Task<Rule39RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 39);
            if (row == null) return null;

            // No browser-preview trimming here — Excel/CSV export reads this summary directly and
            // must see the full (cap-limited-at-save-time, but otherwise complete) row set.
            var summary = DeserializeSummary(row.ResultsJSON) ?? new Rule39ValidationSummary();
            summary.ClientId = row.ClientId;
            if (summary.SavedRunId.GetValueOrDefault() <= 0)
                summary.SavedRunId = runId;

            var review = new Rule39RunReviewViewModel
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

        public async Task<Rule39WorkspaceSaveResult> SaveWorkspaceAsync(Rule39ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0)
                    return new Rule39WorkspaceSaveResult { Success = false, Error = "Run the validation first so the workspace can be saved." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule39WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.NalTable,
                    StudColumn = request.StudQualRefColumn,
                    DeceasedColumn = request.StudFirstTimeColumn
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule39WorkspaceSaveResult
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
                return new Rule39WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule39WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule39WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule39WorkspaceSaveResult
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
                return new Rule39WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 39 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 39 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 39 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule39ValidationRequest request)
        {
            var cfg = await ResolveColumnConfigAsync(request);

            var sql = $@"-- HEMIS RULE 39: FIRST-TIME ENTERING STUDENTS VS NON-ALIGNED QUALIFICATIONS
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- STUD Table: ""{request.StudTable}""  |  Filter: ""{cfg.StudFirstTimeColumn}"" = '{cfg.StudFirstTimeValue}'
-- QUAL Table: ""{request.QualTable}""  |  Join: STUD.""{cfg.StudQualRefColumn}"" = QUAL.""{cfg.QualCodeColumn}""
-- NAL Table : ""{request.NalTable}""  |  Filter: ""{cfg.NalCategoryColumn}"" = '{cfg.NalCategoryValue}'
-- Join key  : QUAL.""{cfg.QualCodeColumn}"" (or STUD.""{cfg.StudQualRefColumn}"" if unmatched) = NAL.""{cfg.NalRefColumn}""

{BuildRule39PrepSql("{schema}", request.StudTable, request.QualTable, request.NalTable, cfg)}

-- Full population (FLAGGED + CLEAR)
SELECT * FROM rule39_population ORDER BY row_number;

-- Summary
SELECT
    COUNT(*) AS total_fte,
    COUNT(*) FILTER (WHERE result = 'FLAGGED') AS flagged,
    COUNT(*) FILTER (WHERE result = 'CLEAR') AS clear,
    ROUND(COUNT(*) FILTER (WHERE result = 'FLAGGED') * 100.0 / NULLIF(COUNT(*), 0), 2) AS exception_rate_pct
FROM rule39_population;";

            return sql.Trim();
        }

        // ── Analysis ─────────────────────────────────────────────────────────────────────

        private async Task<Rule39ValidationSummary> AnalyseAsync(Rule39ValidationRequest request)
        {
            var cfg = await ResolveColumnConfigAsync(request);
            await EnsureRule39IndexesAsync(request.ClientId, request.StudTable, request.QualTable, request.NalTable, cfg);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var studTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";");
            var qualTotal = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.QualTable}\";");
            var nalCategoryCount = await CountAsync(connection,
                $"SELECT COUNT(*) FROM \"{schema}\".\"{request.NalTable}\" WHERE UPPER(TRIM(CAST(\"{cfg.NalCategoryColumn}\" AS text))) = '{EscapeSqlString(cfg.NalCategoryValue.ToUpperInvariant())}';");

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule39PrepSql(schema, request.StudTable, request.QualTable, request.NalTable, cfg);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var studFiltered = await CountAsync(connection, "SELECT COUNT(*) FROM rule39_population;");
            var (flaggedCount, clearCount) = await GetResultCountsAsync(connection);
            var flaggedRows = await LoadRowsWhereAsync(connection, "FLAGGED", MaxFlaggedRows);
            var clearRows = await LoadRowsWhereAsync(connection, "CLEAR", ClearSampleSize);

            var rate = studFiltered == 0 ? 0m : Math.Round((decimal)flaggedCount / studFiltered * 100m, 2);

            return new Rule39ValidationSummary
            {
                Success = true,
                TotalValidated = studFiltered,
                FlaggedCount = flaggedCount,
                ClearCount = clearCount,
                ExceptionRate = rate,
                Status = flaggedCount == 0 ? "PASS" : "FAIL",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable = request.StudTable,
                QualTable = request.QualTable,
                NalTable = request.NalTable,
                StudQualRefColumn = cfg.StudQualRefColumn,
                Stud007Column = cfg.Stud007Column ?? "",
                Stud008Column = cfg.Stud008Column ?? "",
                StudFirstTimeColumn = cfg.StudFirstTimeColumn,
                Stud012Column = cfg.Stud012Column ?? "",
                Stud026Column = cfg.Stud026Column ?? "",
                StudFirstTimeValue = cfg.StudFirstTimeValue,
                QualCodeColumn = cfg.QualCodeColumn,
                QualNameColumn = cfg.QualNameColumn,
                NalRefColumn = cfg.NalRefColumn,
                NalNameColumn = cfg.NalNameColumn,
                NalAlignedColumn = cfg.NalAlignedColumn ?? "",
                NalCategoryColumn = cfg.NalCategoryColumn,
                NalCategoryValue = cfg.NalCategoryValue,
                NalHeqsfRefColumn = cfg.NalHeqsfRefColumn ?? "",
                NalSaqaIdColumn = cfg.NalSaqaIdColumn ?? "",
                NalNqfColumn = cfg.NalNqfColumn ?? "",
                NalCreditsColumn = cfg.NalCreditsColumn ?? "",
                NalOutcomeColumn = cfg.NalOutcomeColumn ?? "",
                StudTotalCount = studTotal,
                QualTotalCount = qualTotal,
                NalCategoryCount = nalCategoryCount,
                ClientId = request.ClientId,
                FlaggedRows = flaggedRows,
                ClearSampleRows = clearRows,
                Warning = BuildScaleWarning(flaggedCount, flaggedRows.Count, clearCount, clearRows.Count)
            };
        }

        // Re-runs the same analysis a fresh "Run Validation" would, without the save-run side
        // effect - used by the Excel export path.
        public async Task<Rule39ValidationSummary> GetExportSummaryAsync(Rule39ValidationRequest request) =>
            await AnalyseAsync(request);

        // Cheap population size check - runs the same server-side prep SQL as a full export but
        // stops at a COUNT(*), no result rows loaded. Reports FLAGGED + the (deliberately capped)
        // CLEAR sample, since that's what a CSV/Excel download will actually contain. Mirrors
        // Rule21Service.GetPopulationCountAsync.
        public async Task<int> GetPopulationCountAsync(Rule39ValidationRequest request)
        {
            var cfg = await ResolveColumnConfigAsync(request);
            await EnsureRule39IndexesAsync(request.ClientId, request.StudTable, request.QualTable, request.NalTable, cfg);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule39PrepSql(schema, request.StudTable, request.QualTable, request.NalTable, cfg);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var (flaggedCount, clearCount) = await GetResultCountsAsync(connection);
            return flaggedCount + Math.Min(clearCount, ClearSampleSize);
        }

        // Bypasses AnalyseAsync/LoadRowsWhereAsync entirely for the FLAGGED side - that's the
        // audit-relevant exception population and was previously capped at MaxFlaggedRows (5,000)
        // regardless of the real count. Reads and writes FLAGGED rows one at a time with no cap;
        // the CLEAR side stays a deliberate fixed-size sample (ClearSampleSize), same as before -
        // that's an intentional sample for context, not a truncation bug. Mirrors
        // Rule21Service.StreamCsvExportAsync.
        public async Task StreamCsvExportAsync(Rule39ValidationRequest request, bool onlyExceptions, Stream outputStream)
        {
            var cfg = await ResolveColumnConfigAsync(request);
            await EnsureRule39IndexesAsync(request.ClientId, request.StudTable, request.QualTable, request.NalTable, cfg);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule39PrepSql(schema, request.StudTable, request.QualTable, request.NalTable, cfg);
                await prepCommand.ExecuteNonQueryAsync();
            }

            await using var writer = new StreamWriter(outputStream, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = false };

            await using var command = connection.CreateCommand();
            command.CommandText = onlyExceptions
                ? "SELECT * FROM rule39_population WHERE result = 'FLAGGED' ORDER BY row_number;"
                : $@"
SELECT * FROM ( SELECT * FROM rule39_population WHERE result = 'FLAGGED' ORDER BY row_number ) flagged
UNION ALL
SELECT * FROM ( SELECT * FROM rule39_population WHERE result = 'CLEAR' ORDER BY row_number LIMIT {ClearSampleSize} ) clear_sample;";
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

        private static string? BuildScaleWarning(int flaggedCount, int flaggedLoaded, int clearCount, int clearLoaded)
        {
            if (flaggedCount > flaggedLoaded)
                return $"{flaggedCount:N0} FLAGGED rows were found; only the first {flaggedLoaded:N0} are stored and shown to keep the app responsive. All totals above are exact — the FLAGGED rows shown are complete up to this cap.";
            if (clearCount > clearLoaded)
                return $"CLEAR rows are stored as a representative sample ({clearLoaded:N0} of {clearCount:N0}). FLAGGED rows (the actionable exceptions) are complete. All totals above are exact.";
            return null;
        }

        private static string BuildRule39PrepSql(string schema, string studTable, string qualTable, string nalTable, Rule39ColumnConfig cfg)
        {
            string OptStud(string? col) => col == null ? "NULL::text" : $@"CAST(s.""{col}"" AS text)";
            string OptNal(string? col) => col == null ? "NULL::text" : $@"CAST(""{col}"" AS text)";

            var ftv = EscapeSqlString(cfg.StudFirstTimeValue.ToUpperInvariant());
            var catv = EscapeSqlString(cfg.NalCategoryValue.ToUpperInvariant());

            return $@"
DROP TABLE IF EXISTS rule39_qual;
CREATE TEMP TABLE rule39_qual AS
SELECT DISTINCT ON (norm_code) norm_code, qual_code, qual_name FROM (
    SELECT
        UPPER(TRIM(CAST(""{cfg.QualCodeColumn}"" AS text))) AS norm_code,
        CAST(""{cfg.QualCodeColumn}"" AS text) AS qual_code,
        CAST(""{cfg.QualNameColumn}"" AS text) AS qual_name
    FROM ""{schema}"".""{qualTable}""
    WHERE ""{cfg.QualCodeColumn}"" IS NOT NULL
) x
ORDER BY norm_code;
CREATE INDEX ON rule39_qual(norm_code);
ANALYZE rule39_qual;

DROP TABLE IF EXISTS rule39_nal;
CREATE TEMP TABLE rule39_nal AS
SELECT DISTINCT ON (norm_ref) norm_ref, nal_name, nal_aligned, nal_category, nal_heqsf, nal_saqa, nal_nqf, nal_credits, nal_outcome FROM (
    SELECT
        UPPER(TRIM(CAST(""{cfg.NalRefColumn}"" AS text))) AS norm_ref,
        CAST(""{cfg.NalNameColumn}"" AS text) AS nal_name,
        {OptNal(cfg.NalAlignedColumn)} AS nal_aligned,
        CAST(""{cfg.NalCategoryColumn}"" AS text) AS nal_category,
        {OptNal(cfg.NalHeqsfRefColumn)} AS nal_heqsf,
        {OptNal(cfg.NalSaqaIdColumn)} AS nal_saqa,
        {OptNal(cfg.NalNqfColumn)} AS nal_nqf,
        {OptNal(cfg.NalCreditsColumn)} AS nal_credits,
        {OptNal(cfg.NalOutcomeColumn)} AS nal_outcome
    FROM ""{schema}"".""{nalTable}""
    WHERE ""{cfg.NalRefColumn}"" IS NOT NULL
      AND UPPER(TRIM(CAST(""{cfg.NalCategoryColumn}"" AS text))) = '{catv}'
) y
ORDER BY norm_ref;
CREATE INDEX ON rule39_nal(norm_ref);
ANALYZE rule39_nal;

DROP TABLE IF EXISTS rule39_population;
CREATE TEMP TABLE rule39_population AS
SELECT
    ROW_NUMBER() OVER (ORDER BY UPPER(TRIM(CAST(s.""{cfg.StudQualRefColumn}"" AS text))), q.qual_name) AS row_number,
    CAST(s.""{cfg.StudQualRefColumn}"" AS text) AS stud_qual_ref,
    {OptStud(cfg.Stud007Column)} AS stud_007,
    {OptStud(cfg.Stud008Column)} AS stud_008,
    CAST(s.""{cfg.StudFirstTimeColumn}"" AS text) AS stud_010,
    {OptStud(cfg.Stud012Column)} AS stud_012,
    {OptStud(cfg.Stud026Column)} AS stud_026,
    q.qual_code AS qual_code,
    q.qual_name AS qual_name,
    n.nal_name AS nal_name,
    n.nal_aligned AS nal_aligned,
    n.nal_category AS nal_category,
    n.nal_heqsf AS nal_heqsf,
    n.nal_saqa AS nal_saqa,
    n.nal_nqf AS nal_nqf,
    n.nal_credits AS nal_credits,
    n.nal_outcome AS nal_outcome,
    CASE WHEN n.norm_ref IS NOT NULL THEN 'FLAGGED' ELSE 'CLEAR' END AS result
FROM ""{schema}"".""{studTable}"" s
LEFT JOIN rule39_qual q ON UPPER(TRIM(CAST(s.""{cfg.StudQualRefColumn}"" AS text))) = q.norm_code
LEFT JOIN rule39_nal n ON COALESCE(q.norm_code, UPPER(TRIM(CAST(s.""{cfg.StudQualRefColumn}"" AS text)))) = n.norm_ref
WHERE UPPER(TRIM(CAST(s.""{cfg.StudFirstTimeColumn}"" AS text))) = '{ftv}';

CREATE INDEX ON rule39_population(result);
ANALYZE rule39_population;";
        }

        private static async Task<(int Flagged, int Clear)> GetResultCountsAsync(NpgsqlConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    COUNT(*) FILTER (WHERE result = 'FLAGGED'),
    COUNT(*) FILTER (WHERE result = 'CLEAR')
FROM rule39_population;";
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return (GetInt(reader, 0), GetInt(reader, 1));
            return (0, 0);
        }

        private static async Task<List<Rule39ValidationRowViewModel>> LoadRowsWhereAsync(NpgsqlConnection connection, string result, int limit)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM rule39_population WHERE result = @result ORDER BY row_number LIMIT @limit;";
            command.Parameters.AddWithValue("result", result);
            command.Parameters.AddWithValue("limit", limit);

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule39ValidationRowViewModel>();
            while (await reader.ReadAsync())
            {
                var qualRef = GetString(reader, "stud_qual_ref");
                var qualCode = GetString(reader, "qual_code");
                var qualName = GetString(reader, "qual_name");
                var nalName = GetString(reader, "nal_name");
                var cat = GetString(reader, "nal_category");
                var res = GetString(reader, "result") ?? "CLEAR";

                rows.Add(new Rule39ValidationRowViewModel
                {
                    RowNumber = Convert.ToInt32(reader.GetValue(reader.GetOrdinal("row_number"))),
                    StudQualRef = qualRef ?? "",
                    Stud007Value = GetString(reader, "stud_007") ?? "",
                    Stud008Value = GetString(reader, "stud_008") ?? "",
                    Stud010Value = GetString(reader, "stud_010") ?? "",
                    Stud012Value = GetString(reader, "stud_012") ?? "",
                    Stud026Value = GetString(reader, "stud_026") ?? "",
                    QualCodeValue = qualCode ?? "",
                    QualNameValue = qualName ?? "",
                    NalQualName = nalName,
                    NalAlignedName = GetString(reader, "nal_aligned"),
                    NalCategory = cat,
                    NalHeqsfRef = GetString(reader, "nal_heqsf"),
                    NalSaqaId = GetString(reader, "nal_saqa"),
                    NalNqf = GetString(reader, "nal_nqf"),
                    NalCredits = GetString(reader, "nal_credits"),
                    NalOutcome = GetString(reader, "nal_outcome"),
                    Result = res,
                    ExceptionReason = string.Equals(res, "FLAGGED", StringComparison.OrdinalIgnoreCase)
                        ? $"Qualification '{(string.IsNullOrWhiteSpace(qualCode) ? qualRef : qualCode)}' ({(string.IsNullOrWhiteSpace(qualName) ? "Unknown qualification" : qualName)}) found in Category '{cat}' Non-Aligned list: '{nalName}'"
                        : null
                });
            }
            return rows;
        }

        private static void ApplyBrowserPreview(Rule39ValidationSummary summary)
        {
            var flaggedRows = summary.FlaggedRows ?? new List<Rule39ValidationRowViewModel>();
            var clearRows = summary.ClearSampleRows ?? new List<Rule39ValidationRowViewModel>();

            if (flaggedRows.Count <= BrowserPreviewPerResultLimit && clearRows.Count <= BrowserPreviewPerResultLimit)
            {
                summary.IsPreviewOnly = false;
                summary.PreviewLimit = 0;
                return;
            }

            summary.FlaggedRows = flaggedRows.Take(BrowserPreviewPerResultLimit).ToList();
            summary.ClearSampleRows = clearRows.Take(BrowserPreviewPerResultLimit).ToList();
            summary.IsPreviewOnly = true;
            summary.PreviewLimit = BrowserPreviewPerResultLimit;
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule39ValidationRequest request, Rule39ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 39);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 39,
                RuleName = "First-Time Entering vs Non-Aligned Qualifications",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.ClearCount,
                FailCount = summary.FlaggedCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.NalTable,
                StudColumn = summary.StudQualRefColumn,
                DeceasedColumn = summary.StudFirstTimeColumn,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.FlaggedRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Column configuration resolution (degrades optional display columns to NULL when
        //    the uploaded table doesn't have them, instead of hard-failing the whole rule) ────

        private sealed class Rule39ColumnConfig
        {
            public string StudQualRefColumn = "_001";
            public string StudFirstTimeColumn = "_010";
            public string StudFirstTimeValue = "F";
            public string? Stud007Column;
            public string? Stud008Column;
            public string? Stud012Column;
            public string? Stud026Column;
            public string QualCodeColumn = "_001";
            public string QualNameColumn = "_003";
            public string NalRefColumn = "";
            public string NalNameColumn = "";
            public string? NalAlignedColumn;
            public string NalCategoryColumn = "";
            public string NalCategoryValue = "C";
            public string? NalHeqsfRefColumn;
            public string? NalSaqaIdColumn;
            public string? NalNqfColumn;
            public string? NalCreditsColumn;
            public string? NalOutcomeColumn;
        }

        private async Task<Rule39ColumnConfig> ResolveColumnConfigAsync(Rule39ValidationRequest request)
        {
            var cfg = new Rule39ColumnConfig
            {
                StudQualRefColumn = Default(request.StudQualRefColumn, "_001"),
                StudFirstTimeColumn = Default(request.StudFirstTimeColumn, "_010"),
                StudFirstTimeValue = Default(request.StudFirstTimeValue, "F"),
                Stud007Column = NullIfBlank(request.Stud007Column),
                Stud008Column = NullIfBlank(request.Stud008Column),
                Stud012Column = NullIfBlank(request.Stud012Column),
                Stud026Column = NullIfBlank(request.Stud026Column),
                QualCodeColumn = Default(request.QualCodeColumn, "_001"),
                QualNameColumn = Default(request.QualNameColumn, "_003"),
                NalRefColumn = request.NalRefColumn,
                NalNameColumn = request.NalNameColumn,
                NalAlignedColumn = NullIfBlank(request.NalAlignedColumn),
                NalCategoryColumn = request.NalCategoryColumn,
                NalCategoryValue = Default(request.NalCategoryValue, "C"),
                NalHeqsfRefColumn = NullIfBlank(request.NalHeqsfRefColumn),
                NalSaqaIdColumn = NullIfBlank(request.NalSaqaIdColumn),
                NalNqfColumn = NullIfBlank(request.NalNqfColumn),
                NalCreditsColumn = NullIfBlank(request.NalCreditsColumn),
                NalOutcomeColumn = NullIfBlank(request.NalOutcomeColumn)
            };

            var studColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.StudTable);
            var qualColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.QualTable);
            var nalColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.NalTable);
            EnsureHasColumns(request.StudTable, studColumns, cfg.StudQualRefColumn, cfg.StudFirstTimeColumn);
            EnsureHasColumns(request.QualTable, qualColumns, cfg.QualCodeColumn, cfg.QualNameColumn);
            EnsureHasColumns(request.NalTable, nalColumns, cfg.NalRefColumn, cfg.NalNameColumn, cfg.NalCategoryColumn);

            var studSet = new HashSet<string>(studColumns, StringComparer.OrdinalIgnoreCase);
            var nalSet = new HashSet<string>(nalColumns, StringComparer.OrdinalIgnoreCase);
            if (cfg.Stud007Column != null && !studSet.Contains(cfg.Stud007Column)) cfg.Stud007Column = null;
            if (cfg.Stud008Column != null && !studSet.Contains(cfg.Stud008Column)) cfg.Stud008Column = null;
            if (cfg.Stud012Column != null && !studSet.Contains(cfg.Stud012Column)) cfg.Stud012Column = null;
            if (cfg.Stud026Column != null && !studSet.Contains(cfg.Stud026Column)) cfg.Stud026Column = null;
            if (cfg.NalAlignedColumn != null && !nalSet.Contains(cfg.NalAlignedColumn)) cfg.NalAlignedColumn = null;
            if (cfg.NalHeqsfRefColumn != null && !nalSet.Contains(cfg.NalHeqsfRefColumn)) cfg.NalHeqsfRefColumn = null;
            if (cfg.NalSaqaIdColumn != null && !nalSet.Contains(cfg.NalSaqaIdColumn)) cfg.NalSaqaIdColumn = null;
            if (cfg.NalNqfColumn != null && !nalSet.Contains(cfg.NalNqfColumn)) cfg.NalNqfColumn = null;
            if (cfg.NalCreditsColumn != null && !nalSet.Contains(cfg.NalCreditsColumn)) cfg.NalCreditsColumn = null;
            if (cfg.NalOutcomeColumn != null && !nalSet.Contains(cfg.NalOutcomeColumn)) cfg.NalOutcomeColumn = null;

            return cfg;
        }

        private static void EnsureHasColumns(string tableName, IReadOnlyCollection<string> availableColumns, params string[] requiredColumns)
        {
            var missing = requiredColumns.Where(required => !availableColumns.Contains(required, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"Table {tableName} is missing required column(s): {string.Join(", ", missing)}.");
        }

        private async Task EnsureRule39IndexesAsync(int clientId, string studTable, string qualTable, string nalTable, Rule39ColumnConfig cfg)
        {
            await _datasets.EnsureJoinIndexAsync(clientId, studTable, cfg.StudQualRefColumn);
            await _datasets.EnsureJoinIndexAsync(clientId, studTable, cfg.StudFirstTimeColumn);
            await _datasets.EnsureJoinIndexAsync(clientId, qualTable, cfg.QualCodeColumn);
            await _datasets.EnsureJoinIndexAsync(clientId, nalTable, cfg.NalRefColumn);
            await _datasets.EnsureJoinIndexAsync(clientId, nalTable, cfg.NalCategoryColumn);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────

        private static string Default(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
        private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private static string? GetString(System.Data.Common.DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            if (reader.IsDBNull(ordinal)) return null;
            var value = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static int GetInt(System.Data.Common.DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

        private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static string EscapeSqlString(string? value) => (value ?? "").Replace("'", "''");

        private static string? FindFirst(IEnumerable<string> values, IEnumerable<string> preferredExact, IEnumerable<string> preferredContains)
        {
            var list = values.ToList();
            foreach (var exact in preferredExact)
            {
                var match = list.FirstOrDefault(value => value.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            foreach (var contains in preferredContains)
            {
                var match = list.FirstOrDefault(value => value.Contains(contains, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return list.FirstOrDefault();
        }

        private static Rule39ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule39ValidationSummary>(decoded);
            }
            catch { return null; }
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
    }
}
