using Newtonsoft.Json;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace HemisAudit.Services
{
    // Rule 44: Masters / PhD Research Time Validation — validates against the engagement's own
    // uploaded Supabase data instead of a live SQL Server connection. Selects Masters/Doctoral
    // students via the QUAL._005 PG-type filter, bridges STUD -> QUAL -> PQM, and compares
    // STUD's actual research-time percentage against PQM's expected value. The original SQL-Server
    // design loaded every row with no cap (ApplyBrowserPreview was a no-op) — RowLimit is
    // introduced here from the start, matching the house style established for every rule this
    // session; totals are always exact via an independent COUNT query.
    public class Rule44Service : IRule44Service
    {
        // Storage/Excel population cap — effectively "full population" (well above any realistic
        // institution's record count) while still guarding against a pathological runaway query.
        private const int RowLimit = 200000;
        // The UI (Analysis/Results tabs) only ever renders a small sample so the app stays fast;
        // the full population above is always stored and always available via Excel/CSV download.
        private const int UiSampleLimit = 10;

        private readonly IConfiguration _configuration;
        private readonly IEngagementDatasetService _datasets;
        private readonly ISystemDatabaseService _systemDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public Rule44Service(
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

        // ── Discovery ─────────────────────────────────────────────────────────

        public async Task<Rule44TableDiscoveryResult> GetTablesAsync(int clientId)
        {
            try
            {
                var tables = await _datasets.ListTableNamesAsync(clientId);
                if (tables.Count == 0)
                {
                    return new Rule44TableDiscoveryResult
                    {
                        Success = false,
                        Error = "No tables have been uploaded for this engagement yet. Upload data under Datasets first."
                    };
                }

                return new Rule44TableDiscoveryResult
                {
                    Success       = true,
                    Tables        = tables,
                    AutoStudTable = FindFirst(tables, ["dbo_STUD", "STUD"], ["stud"]),
                    AutoQualTable = FindFirst(tables, ["dbo_QUAL", "QUAL"], ["qual"]),
                    AutoPqmTable  = FindFirst(tables, ["PQM"], ["pqm"])
                };
            }
            catch (Exception ex) { return new Rule44TableDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule44ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole)
        {
            try
            {
                var cols = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
                string? auto = tableRole?.ToLowerInvariant() switch
                {
                    "stud_studentno"    => FindFirst(cols, ["_007"], []),
                    "stud_id"           => FindFirst(cols, ["_008"], []),
                    "stud_qualcode"     => FindFirst(cols, ["_001"], []),
                    "stud_status"       => FindFirst(cols, ["_010"], []),
                    "stud_researchtime" => FindFirst(cols, ["_073"], []),
                    "qual_qualcode"     => FindFirst(cols, ["_001"], []),
                    "qual_name"         => FindFirst(cols, ["_003"], []),
                    "qual_type"         => FindFirst(cols, ["_005"], []),
                    "pqm_name"          => FindFirst(cols, ["Authorised_Qualification_Name"], ["Qualification_Name", "Authorised"]),
                    "pqm_researchtime"  => FindFirst(cols, ["Research_1"], ["Research"]),
                    _ => null
                };
                return new Rule44ColumnDiscoveryResult { Success = true, Columns = cols, AutoSelected = auto };
            }
            catch (Exception ex) { return new Rule44ColumnDiscoveryResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule44VerifyResult> VerifyTablesAsync(Rule44VerifyRequest request)
        {
            try
            {
                NormalizeRequest(request.StudTable, request.QualTable, request.PqmTable, request.ColumnMapping, out var st, out var qt, out var pt, out var m);
                var pgList = ParsePgTypes(request.PgTypesText);

                await ValidateColumnsExistAsync(request.ClientId, st, [m.StudQualCodeCol]);
                await ValidateColumnsExistAsync(request.ClientId, qt, [m.QualQualCodeCol, m.QualTypeCol]);

                var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
                await using var connection = conn;

                var pgSql = BuildPgInList(pgList);
                var countSql = $@"
SELECT COUNT(*) FROM ""{schema}"".""{Sanitise(st)}"" S
INNER JOIN ""{schema}"".""{Sanitise(qt)}"" Q ON UPPER(TRIM(CAST(S.""{m.StudQualCodeCol}"" AS text))) = UPPER(TRIM(CAST(Q.""{m.QualQualCodeCol}"" AS text)))
WHERE {(string.IsNullOrWhiteSpace(pgSql) ? "1=0" : $"TRIM(CAST(Q.\"{m.QualTypeCol}\" AS text)) IN ({pgSql})")};";

                var mastersDoctCount = await CountAsync(connection, countSql);
                var pqmCount = await CountAsync(connection, $"SELECT COUNT(*) FROM \"{schema}\".\"{Sanitise(pt)}\";");

                return new Rule44VerifyResult { Success = true, MastersDoctCount = mastersDoctCount, PqmCount = pqmCount };
            }
            catch (Exception ex) { return new Rule44VerifyResult { Success = false, Error = ex.Message }; }
        }

        // ── Validation ────────────────────────────────────────────────────────

        public async Task<Rule44ValidationSummary> RunValidationAsync(Rule44ValidationRequest request, string? userEmail = null, string? userName = null)
        {
            try
            {
                NormalizeRequest(request.StudTable, request.QualTable, request.PqmTable, request.ColumnMapping, out var st, out var qt, out var pt, out var m);
                request.StudTable = st; request.QualTable = qt; request.PqmTable = pt;

                var summary = await AnalyseAsync(request, m);

                if (summary.Success && request.ClientId > 0)
                {
                    try { summary.SavedRunId = await SaveValidationRunAsync(request, summary, userEmail, userName); }
                    catch (Exception ex)
                    {
                        summary.Success = false;
                        summary.Error   = $"Analysis completed, but the run could not be saved: {ex.Message}";
                        return summary;
                    }
                }

                return ApplyUiSample(summary);
            }
            catch (Exception ex) { return new Rule44ValidationSummary { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule44ValidationSummary?> GetStoredSummaryAsync(int runId)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 44);
            if (row == null) return null;
            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary.SavedRunId = runId;
            return summary;
        }

        private async Task<Rule44ValidationSummary> AnalyseAsync(Rule44ValidationRequest request, Rule44ColumnMapping m)
        {
            var pgList = ParsePgTypes(request.PgTypesText);
            if (pgList.Count == 0)
                return new Rule44ValidationSummary { Success = false, Error = "No valid PG type codes were specified." };

            var pgSql = BuildPgInList(pgList);

            await ValidateColumnsExistAsync(request.ClientId, request.StudTable, [m.StudStudentNoCol, m.StudIdCol, m.StudQualCodeCol, m.StudStatusCol, m.StudResearchTimeCol]);
            await ValidateColumnsExistAsync(request.ClientId, request.QualTable, [m.QualQualCodeCol, m.QualNameCol, m.QualTypeCol]);
            await ValidateColumnsExistAsync(request.ClientId, request.PqmTable,  [m.PqmNameCol, m.PqmResearchTimeCol]);

            var (conn, schema) = await OpenEngagementConnectionAsync(request.ClientId);
            await using var connection = conn;

            var (bodySql, orderSql) = BuildValidationSqlParts(schema, request.StudTable, request.QualTable, request.PqmTable, m, pgSql);

            var countSql = $@"
WITH validation AS ({bodySql})
SELECT
    COUNT(*) AS total,
    COUNT(*) FILTER (WHERE validation_result = 'PASS') AS pass_count,
    COUNT(*) FILTER (WHERE validation_result = 'FAIL') AS fail_count,
    COUNT(*) FILTER (WHERE validation_result = 'MISSING_PQM') AS missing_count
FROM validation;";

            int totalCount, passCount, failCount, missingCount;
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = countSql;
                await using var reader = await countCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    totalCount = Convert.ToInt32(reader.GetValue(0));
                    passCount = Convert.ToInt32(reader.GetValue(1));
                    failCount = Convert.ToInt32(reader.GetValue(2));
                    missingCount = Convert.ToInt32(reader.GetValue(3));
                }
                else { totalCount = passCount = failCount = missingCount = 0; }
            }

            var rowsSql = $@"
WITH validation AS ({bodySql})
SELECT * FROM validation
{orderSql}
LIMIT @limit;";

            var passRows = new List<Rule44ReviewRow>();
            var failRows = new List<Rule44ReviewRow>();
            int rowNo = 0;

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = rowsSql;
                cmd.Parameters.AddWithValue("limit", RowLimit);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rowNo++;
                    var result = GetString(reader, "validation_result") ?? "";
                    var row = new Rule44ReviewRow
                    {
                        RowNumber             = rowNo,
                        StudentNo             = GetString(reader, "student_no") ?? "",
                        StudentId             = GetString(reader, "student_id") ?? "",
                        QualCode              = GetString(reader, "qual_code") ?? "",
                        StudStatus            = GetString(reader, "stud_status") ?? "",
                        QualName              = GetString(reader, "qual_name") ?? "",
                        QualType              = GetString(reader, "qual_type") ?? "",
                        StudResearchTime      = GetString(reader, "stud_research_time") ?? "",
                        PqmResearchTime       = GetString(reader, "pqm_research_time") ?? "",
                        ValidationResult      = result,
                        ValidationExplanation = GetString(reader, "validation_explanation") ?? ""
                    };

                    if (result == "PASS") passRows.Add(row);
                    else failRows.Add(row);
                }
            }

            var excRate = totalCount == 0 ? 0m : Math.Round((decimal)(totalCount - passCount) / totalCount * 100m, 2);
            var loadedTotal = passRows.Count + failRows.Count;

            return new Rule44ValidationSummary
            {
                Success          = true,
                Timestamp        = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StudTable        = request.StudTable,
                QualTable        = request.QualTable,
                PqmTable         = request.PqmTable,
                PgTypesText      = request.PgTypesText,
                ColumnMapping    = m,
                ClientId         = request.ClientId,
                TotalCount       = totalCount,
                PassCount        = passCount,
                FailCount        = failCount,
                MissingPqmCount  = missingCount,
                ExceptionRate    = excRate,
                Status           = (failCount + missingCount == 0) ? "PASS" : "FAIL",
                PassRows         = passRows,
                FailRows         = failRows,
                Warning          = totalCount > loadedTotal
                    ? $"{totalCount:N0} rows were found; only the first {loadedTotal:N0} are stored to keep the app responsive. All totals above are exact."
                    : null
            };
        }

        // The service always computes and stores the full tested population (up to RowLimit).
        // The UI only ever needs a small sample to stay fast — this returns a shallow copy with
        // PassRows/FailRows truncated to UiSampleLimit each; the full set remains in ResultsJSON
        // for Excel/CSV export via GetStoredSummaryAsync.
        private static Rule44ValidationSummary ApplyUiSample(Rule44ValidationSummary full)
        {
            if (full.PassRows.Count <= UiSampleLimit && full.FailRows.Count <= UiSampleLimit)
                return full;

            return new Rule44ValidationSummary
            {
                Success = full.Success, Error = full.Error, SavedRunId = full.SavedRunId, ClientId = full.ClientId,
                Timestamp = full.Timestamp, StudTable = full.StudTable, QualTable = full.QualTable, PqmTable = full.PqmTable,
                PgTypesText = full.PgTypesText, ColumnMapping = full.ColumnMapping,
                TotalCount = full.TotalCount, PassCount = full.PassCount, FailCount = full.FailCount,
                MissingPqmCount = full.MissingPqmCount, ExceptionRate = full.ExceptionRate, Status = full.Status,
                PassRows = full.PassRows.Take(UiSampleLimit).ToList(),
                FailRows = full.FailRows.Take(UiSampleLimit).ToList(),
                Warning = "UI results show only the first 10 sample rows. Download the results to access the full tested population."
            };
        }

        private static (string BodySql, string OrderSql) BuildValidationSqlParts(string schema, string studTable, string qualTable, string pqmTable, Rule44ColumnMapping m, string pgSql)
        {
            var st = Sanitise(studTable);
            var qt = Sanitise(qualTable);
            var pt = Sanitise(pqmTable);

            var body = $@"
    SELECT
        F.student_no, F.student_id, F.qual_code, F.stud_status, F.qual_name, F.qual_type, F.stud_research_time,
        COALESCE(TRIM(CAST(P.""{m.PqmResearchTimeCol}"" AS text)), '') AS pqm_research_time,
        CASE
            WHEN P.""{m.PqmNameCol}"" IS NULL THEN 'MISSING_PQM'
            WHEN F.stud_research_time = TRIM(CAST(P.""{m.PqmResearchTimeCol}"" AS text)) THEN 'PASS'
            ELSE 'FAIL'
        END AS validation_result,
        CASE
            WHEN P.""{m.PqmNameCol}"" IS NULL
                THEN 'No PQM record found for qualification: ' || COALESCE(F.qual_name, '')
            WHEN F.stud_research_time = TRIM(CAST(P.""{m.PqmResearchTimeCol}"" AS text))
                THEN 'PASS: STUD._073 (' || F.stud_research_time || ') agrees with PQM.{m.PqmResearchTimeCol} (' || COALESCE(TRIM(CAST(P.""{m.PqmResearchTimeCol}"" AS text)), '') || ')'
            ELSE 'FAIL: STUD._073 (' || F.stud_research_time || ') disagrees with PQM.{m.PqmResearchTimeCol} (' || COALESCE(TRIM(CAST(P.""{m.PqmResearchTimeCol}"" AS text)), '') || ')'
        END AS validation_explanation
    FROM (
        SELECT
            TRIM(CAST(S.""{m.StudStudentNoCol}""    AS text)) AS student_no,
            TRIM(CAST(S.""{m.StudIdCol}""           AS text)) AS student_id,
            TRIM(CAST(S.""{m.StudQualCodeCol}""     AS text)) AS qual_code,
            TRIM(CAST(S.""{m.StudStatusCol}""       AS text)) AS stud_status,
            TRIM(CAST(S.""{m.StudResearchTimeCol}"" AS text)) AS stud_research_time,
            TRIM(CAST(Q.""{m.QualQualCodeCol}""     AS text)) AS qual_qual_code,
            TRIM(CAST(Q.""{m.QualNameCol}""         AS text)) AS qual_name,
            TRIM(CAST(Q.""{m.QualTypeCol}""         AS text)) AS qual_type
        FROM ""{schema}"".""{st}"" S
        INNER JOIN ""{schema}"".""{qt}"" Q
            ON UPPER(TRIM(CAST(S.""{m.StudQualCodeCol}"" AS text))) = UPPER(TRIM(CAST(Q.""{m.QualQualCodeCol}"" AS text)))
        WHERE {(string.IsNullOrWhiteSpace(pgSql) ? "1=0" : $"TRIM(CAST(Q.\"{m.QualTypeCol}\" AS text)) IN ({pgSql})")}
    ) F
    LEFT JOIN ""{schema}"".""{pt}"" P
        ON UPPER(REGEXP_REPLACE(F.qual_name, '\s+', ' ', 'g')) = UPPER(REGEXP_REPLACE(TRIM(CAST(P.""{m.PqmNameCol}"" AS text)), '\s+', ' ', 'g'))";

            var order = @"
ORDER BY
    CASE WHEN validation_result = 'MISSING_PQM' THEN 0
         WHEN validation_result = 'FAIL' THEN 1
         ELSE 2 END,
    student_no";

            return (body, order);
        }

        // ── SQL generation ────────────────────────────────────────────────────

        public string GenerateSql(Rule44ValidationRequest request)
        {
            NormalizeRequest(request.StudTable, request.QualTable, request.PqmTable, request.ColumnMapping, out var st, out var qt, out var pt, out var m);
            var pgList = ParsePgTypes(request.PgTypesText);
            var pgSql  = BuildPgInList(pgList);
            var (bodySql, orderSql) = BuildValidationSqlParts("{schema}", st, qt, pt, m, pgSql);

            return $@"-- ============================================================
-- HEMIS RULE 44 - Masters / PhD Research Time Validation
-- Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
-- Source    : this engagement's own uploaded tables (schema engagement_{{ClientId}}), not a live SQL Server.
-- Tables    : ""{Sanitise(st)}"" STUD  |  ""{Sanitise(qt)}"" QUAL  |  ""{Sanitise(pt)}"" PQM
-- Display   : STUD.{m.StudStudentNoCol}, STUD.{m.StudIdCol}, STUD.{m.StudQualCodeCol}, STUD.{m.StudStatusCol}, STUD.{m.StudResearchTimeCol}
-- Filter    : QUAL.{m.QualTypeCol} IN ({string.Join(", ", pgList)})
-- Compare   : STUD.{m.StudResearchTimeCol}  vs  PQM.{m.PqmResearchTimeCol}
-- ============================================================
WITH validation AS ({bodySql})
SELECT * FROM validation
{orderSql};".Trim();
        }

        // ── Save / Load ───────────────────────────────────────────────────────

        private async Task<int> SaveValidationRunAsync(Rule44ValidationRequest request, Rule44ValidationSummary summary, string? userEmail, string? userName)
        {
            await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
            await _systemDb.MarkPreviousRuleRunsHistoricalAsync(request.ClientId, 44);

            var runId = await _systemDb.SaveValidationRunAsync(new SaveValidationRunRequest
            {
                ClientId = request.ClientId,
                RuleNumber = 44,
                RuleName = "Masters/PhD Research Time Validation",
                Status = summary.Status,
                TotalRecords = summary.TotalCount,
                PassCount = summary.PassCount,
                FailCount = summary.TotalCount - summary.PassCount,
                ExceptionRate = summary.ExceptionRate,
                StudTable = request.StudTable,
                DeceasedTable = request.QualTable,
                StudColumn = request.PqmTable,
                DeceasedColumn = request.PgTypesText,
                ExceptionsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary.FailRows)),
                ResultsJSON = ValidationPayloadCodec.Encode(JsonConvert.SerializeObject(summary))
            }, userEmail, userName);

            return runId;
        }

        public async Task<int?> GetClientIdForRunAsync(int runId) => await _systemDb.GetClientIdForRunAsync(runId);

        public async Task<Rule44WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetCurrentRuleRunAsync(clientId, 44);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary != null) summary = ApplyUiSample(summary);

            var workspace = new Rule44WorkspaceStateViewModel
            {
                ClientId      = row.ClientId,
                RunId         = row.RunId,
                StudTable     = string.IsNullOrWhiteSpace(row.StudTable) ? "dbo_STUD" : row.StudTable,
                QualTable     = string.IsNullOrWhiteSpace(row.DeceasedTable) ? "dbo_QUAL" : row.DeceasedTable,
                PqmTable      = string.IsNullOrWhiteSpace(row.StudColumn) ? "PQM" : row.StudColumn,
                PgTypesText   = string.IsNullOrWhiteSpace(row.DeceasedColumn) ? "07, 27, 28, 49, 72, 73, 08, 30, 50, 74, 75" : row.DeceasedColumn,
                CurrentStatus = row.Status,
                LastEditedByUserName = row.LastEditedByUserName,
                LastEditedAt  = row.LastEditedAt,
                Summary       = summary,
                ColumnMapping = summary?.ColumnMapping ?? new Rule44ColumnMapping()
            };

            if (summary != null) workspace.CurrentStatus = summary.Status;

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            workspace.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(clientId, currentUser.Id) ?? ""
                : "";

            var signoffs = await _systemDb.GetRuleRunSignoffsAsync(workspace.RunId!.Value, currentUser?.Id);
            workspace.HasDataAnalystSignoff = signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            var mySignoff = signoffs.FirstOrDefault(s => ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, workspace.CurrentUserEngagementRole));
            workspace.CurrentUserHasSignedOff   = mySignoff != null;
            workspace.CurrentUserSignoffComment = mySignoff?.Comment ?? "";
            workspace.IsWorkspaceSaved          = await _systemDb.IsWorkspaceSavedAsync(workspace.RunId!.Value);

            if (workspace.Summary != null) workspace.Summary.SavedRunId = workspace.RunId;
            return workspace;
        }

        public async Task<Rule44RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null)
        {
            var row = await _systemDb.GetRuleRunByIdAsync(runId, 44);
            if (row == null) return null;

            var summary = DeserializeSummary(row.ResultsJSON);
            if (summary == null) return null;
            summary = ApplyUiSample(summary);

            var review = new Rule44RunReviewViewModel
            {
                RunId          = row.RunId,
                ClientId       = row.ClientId,
                IsCurrentRun   = row.IsCurrent,
                EngagementName = row.EngagementName,
                MaconomyNumber = row.MaconomyNumber,
                Summary        = summary
            };

            summary.SavedRunId = review.RunId;

            var currentUser = string.IsNullOrWhiteSpace(currentUserEmail) ? null : await _userManager.FindByEmailAsync(currentUserEmail);
            review.CurrentUserEngagementRole = currentUser != null
                ? await _systemDb.GetRawEngagementRoleAsync(row.ClientId, currentUser.Id) ?? ""
                : "";

            var signoffs = await _systemDb.GetRuleRunSignoffsAsync(runId, currentUser?.Id);
            review.Signoffs              = signoffs;
            review.HasDataAnalystSignoff = signoffs.Any(s => string.Equals(s.SignoffRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));
            review.GeneratedSql          = GenerateSql(new Rule44ValidationRequest
            {
                ClientId    = row.ClientId,
                StudTable   = summary.StudTable,
                QualTable   = summary.QualTable,
                PqmTable    = summary.PqmTable,
                PgTypesText = summary.PgTypesText,
                ColumnMapping = summary.ColumnMapping
            });
            return review;
        }

        public async Task<Rule44WorkspaceSaveResult> SaveWorkspaceAsync(Rule44ValidationRequest request, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                if (!request.RunId.HasValue || request.RunId.Value <= 0)
                    return new Rule44WorkspaceSaveResult { Success = false, Error = "No saved run exists. Run Rule 44 first." };

                var clientId = await _systemDb.GetClientIdForRunAsync(request.RunId.Value);
                if (!clientId.HasValue || clientId.Value != request.ClientId)
                    return new Rule44WorkspaceSaveResult { Success = false, Error = "The saved run could not be found for this engagement." };

                await _systemDb.EnsureClientNotArchivedAsync(request.ClientId);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(request.RunId.Value);

                await _systemDb.SaveRuleWorkspaceFieldsAsync(new SaveRuleWorkspaceFieldsRequest
                {
                    RunId = request.RunId.Value,
                    ClientId = request.ClientId,
                    StudTable = request.StudTable,
                    DeceasedTable = request.QualTable,
                    StudColumn = request.PqmTable,
                    DeceasedColumn = request.PgTypesText
                }, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(request.ClientId, reviewerEmail);
                return new Rule44WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace saved. {cleared} signoff(s) were cleared." : "Workspace saved.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule44WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task<Rule44WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null)
        {
            try
            {
                var clientId = await _systemDb.GetClientIdForRunAsync(runId);
                if (!clientId.HasValue)
                    return new Rule44WorkspaceSaveResult { Success = false, Error = "Saved run not found." };

                await _systemDb.EnsureClientNotArchivedAsync(clientId.Value);
                var cleared = await _systemDb.ClearRuleSignoffsAndFlagForReviewAsync(runId);
                await _systemDb.MarkRuleWorkspaceEditStartedAsync(runId, reviewerName ?? reviewerEmail);

                var workspace = await GetCurrentWorkspaceStateAsync(clientId.Value, reviewerEmail);
                return new Rule44WorkspaceSaveResult
                {
                    Success             = true,
                    Message             = cleared > 0 ? $"Workspace unlocked for editing. {cleared} signoff(s) cleared." : "Workspace unlocked for editing.",
                    SignoffsCleared     = cleared > 0,
                    ClearedSignoffCount = cleared,
                    Workspace           = workspace
                };
            }
            catch (Exception ex) { return new Rule44WorkspaceSaveResult { Success = false, Error = ex.Message }; }
        }

        public async Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Your account could not be resolved in the system database.");
            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Validation run not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);

            if (!await _systemDb.RuleWorkspaceReadyForSignoffAsync(runId))
                throw new InvalidOperationException("The data analyst must save the workspace before signoff is available.");

            var role = await _systemDb.GetRawEngagementRoleAsync(clientId, reviewer.Id);
            if (!CanSignOffAsRole(role))
                throw new InvalidOperationException("Only assigned data analysts, managers, and directors can sign off.");

            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) &&
                !await _systemDb.HasRuleSignoffRoleAsync(runId, "DataAnalyst"))
                throw new InvalidOperationException("The assigned data analyst must sign off first.");

            await _systemDb.AddOrUpdateRuleSignoffAsync(runId, clientId, reviewer.Id, role!, comment);
        }

        public async Task RemoveSignoffAsync(int runId, string reviewerEmail)
        {
            var reviewer = await _userManager.FindByEmailAsync(reviewerEmail)
                ?? throw new InvalidOperationException("Your account could not be resolved.");
            var clientId = await _systemDb.GetClientIdForRunAsync(runId)
                ?? throw new InvalidOperationException("Validation run not found.");

            await _systemDb.EnsureClientNotArchivedAsync(clientId);
            await _systemDb.RemoveRuleSignoffByReviewerAsync(runId, reviewer.Id);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Rule44ValidationSummary? DeserializeSummary(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var decoded = ValidationPayloadCodec.Decode(json);
                return JsonConvert.DeserializeObject<Rule44ValidationSummary>(decoded);
            }
            catch { return null; }
        }

        private static void NormalizeRequest(
            string studTableIn, string qualTableIn, string pqmTableIn, Rule44ColumnMapping? mappingIn,
            out string studTable, out string qualTable, out string pqmTable, out Rule44ColumnMapping m)
        {
            studTable = (studTableIn ?? "dbo_STUD").Trim();
            qualTable = (qualTableIn ?? "dbo_QUAL").Trim();
            pqmTable  = (pqmTableIn  ?? "PQM").Trim();
            m         = mappingIn ?? new Rule44ColumnMapping();
            m.StudStudentNoCol    = ColOrDef(m.StudStudentNoCol,    "_007");
            m.StudIdCol           = ColOrDef(m.StudIdCol,           "_008");
            m.StudQualCodeCol     = ColOrDef(m.StudQualCodeCol,     "_001");
            m.StudStatusCol       = ColOrDef(m.StudStatusCol,       "_010");
            m.StudResearchTimeCol = ColOrDef(m.StudResearchTimeCol, "_073");
            m.QualQualCodeCol     = ColOrDef(m.QualQualCodeCol,     "_001");
            m.QualNameCol         = ColOrDef(m.QualNameCol,         "_003");
            m.QualTypeCol         = ColOrDef(m.QualTypeCol,         "_005");
            m.PqmNameCol          = ColOrDef(m.PqmNameCol,         "Authorised_Qualification_Name");
            m.PqmResearchTimeCol  = ColOrDef(m.PqmResearchTimeCol, "Research_1");
        }

        private static string ColOrDef(string? v, string def) => string.IsNullOrWhiteSpace(v) ? def : v.Trim();

        private static List<string> ParsePgTypes(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                       .Select(t => t.Trim())
                       .Where(t => t.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        private static string BuildPgInList(IEnumerable<string> types)
            => string.Join(", ", types.Select(t => $"'{t.Replace("'", "''")}'"));

        // Returns the actual matched value's real casing (not the literal searched term) — Postgres
        // double-quoted identifiers are case-sensitive, so the auto-selected column name must match
        // the physical column exactly, not just case-insensitively.
        private static string? FindFirst(List<string> values, string[] exactMatches, string[] partials)
        {
            foreach (var e in exactMatches)
            {
                var m = values.FirstOrDefault(v => string.Equals(v, e, StringComparison.OrdinalIgnoreCase));
                if (m != null) return m;
            }
            foreach (var p in partials)
            {
                var m = values.FirstOrDefault(v => v.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
                if (m != null) return m;
            }
            return null;
        }

        private static string Sanitise(string name) => name.Replace("\"", "").Replace("'", "").Replace(";", "").Trim();

        // Column names come from defaults or a previously-saved workspace and may not match this
        // engagement's actual uploaded table — check before querying so a mismatch surfaces as a
        // clear message instead of a raw Postgres "column does not exist" error.
        private async Task ValidateColumnsExistAsync(int clientId, string tableName, IEnumerable<string> requiredColumns)
        {
            var actual  = await _datasets.GetValidatedColumnsAsync(clientId, tableName);
            var missing = requiredColumns.Where(c => !actual.Contains(c, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Column(s) {string.Join(", ", missing.Select(m => $"\"{m}\""))} were not found in table \"{tableName}\". " +
                    "Update the column mapping to match your uploaded data, then run again.");
        }

        private static string? GetString(System.Data.Common.DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            if (reader.IsDBNull(ordinal)) return null;
            var value = Convert.ToString(reader.GetValue(ordinal));
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static async Task<int> CountAsync(NpgsqlConnection conn, string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private static bool CanSignOffAsRole(string? role) =>
            string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Manager",     StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director",    StringComparison.OrdinalIgnoreCase);

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
