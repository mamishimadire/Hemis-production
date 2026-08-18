using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.Services;
using HemisAudit.ViewModels;

namespace HemisAudit.Controllers
{
    [Authorize]
    public class Rule64Controller : Controller
    {
        private readonly IRule64Service _rule64;
        private readonly IAuditLogService _audit;
        private readonly UserManager<ApplicationUser> _users;
        private readonly ISystemDatabaseService _systemDb;
        private readonly IExportService _export;
        private readonly ILogger<Rule64Controller> _logger;

        public Rule64Controller(
            IRule64Service rule64,
            IAuditLogService audit,
            UserManager<ApplicationUser> users,
            ISystemDatabaseService systemDb,
            IExportService export,
            ILogger<Rule64Controller> logger)
        {
            _rule64 = rule64;
            _audit = audit;
            _users = users;
            _systemDb = systemDb;
            _export = export;
            _logger = logger;
        }

        public async Task<IActionResult> Index(int clientId = 0)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            await _systemDb.NormalizeCompletedRunStatusesAsync();

            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(role, "Trainee", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only assigned engagement members can open audit modules.";
                return RedirectToAction("Index", "Dashboard");
            }

            var clients = await _systemDb.GetClientsAsync(user, role, approvedOnly: true);
            if (clientId > 0 && !await _systemDb.CanAccessClientResultsAsync(clientId, user, role))
            {
                TempData["Error"] = "You cannot access this engagement.";
                return RedirectToAction("Index", "Dashboard");
            }

            ViewBag.Clients = clients
                .Select(c => new Client
                {
                    Id = c.Id,
                    Name = c.EngagementName,
                    FiscalYear = c.MaconomyNumber,
                    Status = c.Status,
                    CreatedAt = c.CreatedAt,
                    CreatedByUserId = "",
                    IsActive = true
                })
                .ToList();
            ViewBag.ClientId = clientId;
            ViewBag.CurrentSystemRole = role;
            ViewBag.ModuleNavigation = ModuleSequenceNavigationHelper.BuildForWorkspace(64, clientId);
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetWorkspaceState(int clientId)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            await _systemDb.NormalizeCompletedRunStatusesAsync();

            if (clientId <= 0)
                return Json(new { success = true, hasWorkspace = false });

            if (!await _systemDb.CanAccessClientResultsAsync(clientId, user, role))
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                return Json(new { success = false, error = "You cannot access this engagement." });
            }

            var workspace = await _rule64.GetCurrentWorkspaceStateAsync(clientId, user?.Email);
            var resultsVisible = CanViewResults(role, workspace);
            if (workspace != null)
                workspace.ResultsVisible = resultsVisible;
            if (workspace != null && !resultsVisible)
                workspace.Summary = null;

