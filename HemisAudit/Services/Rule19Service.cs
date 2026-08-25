using System.Globalization;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 19: Masters and PhD Population Validation — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. Ported from the Rule18
    // pattern. 100% population testing: STUD INNER JOIN QUAL (required), optionally INNER JOIN
    // CRED/CRSE for course linkage, optionally LEFT JOIN PQM for qualification-name/type
    // validation. Every returned row's Rule19_Filter_Check is PASS by construction (the WHERE
    // clause already applies the STUD fulfilled-column and QUAL type-column filters); PASS/FAIL
    // is driven entirely by PQM_Validation_Result when a PQM table is configured, otherwise every
    // row is PASS (matching the original design exactly).
    public class Rule19Service : IRule19Service
    {
        private const int BrowserPreviewRowLimit = 10;
        private static readonly string[] DefaultMdTypes = ["07", "27", "28", "49", "72", "73", "08", "30", "50", "74", "75"];
        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule19Service(
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

        public async Task<Rule19TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule19TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule19TableDiscoveryResult
                {
                    Success = true,
                    Tables = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "dbo_stud", "STUD", "stud"], ["stud"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "dbo_qual", "QUAL", "qual"], ["qual"]),
                    AutoPqmTable = FindFirst(tables, ["PQM", "pqm"], ["pqm"]),
                    AutoCredTable = FindFirst(tables, ["dbo_CRED", "dbo_cred", "CRED", "cred"], ["cred"]),
                    AutoCrseTable = FindFirst(tables, ["dbo_CRSE", "dbo_crse", "CRSE", "crse"], ["crse"])
                };
            }
            catch (Exception ex)
            {
                return new Rule19TableDiscoveryResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule19ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName)
        {
            try
            {
                var columns = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                return new Rule19ColumnSelectionResult
                {
                    Success = true,
                    Columns = columns,
                    AutoQualCodeColumn = FindFirst(columns, ["_001"], ["qual", "code"]),
                    AutoFulfilledColumn = FindFirst(columns, ["_025"], ["fulfill"]),
                    AutoQualTypeColumn = FindFirst(columns, ["_005"], ["type"]),
                    AutoQualNameColumn = FindFirst(columns, ["_003"], ["name", "qualification_name"]),
                    AutoPqmQualNameColumn = FindFirst(columns, ["Authorised_Qualification_Name"], ["authorised", "qualification"]),
                    AutoPqmQualTypeColumn = FindFirst(columns, ["HEQF_Qual_Type"], ["heqf", "qual_type"])
                };
            }
            catch (Exception ex)
            {
                return new Rule19ColumnSelectionResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule19FilterValueResult> GetFilterValuesAsync(int clientId, string studTable, string fulfilledColumn)
        {
            try
            {
                var values = await _datasets.GetDistinctColumnValuesAsync(clientId, studTable, fulfilledColumn, take: 200);
                var options = values
                    .Select(v => new Rule19FilterValueOption { Value = v.Value, Count = (int)v.Count, Label = $"{v.Value} ({v.Count:N0} records)" })
                    .ToList();

                return new Rule19FilterValueResult
                {
                    Success = true,
                    Options = options,
                    DefaultValue = options.FirstOrDefault()?.Value
                };
            }
            catch (Exception ex)
            {
                return new Rule19FilterValueResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule19VerifyResult> VerifyTablesAsync(Rule19VerifyRequest request)
        {
            try
            {
                ValidateRequest(request.StudTable, request.QualTable, request.FulfilledValue);

                var summary = await AnalyseAsync(ToValidationRequest(request));
                return new Rule19VerifyResult
                {
                    Success = true,
                    StudRecordCount = summary.StudRecordCount,
                    QualRecordCount = summary.QualRecordCount,
                    CredRecordCount = summary.CredRecordCount,
                    CrseRecordCount = summary.CrseRecordCount,
                    EligiblePopulationCount = summary.MatchingCount,
                    PreviewRows = CreateBrowserPreview(summary).MatchingRows
                };
            }
            catch (Exception ex)
            {
                return new Rule19VerifyResult { Success = false, Error = ex.Message };
            }
        }

        // Re-runs the same analysis a fresh "Run Validation" would, without the save-run side
        // effect - used by the export path so a download always reflects the current live data
        // rather than whatever was last saved. AnalyseAsync already loads the full, unbounded
        // population (no preview/full distinction exists for this rule).
        public async Task<Rule19ValidationSummary> GetExportSummaryAsync(Rule19ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.QualTable, request.FulfilledValue);
            return await AnalyseAsync(request);
        }

        public async Task<Rule19ValidationSummary> RunValidationAsync(Rule19ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                ValidateRequest(request.StudTable, request.QualTable, request.FulfilledValue);

                var summary = await AnalyseAsync(request);
                if (summary.Success && request.ClientId > 0)
                {
                    try
                    {
                        summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName);
                    }
                    catch (Exception ex)
                    {
                        summary.Warning = $"Validation completed, but the workspace could not be saved automatically: {ex.Message}";
                    }
                }

                ApplyBrowserPreview(summary);
                return summary;
            }
            catch (Exception ex)
            {
                return new Rule19ValidationSummary { Success = false, Error = ex.Message };
            }
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule19WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 19);
            if (row == null) return null;

            var deserializedSummary = DeserializeSummary(row.ResultsJSON);
            if (includeSummary && deserializedSummary != null)
                ApplyBrowserPreview(deserializedSummary);
            var summary = includeSummary ? deserializedSummary : null;

            var workspace = new Rule19WorkspaceStateViewModel
            {
                ClientId = row.ClientId,
                RunId = row.RunId,
                StudTable = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable,
                QualTable = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_QUAL" : row.DeceasedTable,
                QualCodeColumn = string.IsNullOrWhiteSpace(row.StudColumn) ? "_001" : row.StudColumn,
                FulfilledColumn = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "_025" : row.DeceasedColumn,
                FulfilledValue = deserializedSummary?.FulfilledValue ?? "F",
                QualTypeColumn = deserializedSummary?.QualTypeColumn ?? "_005",
                MdTypesText = string.IsNullOrWhiteSpace(deserializedSummary?.MdTypesText)
                    ? string.Join(", ", DefaultMdTypes)
                    : deserializedSummary!.MdTypesText,
                QualNameColumn = deserializedSummary?.QualNameColumn ?? "_003",
                PqmTable = deserializedSummary?.PqmTable ?? "",
                PqmQualNameColumn = deserializedSummary?.PqmQualNameColumn ?? "",
                PqmQualTypeColumn = deserializedSummary?.PqmQualTypeColumn ?? "",
                CredTable = deserializedSummary?.CredTable ?? "",
                CredJoinCol = string.IsNullOrWhiteSpace(deserializedSummary?.CredJoinCol) ? "_001" : deserializedSummary!.CredJoinCol,
                CredCourseCol = string.IsNullOrWhiteSpace(deserializedSummary?.CredCourseCol) ? "_030" : deserializedSummary!.CredCourseCol,
                CrseTable = deserializedSummary?.CrseTable ?? "",
                CrseCourseCol = string.IsNullOrWhiteSpace(deserializedSummary?.CrseCourseCol) ? "_030" : deserializedSummary!.CrseCourseCol,
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

        public async Task<Rule19RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 19);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON) ?? new Rule19ValidationSummary();
            summary.ClientId = row.ClientId;
            if (summary.SavedRunId.GetValueOrDefault() <= 0)
                summary.SavedRunId = runId;

            if (includeFullResults)
            {
                summary.DisplayedCount = summary.MatchingRows.Count;
                summary.IsPreviewOnly = false;
                summary.PreviewLimit = 0;
            }
            else
            {
                ApplyBrowserPreview(summary);
            }

            var review = new Rule19RunReviewViewModel
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

        public async Task<Rule19WorkspaceSaveResult> SaveWorkspaceAsync(Rule19ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0)
                    return new Rule19WorkspaceSaveResult { Success = false, Error = "Run Rule 19 first so the workspace can be saved." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule19WorkspaceSaveResult { Success = false, Error = "The saved workspace could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.QualTable,
                    StudColumn = request.QualCodeColumn,
                    DeceasedColumn = request.FulfilledColumn
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail, includeSummary: false);
                return new Rule19WorkspaceSaveResult
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
                return new Rule19WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task<Rule19WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule19WorkspaceSaveResult { Success = false, Error = "Saved workspace was not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var clearedSignoffs = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail, includeSummary: false);
                return new Rule19WorkspaceSaveResult
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
                return new Rule19WorkspaceSaveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("The reviewer could not be resolved in the system database.");

            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("The selected Rule 19 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var signoffRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(signoffRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can sign off a Rule 19 run.");

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
                ?? throw new InvalidOperationException("The selected Rule 19 run could not be found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            var engagementRole = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!ValidationRunAccessPolicy.CanAssignedUserRemoveSignoff(engagementRole))
                throw new InvalidOperationException("Only the assigned data analyst, manager, or director can remove signoff from this run.");

            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        public async Task<string> GenerateSqlAsync(Rule19ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.QualTable, request.FulfilledValue);

            var cfg = await ResolveColumnConfigAsync(request);
            var studColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(request.ClientId, request.StudTable), StringComparer.OrdinalIgnoreCase);

            var sql = $@"-- HEMIS RULE 19: MASTERS AND PhD STUDENT POPULATION VALIDATION
-- Source: this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Linkage: {request.StudTable}.{cfg.QualCodeColumn} = {request.QualTable}.{cfg.QualCodeColumn}
{(cfg.HasCredCrse ? $"-- CRED  : {request.StudTable}.{cfg.QualCodeColumn} = {request.CredTable}.{cfg.CredJoinCol}" : "")}
{(cfg.HasCredCrse ? $"-- CRSE  : {request.CredTable}.{cfg.CredCourseCol} = {request.CrseTable}.{cfg.CrseCourseCol}" : "")}
-- Filter : {request.StudTable}.{cfg.FulfilledColumn} = '{cfg.FulfilledValue}'
-- Types  : {request.QualTable}.{cfg.QualTypeColumn} IN ({string.Join(", ", cfg.MdTypes)})
-- Logic  : 100% population testing — returns all filtered rows with link checks and PQM validation

{BuildRule19PrepSql("{schema}", request.StudTable, request.QualTable, request.CredTable, request.CrseTable, request.PqmTable, cfg, studColumns)}

-- Full population result
SELECT * FROM rule19_population ORDER BY sample_number;

-- Qualification type breakdown
SELECT ""Qualification_Type"", COUNT(*) AS record_count
FROM rule19_population
GROUP BY ""Qualification_Type""
ORDER BY COUNT(*) DESC, ""Qualification_Type"" ASC;";

            return sql.Trim();
        }

        private async Task<Rule19ValidationSummary> AnalyseAsync(Rule19ValidationRequest request)
        {
            var cfg = await ResolveColumnConfigAsync(request);
            await EnsureRule19IndexesAsync(request.ClientId, request, cfg);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var studRecordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.StudTable}\";");
            var qualRecordCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.QualTable}\";");
            var credRecordCount = cfg.HasCredCrse ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CredTable}\";") : 0;
            var crseRecordCount = cfg.HasCredCrse ? await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{request.CrseTable}\";") : 0;

            var studColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(request.ClientId, request.StudTable), StringComparer.OrdinalIgnoreCase);

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule19PrepSql(schema, request.StudTable, request.QualTable, request.CredTable, request.CrseTable, request.PqmTable, cfg, studColumns);
                await prepCommand.ExecuteNonQueryAsync();
            }

            var (eligibleCount, passCountRaw, failCountRaw) = await GetCountsAsync(connection);

            var breakdown = new List<Rule19BreakdownItemViewModel>();
            await using (var breakdownCommand = connection.CreateCommand())
            {
                breakdownCommand.CommandText = @"
SELECT ""Qualification_Type"", COUNT(*)
FROM rule19_population
GROUP BY ""Qualification_Type""
ORDER BY COUNT(*) DESC, ""Qualification_Type"" ASC;";
                await using var reader = await breakdownCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    breakdown.Add(new Rule19BreakdownItemViewModel
                    {
                        Value = reader.IsDBNull(0) ? "(blank)" : reader.GetString(0),
                        Count = Convert.ToInt32(reader.GetValue(1))
                    });
                }
            }

            var rows = eligibleCount > 0 ? await LoadPopulationRowsAsync(connection) : new List<Rule19ValidationRowRecord>();

            var passCount = cfg.HasPqm ? passCountRaw : eligibleCount;
            var failCount = cfg.HasPqm ? failCountRaw : 0;
            var status = cfg.HasPqm
                ? (passCount == eligibleCount && eligibleCount > 0 ? "PASS" : (eligibleCount > 0 ? "FAIL" : "NO MATCHING DATA"))
                : (eligibleCount > 0 ? "COMPLETE" : "NO MATCHING DATA");
            var exceptionRate = cfg.HasPqm && eligibleCount > 0 ? Math.Round((decimal)failCount / eligibleCount * 100, 2) : 0m;

            var linkageText = cfg.HasCredCrse
                ? $"{request.StudTable}.{cfg.QualCodeColumn} -> {request.QualTable}.{cfg.QualCodeColumn} | {request.StudTable}.{cfg.QualCodeColumn} -> {request.CredTable}.{cfg.CredJoinCol} | {request.CredTable}.{cfg.CredCourseCol} -> {request.CrseTable}.{cfg.CrseCourseCol}"
                : $"{request.StudTable}.{cfg.QualCodeColumn} -> {request.QualTable}.{cfg.QualCodeColumn}";

            return new Rule19ValidationSummary
            {
                Success = true,
                StudRecordCount = studRecordCount,
                QualRecordCount = qualRecordCount,
                CredRecordCount = credRecordCount,
                CrseRecordCount = crseRecordCount,
                TotalValidated = eligibleCount,
                MatchingCount = eligibleCount,
                DisplayedCount = rows.Count,
                PassCount = passCount,
                FailCount = failCount,
                ExceptionRate = exceptionRate,
                Status = status,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable = request.StudTable,
                QualTable = request.QualTable,
                QualCodeColumn = cfg.QualCodeColumn,
                FulfilledColumn = cfg.FulfilledColumn,
                FulfilledValue = cfg.FulfilledValue,
                QualTypeColumn = cfg.QualTypeColumn,
                MdTypesText = string.Join(", ", cfg.MdTypes),
                QualNameColumn = cfg.QualNameColumn,
                MdTypes = cfg.MdTypes,
                PqmTable = cfg.HasPqm ? request.PqmTable : "",
                PqmQualNameColumn = cfg.HasPqm ? cfg.PqmQualNameColumn : "",
                PqmQualTypeColumn = cfg.HasPqm ? cfg.PqmQualTypeColumn : "",
                CredTable = cfg.HasCredCrse ? request.CredTable : "",
                CredJoinCol = cfg.CredJoinCol,
                CredCourseCol = cfg.CredCourseCol,
                CrseTable = cfg.HasCredCrse ? request.CrseTable : "",
                CrseCourseCol = cfg.CrseCourseCol,
                TableLinkageText = linkageText,
                ProcedureSteps = BuildProcedureSteps(request.StudTable, request.QualTable, cfg),
                ShowAllRecords = true,
                ClientId = request.ClientId,
                Breakdown = breakdown,
                MatchingRows = rows
            };
        }

        // Cheap population size check - runs the same server-side prep SQL as a full export but
        // stops at a COUNT(*), no result rows loaded. Mirrors Rule12Service.GetPopulationCountAsync.
        public async Task<int> GetPopulationCountAsync(Rule19ValidationRequest request)
        {
            ValidateRequest(request.StudTable, request.QualTable, request.FulfilledValue);
            var cfg = await ResolveColumnConfigAsync(request);
            await EnsureRule19IndexesAsync(request.ClientId, request, cfg);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var studColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(request.ClientId, request.StudTable), StringComparer.OrdinalIgnoreCase);

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule19PrepSql(schema, request.StudTable, request.QualTable, request.CredTable, request.CrseTable, request.PqmTable, cfg, studColumns);
                await prepCommand.ExecuteNonQueryAsync();
            }

            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM rule19_population;";
            return Convert.ToInt32(await countCommand.ExecuteScalarAsync());
        }

        // Bypasses AnalyseAsync/LoadPopulationRowsAsync entirely - those buffer every row as a
        // full Dictionary<string,string?> before anything can be written out, unbounded even for
        // the interactive Run (same "450k+ row" OOM risk as Rule 12/14/16 before their fix).
        // Reads and writes one row at a time, using the query's own column names directly.
        // Mirrors Rule12Service.StreamCsvExportAsync.
        public async Task StreamCsvExportAsync(Rule19ValidationRequest request, Stream outputStream)
        {
            ValidateRequest(request.StudTable, request.QualTable, request.FulfilledValue);
            var cfg = await ResolveColumnConfigAsync(request);
            await EnsureRule19IndexesAsync(request.ClientId, request, cfg);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var studColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(request.ClientId, request.StudTable), StringComparer.OrdinalIgnoreCase);

            await using (var prepCommand = connection.CreateCommand())
            {
                prepCommand.CommandText = BuildRule19PrepSql(schema, request.StudTable, request.QualTable, request.CredTable, request.CrseTable, request.PqmTable, cfg, studColumns);
                await prepCommand.ExecuteNonQueryAsync();
            }

            await using var writer = new StreamWriter(outputStream, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = false };

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM rule19_population ORDER BY sample_number;";
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

        private async Task<int> SaveValidationRunAsync(Rule19ValidationRequest request, Rule19ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 19);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 19,
                RuleName = "Masters and PhD Student Population Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalValidated,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.QualTable,
                StudColumn = summary.QualCodeColumn,
                DeceasedColumn = summary.FulfilledColumn,
                ExceptionsJSON = ValidationPayloadCodec.Encode("[]"),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        private static Rule19ValidationRequest ToValidationRequest(Rule19VerifyRequest request) =>
            new()
            {
                ClientId = request.ClientId,
                StudTable = request.StudTable,
                QualTable = request.QualTable,
                QualCodeColumn = request.QualCodeColumn,
                FulfilledColumn = request.FulfilledColumn,
                FulfilledValue = request.FulfilledValue,
                QualTypeColumn = request.QualTypeColumn,
                MdTypesText = request.MdTypesText,
                QualNameColumn = request.QualNameColumn,
                PqmTable = request.PqmTable,
                PqmQualNameColumn = request.PqmQualNameColumn,
                PqmQualTypeColumn = request.PqmQualTypeColumn,
                CredTable = request.CredTable,
                CredJoinCol = request.CredJoinCol,
                CredCourseCol = request.CredCourseCol,
                CrseTable = request.CrseTable,
                CrseCourseCol = request.CrseCourseCol,
                ShowAllRecords = true
            };

        private static List<string> BuildProcedureSteps(string studTable, string qualTable, Rule19ColumnConfig cfg)
        {
            var steps = new List<string>
            {
                $"Load 100% of {studTable} and {qualTable}.",
                $"Link {studTable}.{cfg.QualCodeColumn} to {qualTable}.{cfg.QualCodeColumn}."
            };
            if (cfg.HasCredCrse)
            {
                steps.Add($"Link {studTable}.{cfg.QualCodeColumn} to CRED.{cfg.CredJoinCol}.");
                steps.Add($"Link CRED.{cfg.CredCourseCol} to CRSE.{cfg.CrseCourseCol}.");
            }
            steps.Add($"Keep rows where {studTable}.{cfg.FulfilledColumn} = '{cfg.FulfilledValue}'.");
            steps.Add($"Keep rows where {qualTable}.{cfg.QualTypeColumn} is in the configured Masters/Doctoral list.");
            steps.Add("Return the full linked population with link checks and filter checks.");
            steps.Add(!cfg.HasPqm
                ? "No PQM table configured — PQM_Validation_Result defaults to PASS."
                : $"LEFT JOIN PQM: QUAL.{cfg.QualNameColumn} = PQM.{cfg.PqmQualNameColumn} AND QUAL.{cfg.QualTypeColumn} = PQM.{cfg.PqmQualTypeColumn} (PASS if matched).");
            return steps;
        }

        private static List<string> ParseMdTypes(string? text)
        {
            var values = (text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return values.Count > 0 ? values : DefaultMdTypes.ToList();
        }

        // ── Column configuration resolution (degrades optional display columns to NULL when
        //    the uploaded table doesn't have them, instead of hard-failing the whole rule) ────

        private sealed class Rule19ColumnConfig
        {
            public string QualCodeColumn = "_001";
            public string FulfilledColumn = "_025";
            public string FulfilledValue = "F";
            public string QualTypeColumn = "_005";
            public string QualNameColumn = "_003";
            public List<string> MdTypes = DefaultMdTypes.ToList();
            public bool HasCredCrse;
            public string CredJoinCol = "_001";
            public string CredCourseCol = "_030";
            public string CrseCourseCol = "_030";
            public bool HasCrseName;
            public bool HasCrseFoundation;
            public bool HasPqm;
            public string PqmQualNameColumn = "";
            public string PqmQualTypeColumn = "";
        }

        private async Task<Rule19ColumnConfig> ResolveColumnConfigAsync(Rule19ValidationRequest request)
        {
            var cfg = new Rule19ColumnConfig
            {
                QualCodeColumn = string.IsNullOrWhiteSpace(request.QualCodeColumn) ? "_001" : request.QualCodeColumn,
                FulfilledColumn = string.IsNullOrWhiteSpace(request.FulfilledColumn) ? "_025" : request.FulfilledColumn,
                FulfilledValue = string.IsNullOrWhiteSpace(request.FulfilledValue) ? "F" : request.FulfilledValue,
                QualTypeColumn = string.IsNullOrWhiteSpace(request.QualTypeColumn) ? "_005" : request.QualTypeColumn,
                QualNameColumn = string.IsNullOrWhiteSpace(request.QualNameColumn) ? "_003" : request.QualNameColumn,
                MdTypes = ParseMdTypes(request.MdTypesText),
                HasCredCrse = !string.IsNullOrWhiteSpace(request.CredTable) && !string.IsNullOrWhiteSpace(request.CrseTable),
                CredJoinCol = string.IsNullOrWhiteSpace(request.CredJoinCol) ? "_001" : request.CredJoinCol,
                CredCourseCol = string.IsNullOrWhiteSpace(request.CredCourseCol) ? "_030" : request.CredCourseCol,
                CrseCourseCol = string.IsNullOrWhiteSpace(request.CrseCourseCol) ? "_030" : request.CrseCourseCol,
                HasPqm = !string.IsNullOrWhiteSpace(request.PqmTable) && !string.IsNullOrWhiteSpace(request.PqmQualNameColumn) && !string.IsNullOrWhiteSpace(request.PqmQualTypeColumn),
                PqmQualNameColumn = request.PqmQualNameColumn ?? "",
                PqmQualTypeColumn = request.PqmQualTypeColumn ?? ""
            };

            var studColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.StudTable);
            var qualColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.QualTable);
            EnsureHasColumns(request.StudTable, studColumns, cfg.QualCodeColumn, cfg.FulfilledColumn);
            EnsureHasColumns(request.QualTable, qualColumns, cfg.QualCodeColumn, cfg.QualTypeColumn, cfg.QualNameColumn);

            if (cfg.HasCredCrse)
            {
                var credColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.CredTable);
                var crseColumns = new HashSet<string>(await _datasets.GetValidatedColumnsAsync(request.ClientId, request.CrseTable), StringComparer.OrdinalIgnoreCase);
                EnsureHasColumns(request.CredTable, credColumns, cfg.CredJoinCol, cfg.CredCourseCol);
                EnsureHasColumns(request.CrseTable, crseColumns.ToList(), cfg.CrseCourseCol);
                cfg.HasCrseName = crseColumns.Contains("_058");
                cfg.HasCrseFoundation = crseColumns.Contains("_091");
            }

            if (cfg.HasPqm)
            {
                var pqmColumns = await _datasets.GetValidatedColumnsAsync(request.ClientId, request.PqmTable);
                EnsureHasColumns(request.PqmTable, pqmColumns, cfg.PqmQualNameColumn, cfg.PqmQualTypeColumn);
            }

            return cfg;
        }

        private static void EnsureHasColumns(string tableName, IReadOnlyCollection<string> availableColumns, params string[] requiredColumns)
        {
            var missing = requiredColumns.Where(required => !availableColumns.Contains(required, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"Table {tableName} is missing required column(s): {string.Join(", ", missing)}.");
        }

        // Uploaded engagement tables have no indexes beyond their primary key. When CRED/CRSE
        // linkage is configured, CRED can be as large as a CREG-scale table (real engagements
        // have seen 450k+ rows) — building the expression indexes once, up front, keeps the join
        // fast on every run after the first.
        private async Task EnsureRule19IndexesAsync(int clientId, Rule19ValidationRequest request, Rule19ColumnConfig cfg)
        {
            await _datasets.EnsureJoinIndexAsync(clientId, request.StudTable, cfg.QualCodeColumn);
            await _datasets.EnsureJoinIndexAsync(clientId, request.QualTable, cfg.QualCodeColumn);
            if (cfg.HasCredCrse)
            {
                await _datasets.EnsureJoinIndexAsync(clientId, request.CredTable, cfg.CredJoinCol);
                await _datasets.EnsureJoinIndexAsync(clientId, request.CredTable, cfg.CredCourseCol);
                await _datasets.EnsureJoinIndexAsync(clientId, request.CrseTable, cfg.CrseCourseCol);
            }
        }

        // ── SQL builders (Postgres) ─────────────────────────────────────────────────────

        private static string BuildRule19PrepSql(
            string schema, string studTable, string qualTable, string? credTable, string? crseTable, string? pqmTable,
            Rule19ColumnConfig cfg, HashSet<string> studColumns)
        {
            string OptStud(string col) => studColumns.Contains(col) ? $@"CAST(s.""{col}"" AS text)" : "NULL::text";

            var fulfilledVal = EscapeSqlString(cfg.FulfilledValue.ToUpperInvariant());
            var mdTypeList = cfg.MdTypes.Select(t => $"'{EscapeSqlString(t.ToUpperInvariant())}'").ToList();
            var mdTypePredicate = mdTypeList.Count > 0
                ? $@"UPPER(TRIM(CAST(q.""{cfg.QualTypeColumn}"" AS text))) IN ({string.Join(", ", mdTypeList)})"
                : "1=0";

            var credCrseJoin = cfg.HasCredCrse ? $@"
INNER JOIN ""{schema}"".""{credTable}"" cred ON UPPER(TRIM(CAST(s.""{cfg.QualCodeColumn}"" AS text))) = UPPER(TRIM(CAST(cred.""{cfg.CredJoinCol}"" AS text)))
INNER JOIN ""{schema}"".""{crseTable}"" crse ON UPPER(TRIM(CAST(cred.""{cfg.CredCourseCol}"" AS text))) = UPPER(TRIM(CAST(crse.""{cfg.CrseCourseCol}"" AS text)))" : "";

            var pqmJoin = cfg.HasPqm ? $@"
LEFT JOIN ""{schema}"".""{pqmTable}"" pqm
    ON UPPER(TRIM(CAST(q.""{cfg.QualNameColumn}"" AS text))) = UPPER(TRIM(CAST(pqm.""{cfg.PqmQualNameColumn}"" AS text)))
   AND UPPER(TRIM(CAST(q.""{cfg.QualTypeColumn}"" AS text))) = UPPER(TRIM(CAST(pqm.""{cfg.PqmQualTypeColumn}"" AS text)))" : "";

            var credCrseSelect = cfg.HasCredCrse ? $@"
    CAST(cred.""{cfg.CredJoinCol}"" AS text) AS ""CRED_Qualification_Code"",
    CAST(cred.""{cfg.CredCourseCol}"" AS text) AS ""CRED_Course_Code"",
    CAST(crse.""{cfg.CrseCourseCol}"" AS text) AS ""CRSE_Course_Code"",
    {(cfg.HasCrseName ? @"CAST(crse.""_058"" AS text)" : "NULL::text")} AS ""Course_Name"",
    {(cfg.HasCrseFoundation ? @"CAST(crse.""_091"" AS text)" : "NULL::text")} AS ""Foundation_Course_Indicator"","
                : "";

            var pqmSelect = cfg.HasPqm ? $@"
    CAST(pqm.""{cfg.PqmQualNameColumn}"" AS text) AS ""PQM_Qualification_Name"",
    CAST(pqm.""{cfg.PqmQualTypeColumn}"" AS text) AS ""PQM_Qualification_Type"","
                : "";

            var credCrseLinkCheck = cfg.HasCredCrse ? @"
    'MATCH' AS ""STUD_CRED_Link_Check"",
    'MATCH' AS ""CRED_CRSE_Link_Check"","
                : "";

            var pqmValidation = cfg.HasPqm
                ? $@"CASE WHEN pqm.""{cfg.PqmQualNameColumn}"" IS NOT NULL AND TRIM(CAST(pqm.""{cfg.PqmQualNameColumn}"" AS text)) <> '' THEN 'PASS' ELSE 'FAIL' END"
                : "'PASS'";

            return $@"
DROP TABLE IF EXISTS rule19_population;

CREATE TEMP TABLE rule19_population AS
SELECT
    ROW_NUMBER() OVER (ORDER BY s.""{cfg.QualCodeColumn}"") AS sample_number,
    'Control_1' AS ""Control_Type"",
    CAST(s.""{cfg.QualCodeColumn}"" AS text) AS ""Student_Qualification_Code"",
    {OptStud("_007")} AS ""Student_Number"",
    {OptStud("_008")} AS ""South_African_ID_Number"",
    {OptStud("_010")} AS ""Entrance_Category"",
    {OptStud("_011")} AS ""Date_Of_Birth"",
    {OptStud("_012")} AS ""Gender"",
    {OptStud("_013")} AS ""Race"",
    {OptStud("_014")} AS ""Nationality"",
    {OptStud("_019")} AS ""NSFAS_Status"",
    {OptStud("_021")} AS ""Previous_Years_Activity"",
    {OptStud("_024")} AS ""Attendance_Mode"",
    CAST(s.""{cfg.FulfilledColumn}"" AS text) AS ""Qualification_Fulfilled_Indicator"",
    {OptStud("_066")} AS ""Student_Last_Name"",
    {OptStud("_067")} AS ""Student_First_Name"",
    {OptStud("_068")} AS ""Student_Middle_Name"",
    {OptStud("_073")} AS ""Percentage_Research_Curriculum"",
    CAST(q.""{cfg.QualCodeColumn}"" AS text) AS ""QUAL_Qualification_Code"",
    CAST(q.""{cfg.QualNameColumn}"" AS text) AS ""Qualification_Name"",
    CAST(q.""{cfg.QualTypeColumn}"" AS text) AS ""Qualification_Type"",{credCrseSelect}{pqmSelect}
    'MATCH' AS ""STUD_QUAL_Link_Check"",{credCrseLinkCheck}
    'PASS' AS ""Rule19_Filter_Check"",
    {pqmValidation} AS ""PQM_Validation_Result""
FROM ""{schema}"".""{studTable}"" s
INNER JOIN ""{schema}"".""{qualTable}"" q ON UPPER(TRIM(CAST(s.""{cfg.QualCodeColumn}"" AS text))) = UPPER(TRIM(CAST(q.""{cfg.QualCodeColumn}"" AS text))){credCrseJoin}{pqmJoin}
WHERE UPPER(TRIM(CAST(s.""{cfg.FulfilledColumn}"" AS text))) = '{fulfilledVal}'
  AND {mdTypePredicate};

ANALYZE rule19_population;";
        }

        private static async Task<(int Eligible, int Pass, int Fail)> GetCountsAsync(NpgsqlConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    COUNT(*) AS eligible,
    SUM(CASE WHEN ""PQM_Validation_Result"" = 'PASS' THEN 1 ELSE 0 END) AS pass_count,
    SUM(CASE WHEN ""PQM_Validation_Result"" = 'FAIL' THEN 1 ELSE 0 END) AS fail_count
FROM rule19_population;";
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return (GetInt(reader, 0), GetInt(reader, 1), GetInt(reader, 2));
            return (0, 0, 0);
        }

        private async Task<List<Rule19ValidationRowRecord>> LoadPopulationRowsAsync(NpgsqlConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT * FROM rule19_population ORDER BY sample_number;";

            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Rule19ValidationRowRecord>();
            var validationNumber = 0;
            while (await reader.ReadAsync())
            {
                validationNumber++;
                var displayValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    displayValues[reader.GetName(i)] = reader.IsDBNull(i)
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }

                var pqmName = displayValues.TryGetValue("PQM_Qualification_Name", out var pn) ? pn : null;
                var pqmType = displayValues.TryGetValue("PQM_Qualification_Type", out var pt) ? pt : null;

                rows.Add(new Rule19ValidationRowRecord
                {
                    ValidationNumber = validationNumber,
                    QualCodeValue = displayValues.TryGetValue("Student_Qualification_Code", out var qc) ? qc ?? "" : "",
                    FulfilledValue = displayValues.TryGetValue("Qualification_Fulfilled_Indicator", out var fv) ? fv ?? "" : "",
                    QualTypeValue = displayValues.TryGetValue("Qualification_Type", out var qt) ? qt ?? "" : "",
                    ValidationResult = displayValues.TryGetValue("PQM_Validation_Result", out var vr) ? vr ?? "PASS" : "PASS",
                    PqmQualName = string.IsNullOrWhiteSpace(pqmName) ? null : pqmName,
                    PqmQualType = string.IsNullOrWhiteSpace(pqmType) ? null : pqmType,
                    PqmNameMatch = !string.IsNullOrWhiteSpace(pqmName),
                    PqmTypeMatch = !string.IsNullOrWhiteSpace(pqmType),
                    DisplayValues = displayValues
                });
            }

            return rows;
        }

        private static Rule19ValidationSummary CreateBrowserPreview(Rule19ValidationSummary summary)
        {
            var previewRows = summary.MatchingRows.Take(BrowserPreviewRowLimit).ToList();
            var sourceRowCount = Math.Max(summary.MatchingCount, summary.MatchingRows.Count);
            var isPreviewOnly = sourceRowCount > previewRows.Count;
            var previewLimit = isPreviewOnly ? previewRows.Count : 0;

            return new Rule19ValidationSummary
            {
                Success = summary.Success,
                StudRecordCount = summary.StudRecordCount,
                QualRecordCount = summary.QualRecordCount,
                TotalValidated = summary.TotalValidated,
                MatchingCount = summary.MatchingCount,
                DisplayedCount = previewRows.Count,
                IsPreviewOnly = isPreviewOnly,
                PreviewLimit = previewLimit,
                PassCount = summary.PassCount,
                FailCount = summary.FailCount,
                ExceptionRate = summary.ExceptionRate,
                Status = summary.Status,
                Timestamp = summary.Timestamp,
                StudTable = summary.StudTable,
                QualTable = summary.QualTable,
                QualCodeColumn = summary.QualCodeColumn,
                FulfilledColumn = summary.FulfilledColumn,
                FulfilledValue = summary.FulfilledValue,
                QualTypeColumn = summary.QualTypeColumn,
                MdTypesText = summary.MdTypesText,
                QualNameColumn = summary.QualNameColumn,
                MdTypes = summary.MdTypes.ToList(),
                TableLinkageText = summary.TableLinkageText,
                ProcedureSteps = summary.ProcedureSteps.ToList(),
                ShowAllRecords = summary.ShowAllRecords,
                ClientId = summary.ClientId,
                SavedRunId = summary.SavedRunId,
                PqmTable = summary.PqmTable,
                PqmQualNameColumn = summary.PqmQualNameColumn,
                PqmQualTypeColumn = summary.PqmQualTypeColumn,
                CredTable = summary.CredTable,
                CredJoinCol = summary.CredJoinCol,
                CredCourseCol = summary.CredCourseCol,
                CrseTable = summary.CrseTable,
                CrseCourseCol = summary.CrseCourseCol,
                CredRecordCount = summary.CredRecordCount,
                CrseRecordCount = summary.CrseRecordCount,
                Breakdown = summary.Breakdown
                    .Select(item => new Rule19BreakdownItemViewModel { Value = item.Value, Count = item.Count })
                    .ToList(),
                MatchingRows = previewRows
                    .Select(row => new Rule19ValidationRowRecord
                    {
                        ValidationNumber = row.ValidationNumber,
                        QualCodeValue = row.QualCodeValue,
                        FulfilledValue = row.FulfilledValue,
                        QualTypeValue = row.QualTypeValue,
                        ValidationResult = row.ValidationResult,
                        PqmQualName = row.PqmQualName,
                        PqmQualType = row.PqmQualType,
                        PqmNameMatch = row.PqmNameMatch,
                        PqmTypeMatch = row.PqmTypeMatch,
                        DisplayValues = new Dictionary<string, string?>(row.DisplayValues, StringComparer.OrdinalIgnoreCase)
                    })
                    .ToList(),
                Warning = summary.Warning,
                Error = summary.Error
            };
        }

        private static void ApplyBrowserPreview(Rule19ValidationSummary summary)
        {
            var preview = CreateBrowserPreview(summary);
            summary.DisplayedCount = preview.DisplayedCount;
            summary.IsPreviewOnly = preview.IsPreviewOnly;
            summary.PreviewLimit = preview.PreviewLimit;
            summary.MatchingRows = preview.MatchingRows;
        }

        private static Rule19ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule19ValidationSummary>(decoded);
            }
            catch
            {
                return null;
            }
        }

        private static string? FindFirst(IEnumerable<string> values, IEnumerable<string> preferredExact, IEnumerable<string> preferredContains)
        {
            var list = values.ToList();
            foreach (var exact in preferredExact)
            {
                var match = list.FirstOrDefault(value => value.Equals(exact, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }

            foreach (var contains in preferredContains)
            {
                var match = list.FirstOrDefault(value => value.Contains(contains, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }

            return list.FirstOrDefault();
        }

        private static void ValidateRequest(string studTable, string qualTable, string fulfilledValue)
        {
            if (string.IsNullOrWhiteSpace(studTable) || string.IsNullOrWhiteSpace(qualTable))
                throw new InvalidOperationException("Both STUD and QUAL tables are required.");
            if (string.IsNullOrWhiteSpace(fulfilledValue))
                throw new InvalidOperationException("Select a fulfilled filter value.");
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

        private static int GetInt(System.Data.Common.DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

        private static string EscapeSqlString(string? value) => (value ?? "").Replace("'", "''");
    }
}
