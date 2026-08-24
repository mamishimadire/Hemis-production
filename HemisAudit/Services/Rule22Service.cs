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
    // Rule 22: Staff Validation (dbo_PROF) — validates against the engagement's own uploaded
    // Supabase data instead of a live SQL Server connection. Ported from the Rule18/20 pattern.
    // 100% population testing: every PROF row is classified into exactly one of three mutually
    // exclusive controls based on Column041 (employment status) and Column039 (personnel
    // category); rows matching none of the three controls are silently excluded (matching the
    // original design exactly — Rule22 never counted "Unclassified" rows). Every included row is
    // PASS by construction. Unlike the JOIN-based rules (18/19/20), this is a pure single-table
    // classification — no join indexes are useful here since every row must be scanned regardless.
    public class Rule22Service : IRule22Service
    {
        private const int ReviewPreviewRowsPerControl = 100;
        // Applied per control (sample_number resets per control via PARTITION BY), so a client
        // with a genuinely large single control was silently truncated at 5,000 regardless of its
        // real size. CSV export now bypasses this entirely via StreamCsvExportAsync (true
        // streaming, no cap). Raised for the Excel/interactive path now the Render service has
        // 4GB - matches Excel's own 1,048,576-row-per-sheet format ceiling per control, same
        // reasoning as Rule 12/18.
        private const int MaxExportRowsPerControl = 1_048_576;
        private const int BrowserPreviewRowLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule22Service(
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

        public async Task<Rule22TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule22TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule22TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoProfTable = FindFirst(tables, ["dbo_PROF", "dbo_prof", "PROF", "prof"], ["prof"])
                };
            }
            catch (Exception ex)
            {
                return new Rule22TableDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule22ColumnResult> GetProfColumnsAsync(int clientId, string profTable)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, profTable);
                return new Rule22ColumnResult
                {
                    Success = true,
                    Columns = columns,
                    AutoColumn037 = FindFirst(columns, ["_037"], ["037", "staff"]),
                    AutoColumn038 = FindFirst(columns, ["_038"], ["038", "year", "commencement"]),
                    AutoColumn039 = FindFirst(columns, ["_039"], ["039", "personnel", "category"]),
                    AutoColumn040 = FindFirst(columns, ["_040"], ["040", "rank"]),
                    AutoColumn011 = FindFirst(columns, ["_011"], ["011", "birth"]),
                    AutoColumn012 = FindFirst(columns, ["_012"], ["012", "gender"]),
                    AutoColumn013 = FindFirst(columns, ["_013"], ["013", "race"]),
                    AutoColumn014 = FindFirst(columns, ["_014"], ["014", "national"]),
                    AutoColumn041 = FindFirst(columns, ["_041"], ["041", "employment", "permanent", "temporary"]),
                    AutoColumn042 = FindFirst(columns, ["_042"], ["042", "part", "full", "status"]),
                    AutoColumn046 = FindFirst(columns, ["_046"], ["046", "qual"]),
                    AutoColumn047 = FindFirst(columns, ["_047"], ["047", "joint", "appoint"]),
                    AutoColumn048 = FindFirst(columns, ["_048"], ["048", "payroll"]),
                    AutoColumn094 = FindFirst(columns, ["_094"], ["094", "research", "fellow"])
                };
            }
            catch (Exception ex)
            {
                return new Rule22ColumnResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule22VerifyResult> VerifyTablesAsync(Rule22VerifyRequest request)
        {
            try
            {
                var cfg = await ResolveColumnConfigAsync(request.ClientId, request.ProfTable, ToValidationRequest(request));
                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var profTable = request.ProfTable;
                var control1Count = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{profTable}\" WHERE {BuildControl1Condition(cfg)};");
                var control2Count = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{profTable}\" WHERE {BuildControl2Condition(cfg)};");
                var control3Count = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{profTable}\" WHERE {BuildControl3Condition(cfg)};");

                return new Rule22VerifyResult
                {
                    Success = true,
                    TotalCount = control1Count + control2Count + control3Count,
                    Control1Count = control1Count,
                    Control2Count = control2Count,
                    Control3Count = control3Count,
                    UnclassifiedCount = 0
                };
            }
            catch (Exception ex)
            {
                return new Rule22VerifyResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule22ValidationSummary> RunValidationAsync(Rule22ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                var summary = await AnalyseAsync(request, includeAllReviewRows: false);
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
                return new Rule22ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule22ValidationSummary> GetExportSummaryAsync(Rule22ValidationRequest request) =>
            await AnalyseAsync(request, includeAllReviewRows: true);

        // Cheap population size check - runs the same server-side prep SQL as a full export but
        // stops at a COUNT(*), no result rows loaded. Mirrors Rule12Service.GetPopulationCountAsync.
        public async Task<int> GetPopulationCountAsync(Rule22ValidationRequest request)
        {
            var cfg = await ResolveColumnConfigAsync(request.ClientId, request.ProfTable, request);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule22PrepSql(schema, request.ProfTable, cfg);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var counts = await GetControlCountsAsync(connection);
            return counts.Values.Sum();
        }

        // Bypasses AnalyseAsync/LoadReviewRowsAsync entirely - those buffer every row into a full
        // List<...> before anything can be written out, capped at MaxExportRowsPerControl per
        // control besides. This reads and writes one row at a time directly from
        // rule22_population, so memory use stays roughly constant and nothing is capped, no
        // matter how large any one control's population is. Mirrors
        // Rule12Service.StreamCsvExportAsync.
        public async Task StreamCsvExportAsync(Rule22ValidationRequest request, Stream outputStream)
        {
            var cfg = await ResolveColumnConfigAsync(request.ClientId, request.ProfTable, request);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule22PrepSql(schema, request.ProfTable, cfg);
                await prepCommand.ExecuteNonQueryAsync();
            }

            await using var writer = new StreamWriter(outputStream, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = false };

            var headerParts = new List<string>
            {
                "Control_Type", "Control_Definition", "Staff_Number", cfg.Column038, cfg.Column039, cfg.Column040,
                cfg.Column011, cfg.Column012, cfg.Column013, cfg.Column014, cfg.Column041, cfg.Column042,
                cfg.Column046, cfg.Column047, cfg.Column048, cfg.Column094, "Validation_Result"
            };
            await writer.WriteLineAsync(string.Join(",", headerParts.Select(StreamCsvEscape)));

            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT control_type, control_definition, staff_number, year_038, col_039, col_040, col_011, col_012, col_013,
       col_014, col_041, col_042, col_046, col_047, col_048, col_094
FROM rule22_population
ORDER BY
    CASE control_type WHEN 'Control 1' THEN 1 WHEN 'Control 2' THEN 2 WHEN 'Control 3' THEN 3 ELSE 4 END,
    sample_number;";

            await using var reader = await command.ExecuteReaderAsync();

            var ordControlType = reader.GetOrdinal("control_type");
            var ordControlDefinition = reader.GetOrdinal("control_definition");
            var ordStaffNumber = reader.GetOrdinal("staff_number");
            var ordYear038 = reader.GetOrdinal("year_038");
            var ordCol039 = reader.GetOrdinal("col_039");
            var ordCol040 = reader.GetOrdinal("col_040");
            var ordCol011 = reader.GetOrdinal("col_011");
            var ordCol012 = reader.GetOrdinal("col_012");
            var ordCol013 = reader.GetOrdinal("col_013");
            var ordCol014 = reader.GetOrdinal("col_014");
            var ordCol041 = reader.GetOrdinal("col_041");
            var ordCol042 = reader.GetOrdinal("col_042");
            var ordCol046 = reader.GetOrdinal("col_046");
            var ordCol047 = reader.GetOrdinal("col_047");
            var ordCol048 = reader.GetOrdinal("col_048");
            var ordCol094 = reader.GetOrdinal("col_094");

            string GetVal(int ord) => ord < 0 || reader.IsDBNull(ord)
                ? ""
                : Convert.ToString(reader.GetValue(ord), CultureInfo.InvariantCulture) ?? "";

            var rowValues = new List<string>(17);
            while (await reader.ReadAsync())
            {
                rowValues.Clear();
                rowValues.Add(StreamCsvEscape(GetVal(ordControlType)));
                rowValues.Add(StreamCsvEscape(GetVal(ordControlDefinition)));
                rowValues.Add(StreamCsvEscape(GetVal(ordStaffNumber)));
                rowValues.Add(StreamCsvEscape(GetVal(ordYear038)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol039)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol040)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol011)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol012)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol013)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol014)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol041)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol042)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol046)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol047)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol048)));
                rowValues.Add(StreamCsvEscape(GetVal(ordCol094)));
                rowValues.Add(StreamCsvEscape("PASS"));
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

        public async Task<Rule22WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 22);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule22WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                ProfTable = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_PROF" : row.StudTable,
                Column041 = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "_041" : row.DeceasedTable,
                Column039 = string.IsNullOrWhiteSpace(row.StudColumn) ? "_039" : row.StudColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt = row.LastEditedAt,
                Summary = summary
            };

            if (deserializedSummary != null)
            {
                workspace.Column037 = deserializedSummary.Column037;
                workspace.Column038 = deserializedSummary.Column038;
                workspace.Column039 = deserializedSummary.Column039;
                workspace.Column040 = deserializedSummary.Column040;
                workspace.Column011 = deserializedSummary.Column011;
                workspace.Column012 = deserializedSummary.Column012;
                workspace.Column013 = deserializedSummary.Column013;
                workspace.Column014 = string.IsNullOrWhiteSpace(deserializedSummary.Column014) ? "_014" : deserializedSummary.Column014;
                workspace.Column041 = deserializedSummary.Column041;
                workspace.Column042 = deserializedSummary.Column042;
                workspace.Column046 = deserializedSummary.Column046;
                workspace.Column047 = string.IsNullOrWhiteSpace(deserializedSummary.Column047) ? "_047" : deserializedSummary.Column047;
                workspace.Column048 = deserializedSummary.Column048;
                workspace.Column094 = string.IsNullOrWhiteSpace(deserializedSummary.Column094) ? "_094" : deserializedSummary.Column094;
                workspace.Control1SampleSize = deserializedSummary.Control1SampleSize;
                workspace.Control2SampleSize = deserializedSummary.Control2SampleSize;
                workspace.Control3SampleSize = deserializedSummary.Control3SampleSize;
                workspace.FilterValue041 = string.IsNullOrWhiteSpace(deserializedSummary.FilterValue041) ? "PE" : deserializedSummary.FilterValue041;
                workspace.FilterValue039 = string.IsNullOrWhiteSpace(deserializedSummary.FilterValue039) ? "01" : deserializedSummary.FilterValue039;
                workspace.CurrentStatus = deserializedSummary.Status;
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

            return workspace;
        }

        public async Task<Rule22RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 22);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;

            var review = new Rule22RunReviewViewModel
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

        public async Task<Rule22WorkspaceSaveResult> SaveWorkspaceAsync(Rule22ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0)
                    return new Rule22WorkspaceSaveResult { Success = false, Error = "Run the validation first so the workspace can be saved." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule22WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.ProfTable,
                    DeceasedTable = request.Column041,
                    StudColumn = request.Column039
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule22WorkspaceSaveResult
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
                return new Rule22WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule22WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule22WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule22WorkspaceSaveResult
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
                return new Rule22WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 22 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 22 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 22 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule22ValidationRequest request)
        {
            var cfg = await ResolveColumnConfigAsync(request.ClientId, request.ProfTable, request);

            var sql = $@"-- HEMIS RULE 22: STAFF VALIDATION (dbo_PROF)
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Table    : ""{request.ProfTable}""
-- Scope    : 100% Population Validation
--
-- Control 1: ""{cfg.Column041}"" = '{cfg.FilterValue041}' AND ""{cfg.Column039}"" = '{cfg.FilterValue039}'
-- Control 2: ""{cfg.Column041}"" = '{cfg.FilterValue041}' AND ""{cfg.Column039}"" <> '{cfg.FilterValue039}'
-- Control 3: ""{cfg.Column041}"" <> '{cfg.FilterValue041}' AND ""{cfg.Column039}"" <> '{cfg.FilterValue039}'

{BuildRule22PrepSql("{schema}", request.ProfTable, cfg)}

-- Full population (Control 1 + Control 2 + Control 3)
SELECT * FROM rule22_population ORDER BY
    CASE control_type WHEN 'Control 1' THEN 1 WHEN 'Control 2' THEN 2 WHEN 'Control 3' THEN 3 ELSE 4 END,
    sample_number;

-- Summary
SELECT control_type, COUNT(*) AS row_count FROM rule22_population GROUP BY control_type;";

            return sql.Trim();
        }

        // ── Analysis ─────────────────────────────────────────────────────────────────────

        private async Task<Rule22ValidationSummary> AnalyseAsync(Rule22ValidationRequest request, bool includeAllReviewRows)
        {
            var cfg = await ResolveColumnConfigAsync(request.ClientId, request.ProfTable, request);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule22PrepSql(schema, request.ProfTable, cfg);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var counts = await GetControlCountsAsync(connection);
            var control1Count = counts.GetValueOrDefault("Control 1");
            var control2Count = counts.GetValueOrDefault("Control 2");
            var control3Count = counts.GetValueOrDefault("Control 3");
            var totalValidated = control1Count + control2Count + control3Count;

            var perControlLimit = includeAllReviewRows ? MaxExportRowsPerControl : ReviewPreviewRowsPerControl;
            var reviewRows = await LoadReviewRowsAsync(connection, perControlLimit);

            var loadedControl1 = reviewRows.Count(r => r.ControlType == "Control 1");
            var loadedControl2 = reviewRows.Count(r => r.ControlType == "Control 2");
            var loadedControl3 = reviewRows.Count(r => r.ControlType == "Control 3");
            var wasCapped = loadedControl1 < control1Count || loadedControl2 < control2Count || loadedControl3 < control3Count;

            return new Rule22ValidationSummary
            {
                Success = true,
                TotalValidated = totalValidated,
                PassCount = totalValidated,
                FailCount = 0,
                ExceptionRate = 0m,
                Status = "PASS",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ProfTable = request.ProfTable,
                Column037 = cfg.Column037,
                Column038 = cfg.Column038,
                Column039 = cfg.Column039,
                Column040 = cfg.Column040,
                Column011 = cfg.Column011,
                Column012 = cfg.Column012,
                Column013 = cfg.Column013,
                Column014 = cfg.Column014,
                Column041 = cfg.Column041,
                Column042 = cfg.Column042,
                Column046 = cfg.Column046,
                Column047 = cfg.Column047,
                Column048 = cfg.Column048,
                Column094 = cfg.Column094,
                FilterValue041 = cfg.FilterValue041,
                FilterValue039 = cfg.FilterValue039,
                Control1SampleSize = 0,
                Control2SampleSize = 0,
                Control3SampleSize = 0,
                Control1Count = control1Count,
                Control2Count = control2Count,
                Control3Count = control3Count,
                UnclassifiedCount = 0,
                ClientId = request.ClientId,
                ControlSummaries =
                [
                    new Rule22ControlSummaryItemViewModel
                    {
                        ControlType = "Control 1",
                        ControlDefinition = BuildControl1DefinitionText(cfg),
                        AvailableCount = control1Count,
                        RequestedCount = control1Count,
                        SampleCount = loadedControl1,
                        PassCount = control1Count,
                        FailCount = 0
                    },
                    new Rule22ControlSummaryItemViewModel
                    {
                        ControlType = "Control 2",
                        ControlDefinition = BuildControl2DefinitionText(cfg),
                        AvailableCount = control2Count,
                        RequestedCount = control2Count,
                        SampleCount = loadedControl2,
                        PassCount = control2Count,
                        FailCount = 0
                    },
                    new Rule22ControlSummaryItemViewModel
                    {
                        ControlType = "Control 3",
                        ControlDefinition = BuildControl3DefinitionText(cfg),
                        AvailableCount = control3Count,
                        RequestedCount = control3Count,
                        SampleCount = loadedControl3,
                        PassCount = control3Count,
                        FailCount = 0
                    }
                ],
                MappedColumns = Rule22ColumnMappingHelper.Build(request),
                ReviewRows = reviewRows,
                Warning = BuildWarning(includeAllReviewRows, wasCapped, perControlLimit)
            };
        }

        private static string BuildWarning(bool includeAllReviewRows, bool wasCapped, int perControlLimit)
        {
            if (!includeAllReviewRows)
                return $"100% validation completed. Showing the first {perControlLimit:N0} row(s) per control in the browser.";
            return wasCapped
                ? $"100% validation completed for all rows. Row storage is capped at {perControlLimit:N0} per control to keep the app stable; totals above are still exact."
                : "100% validation completed for all Control 1, Control 2, and Control 3 rows.";
        }

        private static async Task<Dictionary<string, int>> GetControlCountsAsync(NpgsqlConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT control_type, COUNT(*) FROM rule22_population GROUP BY control_type;";
            await using var reader = await command.ExecuteReaderAsync();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
                counts[reader.GetString(0)] = Convert.ToInt32(reader.GetValue(1));
            return counts;
        }

        private static async Task<List<Rule22ReviewRowViewModel>> LoadReviewRowsAsync(NpgsqlConnection connection, int perControlLimit)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT * FROM rule22_population