            return Json(new { success = true, hasWorkspace = workspace != null, resultsVisible, workspace });
        }

        [HttpGet]
        public async Task<IActionResult> Run(int id)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            await _systemDb.NormalizeCompletedRunStatusesAsync();

            var review = await _rule64.GetSavedRunAsync(id, user?.Email);
            if (review == null)
                return NotFound();

            if (!await _systemDb.CanAccessClientResultsAsync(review.ClientId, user, role))
            {
                TempData["Error"] = "You do not have access to this saved validation run.";
                return RedirectToAction("Index", "Dashboard");
            }

            if (!ValidationRunAccessPolicy.CanViewSignedResults(role, review.CurrentUserEngagementRole, review.HasDataAnalystSignoff))
            {
                TempData["Error"] = "Only analyst-signed validation results are available for review.";
                return RedirectToAction("Index", "Dashboard");
            }

            var clientDetail = await _systemDb.GetClientDetailAsync(review.ClientId, user, role);
            var isArchived = clientDetail?.IsArchived == true;
            ViewBag.IsArchived = isArchived;
            ViewBag.CanManageEngagement =
                string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase);
            ViewBag.ModuleNavigation = ModuleSequenceNavigationHelper.BuildForSavedRun(
                64, review.ClientId, clientDetail?.ValidationRuns, role, review.CurrentUserEngagementRole);
            ViewBag.CanOpenWorkspace =
                !isArchived &&
                await _systemDb.CanAccessClientModuleAsync(review.ClientId, user, role) &&
                (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(review.CurrentUserEngagementRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));

            review.GeneratedSql = _rule64.GenerateSql(new Rule64ValidationRequest
            {
                ClientId = review.ClientId,
                StudTable = review.Summary.StudTable,
                CregTable = review.Summary.CregTable,
                ProdTable = review.Summary.ProdTable,
                ColumnMapping = review.Summary.ColumnMapping
            });

            return View(review);
        }

        [HttpPost]
        public async Task<IActionResult> GetTables([FromBody] EngagementTableListRequest model) =>
            Json(await RequireDataAnalystAsync(async () => await _rule64.GetTablesAsync(model.ClientId)));

        [HttpPost]
        public async Task<IActionResult> GetColumns([FromBody] Rule64GetColumnsRequest request) =>
            Json(await RequireDataAnalystAsync(async () => await _rule64.GetColumnsAsync(request.ClientId, request.TableName, request.TableRole)));

        [HttpPost]
        public async Task<IActionResult> VerifyTables([FromBody] Rule64ValidationRequest request) =>
            Json(await RequireDataAnalystAsync(async () => await _rule64.VerifyTablesAsync(request)));

        [HttpPost]
        public async Task<IActionResult> RunValidation([FromBody] Rule64ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (request.ClientId <= 0)
                return Json(new Rule64ValidationSummary { Success = false, Error = "Select an approved engagement before running validation." });

            if (!await _systemDb.CanAccessClientResultsAsync(request.ClientId, user, role))
                return Json(new Rule64ValidationSummary { Success = false, Error = "You cannot access this engagement." });

            var engagementRole = await _systemDb.GetEngagementRoleAsync(request.ClientId, user, role);
            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(engagementRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new Rule64ValidationSummary { Success = false, Error = "Only the assigned data analyst can run Rule 64." });
            }

            async Task<Rule64ValidationSummary> Execute(IRule64Service svc, IAuditLogService auditSvc)
            {
                var result = await svc.RunValidationAsync(request, user?.Email, user?.FullName ?? user?.Email);
                _logger.LogInformation("Rule64 completed for {Email}. Status={Status}, Total={Total}, Pass={Pass}, Fail={Fail}",
                    user?.Email, result.Status, result.TotalCount, result.PassCount, result.FailCount);

                if (result.Success)
                {
                    await auditSvc.LogAsync(
                        "run_validation",
                        $"Rule 64 on client {request.ClientId}: {result.Status} ({result.FailCount} exception rows), run {result.SavedRunId}",
                        user?.Id,
                        user?.Email);
                }

                return result;
            }

            if (ValidationOperationHttpHelper.IsAsyncRequested(Request))
            {
                return ValidationOperationHttpHelper.Queue(
                    this,
                    HttpContext.RequestServices.GetRequiredService<IValidationOperationService>(),
                    ValidationOperationHttpHelper.ResolveOwnerKey(User),
                    "Rule 64 validation",
                    async (sp, ct) => await Execute(
                        sp.GetRequiredService<IRule64Service>(),
                        sp.GetRequiredService<IAuditLogService>()));
            }

            return Json(await Execute(_rule64, _audit));
        }

        [HttpPost]
        public async Task<IActionResult> SaveWorkspace([FromBody] Rule64ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!await CanEditAsync(request.ClientId, user, role))
                return Json(new Rule64WorkspaceSaveResult { Success = false, Error = "Only the assigned data analyst can save a workspace." });

            var result = await _rule64.SaveWorkspaceAsync(request, user!.Email!, user.FullName);
            if (result.Success)
                await _audit.LogAsync("save_validation_workspace", $"DataAnalyst saved Rule 64 workspace for client {request.ClientId}.", user.Id, user.Email);
            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> BeginWorkspaceEdit([FromBody] Rule64ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!await CanEditAsync(request.ClientId, user, role))
                return Json(new Rule64WorkspaceSaveResult { Success = false, Error = "Only the assigned data analyst can edit a saved workspace." });
            if (!request.RunId.HasValue || request.RunId.Value <= 0)
                return Json(new Rule64WorkspaceSaveResult { Success = false, Error = "Select a saved run before editing the workspace." });

            var result = await _rule64.BeginWorkspaceEditAsync(request.RunId.Value, user!.Email!, user.FullName);
            if (result.Success)
                await _audit.LogAsync("workspace_edit_started", $"DataAnalyst started editing Rule 64 run {request.RunId.Value}.", user.Id, user.Email);
            return Json(result);
        }

        [HttpPost]
        public IActionResult GenerateSql([FromBody] Rule64ValidationRequest request) =>
            Json(new Rule64SqlResult { Success = true, Sql = _rule64.GenerateSql(request) });
        [HttpPost]
        public IActionResult GenerateRScript([FromBody] Rule64ValidationRequest request) =>
            Json(new Rule64SqlResult { Success = true, Sql = Rule64RScriptGenerator.Generate(request) + RScriptScaffold.BuildAutoExportFooter("Rule64") });

        [HttpPost]
        public async Task<IActionResult> SignOffWorkspace([FromBody] Rule64WorkspaceSignoffInputModel model)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (model.ClientId <= 0)
                return Json(new { success = false, error = "Select an engagement before signing off." });
            if (!await ValidationRunAccessPolicy.CanAssignedUserRemoveOwnSignoffAsync(_systemDb, model.ClientId, user, role))
                return Json(new { success = false, error = "Only the assigned data analyst, manager, or director can sign off." });
            if (!model.RunId.HasValue || model.RunId.Value <= 0)
                return Json(new { success = false, error = "Run the validation first so the saved workspace exists." });

            var review = await _rule64.GetSavedRunAsync(model.RunId.Value, user?.Email);
            if (review == null || review.ClientId != model.ClientId)
                return Json(new { success = false, error = "The saved validation run could not be found for this engagement." });
            if (!review.IsCurrentRun)
                return Json(new { success = false, error = "History results are read-only. Signoff is only available on the current run." });
            if (!ValidationRunAccessPolicy.CanCompleteReviewSignoff(role, review.CurrentUserEngagementRole, review.HasDataAnalystSignoff))
                return Json(new { success = false, error = "The assigned data analyst must sign off before this review can be completed." });

            await _rule64.AddOrUpdateSignoffAsync(model.RunId.Value, user!.Email!, model.Comment);
            await _audit.LogAsync("signoff_validation_run", $"Rule 64 signoff saved for run {model.RunId.Value}", user.Id, user.Email);

            var workspace = await _rule64.GetCurrentWorkspaceStateAsync(model.ClientId, user?.Email);
            var resultsVisible = CanViewResults(role, workspace);
            if (workspace != null)
                workspace.ResultsVisible = resultsVisible;
            return Json(new { success = true, message = "Signoff saved.", resultsVisible, workspace });
        }

        [HttpPost]
        public async Task<IActionResult> RemoveWorkspaceSignoff([FromBody] Rule64WorkspaceSignoffInputModel model)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (model.ClientId <= 0 || !model.RunId.HasValue || model.RunId.Value <= 0)
                return Json(new { success = false, error = "Select a saved run before removing signoff." });
            if (!await ValidationRunAccessPolicy.CanAssignedUserRemoveOwnSignoffAsync(_systemDb, model.ClientId, user, role))
                return Json(new { success = false, error = "Only the assigned data analyst, manager, or director can remove signoff." });

            var review = await _rule64.GetSavedRunAsync(model.RunId.Value, user?.Email);
            if (review == null || review.ClientId != model.ClientId)
                return Json(new { success = false, error = "The saved validation run could not be found for this engagement." });
            if (!review.IsCurrentRun)
                return Json(new { success = false, error = "History results are read-only." });
            if (!review.CurrentUserHasSignedOff)
                return Json(new { success = false, error = "There is no signoff for your assigned engagement role to remove." });

            await _rule64.RemoveSignoffAsync(model.RunId.Value, user!.Email!);
            await _audit.LogAsync("remove_validation_signoff", $"Signoff removed for Rule 64 run {model.RunId.Value}", user.Id, user.Email);

            var workspace = await _rule64.GetCurrentWorkspaceStateAsync(model.ClientId, user?.Email);
            var resultsVisible = CanViewResults(role, workspace);
            if (workspace != null)
                workspace.ResultsVisible = resultsVisible;
            return Json(new { success = true, message = "Signoff removed.", resultsVisible, workspace });
        }

        [HttpPost]
        public async Task<IActionResult> DownloadExcel([FromBody] Rule64ValidationSummary summary)
        {
            var resolved = await ResolveDownloadSummaryAsync(summary);
            var bytes = _export.ExportRule64Excel(resolved);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Rule64_STUD_CREG_Student_Number_Validation_{Ts()}.xlsx");
        }

        [HttpPost]
        public async Task<IActionResult> DownloadCsv([FromBody] Rule64ValidationSummary summary)
        {
            var resolved = await ResolveDownloadSummaryAsync(summary);
            var bytes = BuildCsvExport(resolved);
            return File(bytes, "text/csv", $"Rule64_STUD_CREG_Student_Number_Validation_{Ts()}.csv");
        }

        [HttpPost]
        public IActionResult DownloadSql([FromBody] Rule64ValidationRequest request)
        {
            var sql = _rule64.GenerateSql(request);
            var bytes = System.Text.Encoding.UTF8.GetBytes(sql);
            return File(bytes, "application/sql", $"Rule64_STUD_CREG_Student_Number_Validation_{Ts()}.sql");
        }

        [HttpGet]
        public async Task<IActionResult> DownloadSavedExcel(int runId)
        {
            var stored = await _rule64.GetStoredSummaryAsync(runId);
            var resolved = await ResolveDownloadSummaryAsync(stored ?? new Rule64ValidationSummary { SavedRunId = runId });
            var bytes = _export.ExportRule64Excel(resolved);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Rule64_STUD_CREG_Student_Number_Validation_Run_{runId}.xlsx");
        }

        [HttpGet]
        public async Task<IActionResult> DownloadSavedCsv(int runId)
        {
            var stored = await _rule64.GetStoredSummaryAsync(runId);
            var resolved = await ResolveDownloadSummaryAsync(stored ?? new Rule64ValidationSummary { SavedRunId = runId });
            var bytes = BuildCsvExport(resolved);
            return File(bytes, "text/csv", $"Rule64_STUD_CREG_Student_Number_Validation_Run_{runId}.csv");
        }

        [HttpGet]
        public async Task<IActionResult> DownloadSavedSql(int runId)
        {
            var stored = await _rule64.GetStoredSummaryAsync(runId);
            if (stored == null) return NotFound();
            var sql = _rule64.GenerateSql(new Rule64ValidationRequest
            {
                ClientId = stored.ClientId,
                StudTable = stored.StudTable,
                CregTable = stored.CregTable,
                ProdTable = stored.ProdTable,
                ColumnMapping = stored.ColumnMapping
            });
            var bytes = System.Text.Encoding.UTF8.GetBytes(sql);
            return File(bytes, "application/sql", $"Rule64_STUD_CREG_Student_Number_Validation_Run_{runId}.sql");
        }

        private static string Ts() => DateTime.Now.ToString("yyyyMMdd_HHmmss");

        private static byte[] BuildCsvExport(Rule64ValidationSummary summary)
        {
            var passRows = summary.PassRows ?? new List<Rule64ReviewRow>();
            var failRows = summary.FailRows ?? new List<Rule64ReviewRow>();
            using var stream = new MemoryStream();
            using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
            writer.WriteLine("\"HEMIS RULE 64 - STUD to CREG Student Number Validation\"");
            writer.WriteLine($"\"Timestamp\",\"{summary.Timestamp}\"");
            writer.WriteLine($"\"Status\",\"{summary.Status}\"");
            writer.WriteLine($"\"Total Rows\",{summary.TotalCount},\"Clear Rows\",{summary.PassCount},\"Flagged Rows\",{summary.FailCount}");
            writer.WriteLine();
            WriteReviewCsv(writer, passRows, failRows, false);
            writer.Flush();
            return stream.ToArray();
        }

        private static void WriteReviewCsv(StreamWriter writer, IReadOnlyCollection<Rule64ReviewRow> passRows, IReadOnlyCollection<Rule64ReviewRow> failRows, bool exceptionsOnly)
        {
            writer.WriteLine("\"Source Table\",\"STUD Student No\",\"CREG Student No\",\"STUD Compare Value\",\"CREG Compare Value\",\"PRODUCTION Student No\",\"Error Code\",\"Result\",\"Explanation\"");
            var rows = exceptionsOnly ? failRows : failRows.Concat(passRows);
            foreach (var row in rows)
            {
                writer.WriteLine(string.Join(",", new[]
                {
                    CsvValue(row.SourceTable),
                    CsvValue(row.StudentNo),
                    CsvValue(row.CregStudentNo),
                    CsvValue(row.StudCompareValue),
                    CsvValue(row.CregCompareValue),
                    CsvValue(row.ProdStudentNo),
                    CsvValue(row.ErrorCode),
                    CsvValue(row.ValidationResult),
                    CsvValue(row.ValidationExplanation)
                }));
            }
        }

        private static string CsvValue(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

        private async Task<Rule64ValidationSummary> ResolveDownloadSummaryAsync(Rule64ValidationSummary? summary)
        {
            if (summary?.SavedRunId is int runId && runId > 0)
            {
                var stored = await _rule64.GetStoredSummaryAsync(runId);
                if (stored != null)
                    summary = stored;
            }

            summary ??= new Rule64ValidationSummary();
            summary.PassRows ??= new List<Rule64ReviewRow>();
            summary.FailRows ??= new List<Rule64ReviewRow>();
            summary.ExceptionCategories ??= new List<Rule64ExceptionCategoryViewModel>();

            if (summary.PassCount <= 0 && summary.PassRows.Count > 0)
                summary.PassCount = summary.PassRows.Count;
            if (summary.FailCount <= 0 && summary.FailRows.Count > 0)
                summary.FailCount = summary.FailRows.Count;
            if (summary.TotalCount <= 0)
                summary.TotalCount = summary.PassCount + summary.FailCount;

            summary.ExceptionDetailCount = Math.Max(summary.ExceptionDetailCount, summary.FailRows.Count);
            summary.ExceptionRate = summary.TotalCount == 0
                ? 0m
                : Math.Round((decimal)summary.FailCount / summary.TotalCount * 100m, 2);

            if (string.IsNullOrWhiteSpace(summary.Status))
                summary.Status = summary.FailCount == 0 ? "PASS" : "FAIL";

            return summary;
        }

        private async Task<string> GetCurrentSystemRoleAsync(ApplicationUser? user)
        {
            var systemRole = await _systemDb.GetSystemRoleAsync(user);
            if (!string.IsNullOrWhiteSpace(systemRole))
                return systemRole!;

            var roles = user != null ? await _users.GetRolesAsync(user) : new List<string>();
            return roles.FirstOrDefault() ?? "";
        }

        private static bool CanViewResults(string role, Rule64WorkspaceStateViewModel? workspace)
        {
            if (workspace == null)
                return false;

            return ValidationRunAccessPolicy.CanViewSignedResults(role, workspace.CurrentUserEngagementRole, workspace.HasDataAnalystSignoff);
        }

        private async Task<bool> CanEditAsync(int clientId, ApplicationUser? user, string role)
        {
            if (user == null || !string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) || clientId <= 0)
                return false;
            if (!await _systemDb.CanAccessClientResultsAsync(clientId, user, role))
                return false;

            var engagementRole = await _systemDb.GetEngagementRoleAsync(clientId, user, role);
            return ValidationRunAccessPolicy.IsAssignedDataAnalyst(engagementRole);
        }

        private async Task<object> RequireDataAnalystAsync<T>(Func<Task<T>> action)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase))
                return new { success = false, error = "Only the assigned data analyst can configure Rule 64." };
            return (object?)await action() ?? new { success = false, error = "No data returned." };
        }
    }
}