WHERE sample_number <= @limit
ORDER BY
    CASE control_type WHEN 'Control 1' THEN 1 WHEN 'Control 2' THEN 2 WHEN 'Control 3' THEN 3 ELSE 4 END,
    sample_number;";
            command.Parameters.AddWithValue("limit", perControlLimit);

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule22ReviewRowViewModel>();
            var validationNumber = 0;
            while (await reader.ReadAsync())
            {
                validationNumber++;
                rows.Add(new Rule22ReviewRowViewModel
                {
                    ValidationNumber = validationNumber,
                    ControlType = GetString(reader, "control_type") ?? "",
                    ControlDefinition = GetString(reader, "control_definition") ?? "",
                    SampleNumber = Convert.ToInt32(reader.GetValue(reader.GetOrdinal("sample_number"))),
                    StaffNumber037 = GetString(reader, "staff_number") ?? "",
                    Year038 = GetString(reader, "year_038") ?? "",
                    Col039 = GetString(reader, "col_039") ?? "",
                    Col040 = GetString(reader, "col_040") ?? "",
                    Col011 = GetString(reader, "col_011") ?? "",
                    Col012 = GetString(reader, "col_012") ?? "",
                    Col013 = GetString(reader, "col_013") ?? "",
                    Col014 = GetString(reader, "col_014") ?? "",
                    Col041 = GetString(reader, "col_041") ?? "",
                    Col042 = GetString(reader, "col_042") ?? "",
                    Col046 = GetString(reader, "col_046") ?? "",
                    Col047 = GetString(reader, "col_047") ?? "",
                    Col048 = GetString(reader, "col_048") ?? "",
                    Col094 = GetString(reader, "col_094") ?? "",
                    ValidationResult = "PASS",
                    ExceptionReason = ""
                });
            }
            return rows;
        }

        private static void ApplyBrowserPreview(Rule22ValidationSummary summary)
        {
            summary.ReviewRows = summary.ReviewRows.Take(BrowserPreviewRowLimit).ToList();
        }

        private static string BuildRule22PrepSql(string schema, string profTable, Rule22ColumnConfig cfg)
        {
            var controlTypeCase = BuildControlTypeCase(cfg);

            return $@"
DROP TABLE IF EXISTS rule22_population;
CREATE TEMP TABLE rule22_population AS
SELECT
    {controlTypeCase} AS control_type,
    {BuildControlDefinitionCase(cfg)} AS control_definition,
    ROW_NUMBER() OVER (
        PARTITION BY {controlTypeCase}
        ORDER BY
            CASE WHEN CAST(""{cfg.Column037}"" AS text) ~ '^[0-9]+$' THEN 0 ELSE 1 END,
            CASE WHEN CAST(""{cfg.Column037}"" AS text) ~ '^[0-9]+$' THEN CAST(""{cfg.Column037}"" AS numeric) ELSE NULL END,
            CAST(""{cfg.Column037}"" AS text),
            CAST(""{cfg.Column038}"" AS text)
    ) AS sample_number,
    CAST(""{cfg.Column037}"" AS text) AS staff_number,
    CAST(""{cfg.Column038}"" AS text) AS year_038,
    CAST(""{cfg.Column039}"" AS text) AS col_039,
    CAST(""{cfg.Column040}"" AS text) AS col_040,
    CAST(""{cfg.Column011}"" AS text) AS col_011,
    CAST(""{cfg.Column012}"" AS text) AS col_012,
    CAST(""{cfg.Column013}"" AS text) AS col_013,
    CAST(""{cfg.Column014}"" AS text) AS col_014,
    CAST(""{cfg.Column041}"" AS text) AS col_041,
    CAST(""{cfg.Column042}"" AS text) AS col_042,
    CAST(""{cfg.Column046}"" AS text) AS col_046,
    CAST(""{cfg.Column047}"" AS text) AS col_047,
    CAST(""{cfg.Column048}"" AS text) AS col_048,
    CAST(""{cfg.Column094}"" AS text) AS col_094
FROM ""{schema}"".""{profTable}""
WHERE {BuildIncludedCondition(cfg)};

CREATE INDEX ON rule22_population(control_type, sample_number);
ANALYZE rule22_population;";
        }

        // ── Save / Load ──────────────────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule22ValidationRequest request, Rule22ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 22);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 22,
                RuleName = "Staff Validation (dbo_PROF)",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.ProfTable,
                DeceasedTable = summary.Column041,
                StudColumn = summary.Column039,
                ExceptionsJSON = ValidationPayloadCodec.Encode("[]"),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        // ── Column configuration resolution ─────────────────────────────────────────────
        // Unlike Rule19's optional columns, all 14 Rule22 mapped columns are required to exist —
        // matching the original design's strictness (three of them — 014/047/094 — merely fall
        // back to a default column name when left blank, they are not skip-if-missing).

        private sealed class Rule22ColumnConfig
        {
            public string Column037 = "_037";
            public string Column038 = "_038";
            public string Column039 = "_039";
            public string Column040 = "_040";
            public string Column011 = "_011";
            public string Column012 = "_012";
            public string Column013 = "_013";
            public string Column014 = "_014";
            public string Column041 = "_041";
            public string Column042 = "_042";
            public string Column046 = "_046";
            public string Column047 = "_047";
            public string Column048 = "_048";
            public string Column094 = "_094";
            public string FilterValue041 = "PE";
            public string FilterValue039 = "01";
        }

        private static Rule22ValidationRequest ToValidationRequest(Rule22VerifyRequest request) => new()
        {
            ClientId = request.ClientId,
            ProfTable = request.ProfTable,
            Column037 = request.Column037,
            Column038 = request.Column038,
            Column039 = request.Column039,
            Column040 = request.Column040,
            Column011 = request.Column011,
            Column012 = request.Column012,
            Column013 = request.Column013,
            Column014 = request.Column014,
            Column041 = request.Column041,
            Column042 = request.Column042,
            Column046 = request.Column046,
            Column047 = request.Column047,
            Column048 = request.Column048,
            Column094 = request.Column094,
            FilterValue041 = request.FilterValue041,
            FilterValue039 = request.FilterValue039
        };

        private async Task<Rule22ColumnConfig> ResolveColumnConfigAsync(int clientId, string profTable, Rule22ValidationRequest request)
        {
            var cfg = new Rule22ColumnConfig
            {
                Column037 = Default(request.Column037, "_037"),
                Column038 = Default(request.Column038, "_038"),
                Column039 = Default(request.Column039, "_039"),
                Column040 = Default(request.Column040, "_040"),
                Column011 = Default(request.Column011, "_011"),
                Column012 = Default(request.Column012, "_012"),
                Column013 = Default(request.Column013, "_013"),
                Column014 = Default(request.Column014, "_014"),
                Column041 = Default(request.Column041, "_041"),
                Column042 = Default(request.Column042, "_042"),
                Column046 = Default(request.Column046, "_046"),
                Column047 = Default(request.Column047, "_047"),
                Column048 = Default(request.Column048, "_048"),
                Column094 = Default(request.Column094, "_094"),
                FilterValue041 = Default(request.FilterValue041, "PE"),
                FilterValue039 = Default(request.FilterValue039, "01")
            };

            var columns = await _datasets.GetValidatedColumnsAsync(clientId, profTable);
            var required = new[]
            {
                cfg.Column037, cfg.Column038, cfg.Column039, cfg.Column040, cfg.Column011, cfg.Column012,
                cfg.Column013, cfg.Column014, cfg.Column041, cfg.Column042, cfg.Column046, cfg.Column047,
                cfg.Column048, cfg.Column094
            };
            var missing = required.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(column => !columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"Table {profTable} is missing required column(s): {string.Join(", ", missing)}.");

            return cfg;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────

        private static string Default(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

        private static string SqlValue(string columnName) => $@"COALESCE(CAST(""{columnName}"" AS text), '')";

        private static string BuildControl1Condition(Rule22ColumnConfig cfg) =>
            $"{SqlValue(cfg.Column041)} = '{EscapeSqlString(cfg.FilterValue041)}' AND {SqlValue(cfg.Column039)} = '{EscapeSqlString(cfg.FilterValue039)}'";

        private static string BuildControl2Condition(Rule22ColumnConfig cfg) =>
            $"{SqlValue(cfg.Column041)} = '{EscapeSqlString(cfg.FilterValue041)}' AND {SqlValue(cfg.Column039)} <> '{EscapeSqlString(cfg.FilterValue039)}'";

        private static string BuildControl3Condition(Rule22ColumnConfig cfg) =>
            $"{SqlValue(cfg.Column041)} <> '{EscapeSqlString(cfg.FilterValue041)}' AND {SqlValue(cfg.Column039)} <> '{EscapeSqlString(cfg.FilterValue039)}'";

        private static string BuildIncludedCondition(Rule22ColumnConfig cfg) =>
            $"(({BuildControl1Condition(cfg)}) OR ({BuildControl2Condition(cfg)}) OR ({BuildControl3Condition(cfg)}))";

        private static string BuildControlTypeCase(Rule22ColumnConfig cfg) => $@"CASE
        WHEN {BuildControl1Condition(cfg)} THEN 'Control 1'
        WHEN {BuildControl2Condition(cfg)} THEN 'Control 2'
        WHEN {BuildControl3Condition(cfg)} THEN 'Control 3'
        ELSE 'Unclassified'
    END";

        private static string BuildControlDefinitionCase(Rule22ColumnConfig cfg) => $@"CASE
        WHEN {BuildControl1Condition(cfg)} THEN '{EscapeSqlString(BuildControl1DefinitionText(cfg))}'
        WHEN {BuildControl2Condition(cfg)} THEN '{EscapeSqlString(BuildControl2DefinitionText(cfg))}'
        WHEN {BuildControl3Condition(cfg)} THEN '{EscapeSqlString(BuildControl3DefinitionText(cfg))}'
        ELSE 'Did not match Control 1, Control 2, or Control 3'
    END";

        private static string BuildControl1DefinitionText(Rule22ColumnConfig cfg) =>
            $"{cfg.Column041}='{cfg.FilterValue041}' AND {cfg.Column039}='{cfg.FilterValue039}'";

        private static string BuildControl2DefinitionText(Rule22ColumnConfig cfg) =>
            $"{cfg.Column041}='{cfg.FilterValue041}' AND {cfg.Column039}<>'{cfg.FilterValue039}'";

        private static string BuildControl3DefinitionText(Rule22ColumnConfig cfg) =>
            $"{cfg.Column041}<>'{cfg.FilterValue041}' AND {cfg.Column039}<>'{cfg.FilterValue039}'";

        private static string? GetString(System.Data.Common.DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            if (reader.IsDBNull(ordinal)) return null;
            var value = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(value) ? null : value;
        }

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

        private static Rule22ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule22ValidationSummary>(decoded);
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
