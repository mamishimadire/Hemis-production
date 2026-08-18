using HemisAudit.Helpers;
using HemisAudit.Models;
using HemisAudit.Services;
using HemisAudit.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace HemisAudit.Controllers
{
    [Authorize]
    public class Rule68Controller : Controller
    {
        private readonly IRule68Service _rule68;
        private readonly IExportService _export;
        private readonly IAuditLogService _audit;
        private readonly UserManager<ApplicationUser> _users;
        private readonly ISystemDatabaseService _systemDb;

        public Rule68Controller(IRule68Service rule68, IExportService export, IAuditLogService audit, UserManager<ApplicationUser> users, ISystemDatabaseService systemDb)
        {
            _rule68 = rule68; _export = export; _audit = audit; _users = users; _systemDb = systemDb;
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

            ViewBag.Clients = clients.Select(c => new Client
            {
                Id = c.Id, Name = c.EngagementName, FiscalYear = c.MaconomyNumber,
                Status = c.Status, CreatedAt = c.CreatedAt, CreatedByUserId = "", IsActive = true
            }).ToList();
            ViewBag.ClientId = clientId;
            ViewBag.CurrentSystemRole = role;
            ViewBag.ModuleNavigation = ModuleSequenceNavigationHelper.BuildForWorkspace(68, clientId);
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetWorkspaceState(int clientId)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            await _systemDb.NormalizeCompletedRunStatusesAsync();

            if (clientId <= 0) return Json(new { success = true, hasWorkspace = false });

            if (!await _systemDb.CanAccessClientResultsAsync(clientId, user, role))
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                return Json(new { success = false, error = "You cannot access this engagement." });
            }

            var workspace = await _rule68.GetCurrentWorkspaceStateAsync(clientId, user?.Email);
            var resultsVisible = CanViewWorkspaceResults(role, workspace);
            if (workspace != null) workspace.ResultsVisible = resultsVisible;
            if (workspace != null && !resultsVisible) workspace.Summary = null;

            return Json(new { success = true, hasWorkspace = workspace != null, resultsVisible, workspace });
        }

        [HttpGet]
        public async Task<IActionResult> Run(int id)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            await _systemDb.NormalizeCompletedRunStatusesAsync();

            var review = await _rule68.GetSavedRunAsync(id, user?.Email);
            if (review == null) return NotFound();

            if (!await _systemDb.CanAccessClientResultsAsync(review.ClientId, user, role))
            {
                TempData["Error"] = "You do not have access to this saved validation run.";
                return RedirectToAction("Index", "Dashboard");
            }

            if (!CanViewSavedRun(review, role))
            {
                TempData["Error"] = "Only analyst-signed validation results are available for review.";
                return RedirectToAction("Index", "Dashboard");
            }

            ViewBag.IsAdmin = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
            ViewBag.CanDownloadSavedRun = CanDownloadSavedRun(review, role);
            ViewBag.CanManageEngagement =
                string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase);
            var clientDetail = await _systemDb.GetClientDetailAsync(review.ClientId, user, role);
            ViewBag.IsArchived = clientDetail?.IsArchived == true;
            ViewBag.ModuleNavigation = ModuleSequenceNavigationHelper.BuildForSavedRun(68, review.ClientId, clientDetail?.ValidationRuns, role, review.CurrentUserEngagementRole);
            ViewBag.CanOpenWorkspace =
                clientDetail?.IsArchived != true &&
                await _systemDb.CanAccessClientModuleAsync(review.ClientId, user, role) &&
                (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(review.CurrentUserEngagementRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));

            return View(review);
        }

        [HttpPost]
        public async Task<IActionResult> GetTables([FromBody] EngagementTableListRequest model) =>
            Json(await RequireDataAnalystAsync(async () => await _rule68.GetTablesAsync(model.ClientId)));

        [HttpPost]
        public async Task<IActionResult> GetColumns([FromBody] Rule68GetColumnsRequest request) =>
            Json(await RequireDataAnalystAsync(async () => await _rule68.GetColumnsAsync(request.ClientId, request.TableName, request.TableRole)));

        [HttpPost]
        public async Task<IActionResult> VerifyTables([FromBody] Rule68ValidationRequest request) =>
            Json(await RequireDataAnalystAsync(async () => await _rule68.VerifyTablesAsync(request)));

        [HttpPost]
        public async Task<IActionResult> RunValidation([FromBody] Rule68ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (request.ClientId <= 0) return Json(new Rule68ValidationSummary { Success = false, Error = "Select an approved engagement before running validation." });
            if (!await _systemDb.CanAccessClientResultsAsync(request.ClientId, user, role)) return Json(new Rule68ValidationSummary { Success = false, Error = "You cannot access this engagement." });

            var engagementRole = await _systemDb.GetEngagementRoleAsync(request.ClientId, user, role);
            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) || !string.Equals(engagementRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase))
                return Json(new Rule68ValidationSummary { Success = false, Error = "Only the assigned data analyst can run Rule 68." });

            async Task<Rule68ValidationSummary> Execute(IRule68Service svc, IAuditLogService auditSvc)
            {
                var result = await svc.RunValidationAsync(request, user?.Email, user?.FullName ?? user?.Email);
                if (result.Success)
                    await auditSvc.LogAsync("run_validation", $"Rule 68 on client {request.ClientId}: {result.Status} ({result.FailCount} fail rows), run {result.SavedRunId}.", user?.Id, user?.Email);
                return result;
            }

            if (ValidationOperationHttpHelper.IsAsyncRequested(Request))
            {
                return ValidationOperationHttpHelper.Queue(this, HttpContext.RequestServices.GetRequiredService<IValidationOperationService>(),
                    ValidationOperationHttpHelper.ResolveOwnerKey(User), "Rule 68 validation",
                    async (sp, ct) => await Execute(sp.GetRequiredService<IRule68Service>(), sp.GetRequiredService<IAuditLogService>()));
            }

            return Json(await Execute(_rule68, _audit));
        }

        [HttpPost]
        public async Task<IActionResult> BeginWorkspaceEdit([FromBody] Rule68ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!await CanEditWorkspaceAsync(request.ClientId, user, role)) return Json(new Rule68WorkspaceSaveResult { Success = false, Error = "Only the assigned data analyst can edit a saved workspace." });
            if (!request.RunId.HasValue || request.RunId.Value <= 0) return Json(new Rule68WorkspaceSaveResult { Success = false, Error = "Select a saved run before editing." });

            var result = await _rule68.BeginWorkspaceEditAsync(request.RunId.Value, user!.Email!, user.FullName);
            if (result.Success) await _audit.LogAsync("workspace_edit_started", $"DataAnalyst started editing Rule 68 run {request.RunId.Value}.", user?.Id, user?.Email);
            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> SaveWorkspace([FromBody] Rule68ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!await CanEditWorkspaceAsync(request.ClientId, user, role)) return Json(new Rule68WorkspaceSaveResult { Success = false, Error = "Only the assigned data analyst can save a workspace." });

            var result = await _rule68.SaveWorkspaceAsync(request, user!.Email!, user.FullName);
            if (result.Success) await _audit.LogAsync("save_validation_workspace", $"DataAnalyst saved Rule 68 workspace for client {request.ClientId}. Run: {result.Workspace?.RunId}", user?.Id, user?.Email);
            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> SignOffWorkspace([FromBody] Rule68WorkspaceSignoffInputModel model)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (model.ClientId <= 0) return Json(new { success = false, error = "Select an engagement before signing off." });
            if (!await ValidationRunAccessPolicy.CanAssignedUserRemoveOwnSignoffAsync(_systemDb, model.ClientId, user, role)) return Json(new { success = false, error = "Only the assigned data analyst, manager, or director can sign off." });
            if (!model.RunId.HasValue || model.RunId.Value <= 0) return Json(new { success = false, error = "Run the validation first." });

            var review = await _rule68.GetSavedRunAsync(model.RunId.Value, user?.Email);
            if (review == null || review.ClientId != model.ClientId) return Json(new { success = false, error = "The saved validation run could not be found." });
            if (!review.IsCurrentRun) return Json(new { success = false, error = "History results are read-only." });

            var clientDetail = await _systemDb.GetClientDetailAsync(model.ClientId, user, role);
            if (clientDetail?.IsArchived == true) return Json(new { success = false, error = "Archived engagements are read-only." });
            if (!ValidationRunAccessPolicy.CanCompleteReviewSignoff(role, review.CurrentUserEngagementRole, review.HasDataAnalystSignoff))
                return Json(new { success = false, error = "The assigned data analyst must sign off before this review can be completed." });

            try
            {
                await _rule68.AddOrUpdateSignoffAsync(model.RunId.Value, user!.Email!, model.Comment);
                await _audit.LogAsync("signoff_validation_run", $"Rule 68 signoff saved for run {model.RunId.Value}", user.Id, user.Email);
            }
            catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }

            var workspace = await _rule68.GetCurrentWorkspaceStateAsync(model.ClientId, user?.Email);
            var resultsVisible = CanViewWorkspaceResults(role, workspace);
            if (workspace != null) workspace.ResultsVisible = resultsVisible;
            return Json(new { success = true, message = "Signoff saved.", resultsVisible, workspace });
        }

        [HttpPost]
        public async Task<IActionResult> RemoveWorkspaceSignoff([FromBody] Rule68WorkspaceSignoffInputModel model)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (model.ClientId <= 0 || !model.RunId.HasValue || model.RunId.Value <= 0) return Json(new { success = false, error = "Select a saved run before removing signoff." });
            if (!await ValidationRunAccessPolicy.CanAssignedUserRemoveOwnSignoffAsync(_systemDb, model.ClientId, user, role)) return Json(new { success = false, error = "Only the assigned data analyst, manager, or director can remove signoff." });

            var review = await _rule68.GetSavedRunAsync(model.RunId.Value, user?.Email);
            if (review == null || review.ClientId != model.ClientId) return Json(new { success = false, error = "The saved validation run could not be found." });
            if (!review.IsCurrentRun) return Json(new { success = false, error = "History results are read-only." });

            var clientDetail = await _systemDb.GetClientDetailAsync(model.ClientId, user, role);
            if (clientDetail?.IsArchived == true) return Json(new { success = false, error = "Archived engagements are read-only." });
            if (!review.CurrentUserHasSignedOff) return Json(new { success = false, error = "There is no signoff to remove." });

            try { await _rule68.RemoveSignoffAsync(model.RunId.Value, user!.Email!); }
            catch (Exception ex) { return Json(new { success = false, error = ex.Message }); }

            var workspace = await _rule68.GetCurrentWorkspaceStateAsync(model.ClientId, user?.Email);
            var resultsVisible = CanViewWorkspaceResults(role, workspace);
            if (workspace != null) workspace.ResultsVisible = resultsVisible;
            await _audit.LogAsync("remove_validation_signoff", $"{review.CurrentUserEngagementRole} removed signoff for Rule 68 run {model.RunId.Value}", user?.Id, user?.Email);
            return Json(new { success = true, message = "Signoff removed.", resultsVisible, workspace });
        }

        [HttpPost]
        public IActionResult GenerateSql([FromBody] Rule68ValidationRequest request) =>
            Json(new Rule68SqlResult { Success = true, Sql = _rule68.GenerateSql(request) });

        [HttpGet]
        public async Task<IActionResult> DownloadSavedExcel(int runId)
        {
            var review = await LoadAuthorizedSavedRunAsync(runId);
            if (review == null) return RedirectToAction(nameof(Run), new { id = runId });
            var fullSummary = await _rule68.GetStoredSummaryAsync(runId) ?? review.Summary;
            return File(_export.ExportRule68Excel(fullSummary), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Rule68_Credit_Overload_Run_{runId}.xlsx");
        }

        [HttpGet]
        public async Task<IActionResult> DownloadSavedCsv(int runId)
        {
            var review = await LoadAuthorizedSavedRunAsync(runId);
            if (review == null) return RedirectToAction(nameof(Run), new { id = runId });
            var fullSummary = await _rule68.GetStoredSummaryAsync(runId) ?? review.Summary;
            return File(_export.ExportRule68Csv(fullSummary), "text/csv", $"Rule68_Credit_Overload_Run_{runId}.csv");
        }

        [HttpGet]
        public async Task<IActionResult> DownloadSavedSql(int runId)
        {
            var review = await LoadAuthorizedSavedRunAsync(runId);
            if (review == null) return RedirectToAction(nameof(Run), new { id = runId });
            var sql = _rule68.GenerateSql(new Rule68ValidationRequest
            {
                ClientId = review.ClientId,
                StudTable = review.Summary.StudTable,
                CregTable = review.Summary.CregTable,
                QualTable = review.Summary.QualTable,
                CredTable = review.Summary.CredTable,
                CrseTable = review.Summary.CrseTable,
                DetailTable = review.Summary.DetailTable,
                CregStudNoCol = review.Summary.CregStudNoCol,
                CregQualCol = review.Summary.CregQualCol,
                CregCourseCol = review.Summary.CregCourseCol,
                QualQualCol = review.Summary.QualQualCol,
                QualNameCol = review.Summary.QualNameCol,
                CredQualCol = review.Summary.CredQualCol,
                CredCourseCol = review.Summary.CredCourseCol,
                CredCreditsCol = review.Summary.CredCreditsCol,
                CrseCourseCol = review.Summary.CrseCourseCol,
                CrseNameCol = review.Summary.CrseNameCol,
                MaxTotalCredits = review.Summary.MaxTotalCredits,
                DetailErrorTypeCol = review.Summary.DetailErrorTypeCol,
                DetailErrorCol = review.Summary.DetailErrorCol,
                DetailErrorTypeValue = review.Summary.DetailErrorTypeValue,
                DetailExclusionCodes = review.Summary.DetailExclusionCodes,
                DetailElementInfoCol = review.Summary.DetailElementInfoCol
            });
            return File(_export.ExportSql(sql), "application/sql", $"Rule68_Credit_Overload_Run_{runId}.sql");
        }

        [HttpPost]
        public async Task<IActionResult> DownloadExcel([FromBody] Rule68ValidationSummary summary)
        {
            var resolved = await ResolveExportSummaryAsync(summary);
            return File(_export.ExportRule68Excel(resolved), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Rule68_Credit_Overload_{Ts()}.xlsx");
        }

        [HttpPost]
        public async Task<IActionResult> DownloadCsv([FromBody] Rule68ValidationSummary summary)
        {
            var resolved = await ResolveExportSummaryAsync(summary);
            return File(_export.ExportRule68Csv(resolved), "text/csv", $"Rule68_Credit_Overload_{Ts()}.csv");
        }

        [HttpPost]
        public IActionResult DownloadSql([FromBody] Rule68ValidationRequest request) =>
            File(_export.ExportSql(_rule68.GenerateSql(request)), "application/sql", $"Rule68_Credit_Overload_{Ts()}.sql");

        // ─── Private helpers ──────────────────────────────────────────────────

        private async Task<Rule68RunReviewViewModel?> LoadAuthorizedSavedRunAsync(int runId)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            var review = await _rule68.GetSavedRunAsync(runId, user?.Email);
            if (review == null) { TempData["Error"] = "Saved validation run was not found."; return null; }
            if (!await _systemDb.CanAccessClientResultsAsync(review.ClientId, user, role)) { TempData["Error"] = "You do not have access."; return null; }
            if (!CanViewSavedRun(review, role)) { TempData["Error"] = "Only analyst-signed results are available."; return null; }
            if (!CanDownloadSavedRun(review, role)) { TempData["Error"] = "The data analyst must sign off first."; return null; }
            return review;
        }

        private async Task<Rule68ValidationSummary> ResolveExportSummaryAsync(Rule68ValidationSummary summary)
        {
            if (summary.SavedRunId is int savedRunId && savedRunId > 0)
            {
                var stored = await _rule68.GetStoredSummaryAsync(savedRunId);
                if (stored != null) return stored;
            }
            if (summary.ClientId > 0)
            {
                var user = await _users.GetUserAsync(User);
                var workspace = await _rule68.GetCurrentWorkspaceStateAsync(summary.ClientId, user?.Email);
                if (workspace?.RunId is int workspaceRunId && workspaceRunId > 0)
                {
                    var stored = await _rule68.GetStoredSummaryAsync(workspaceRunId);
                    if (stored != null) return stored;
                }
            }
            return summary;
        }

        private static bool CanDownloadSavedRun(Rule68RunReviewViewModel review, string systemRole)
            => ValidationRunAccessPolicy.CanDownloadSignedResults(systemRole, review.CurrentUserEngagementRole, review.HasDataAnalystSignoff);

        private static bool CanViewSavedRun(Rule68RunReviewViewModel review, string systemRole)
            => ValidationRunAccessPolicy.CanViewSignedResults(systemRole, review.CurrentUserEngagementRole, review.HasDataAnalystSignoff);

        private static bool CanViewWorkspaceResults(string role, Rule68WorkspaceStateViewModel? workspace)
        {
            if (workspace == null) return false;
            return ValidationRunAccessPolicy.CanViewSignedResults(role, workspace.CurrentUserEngagementRole, workspace.HasDataAnalystSignoff);
        }

        private async Task<bool> CanEditWorkspaceAsync(int clientId, ApplicationUser? user, string role)
        {
            if (user == null || !string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) || clientId <= 0) return false;
            if (!await _systemDb.CanAccessClientResultsAsync(clientId, user, role)) return false;
            var engagementRole = await _systemDb.GetEngagementRoleAsync(clientId, user, role);
            return ValidationRunAccessPolicy.IsAssignedDataAnalyst(engagementRole);
        }

        private async Task<string> GetCurrentSystemRoleAsync(ApplicationUser? user)
        {
            var systemRole = await _systemDb.GetSystemRoleAsync(user);
            if (!string.IsNullOrWhiteSpace(systemRole)) return systemRole!;
            var roles = user != null ? await _users.GetRolesAsync(user) : new List<string>();
            return roles.FirstOrDefault() ?? "";
        }

        private async Task<object> RequireDataAnalystAsync<T>(Func<Task<T>> action) where T : class
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase))
                return new { success = false, error = "Only the assigned data analyst can configure or run Rule 68." };
            var result = await action();
            return result ?? (object)new { success = false, error = "Action returned no result." };
        }

        private static string Ts() => DateTime.Now.ToString("yyyyMMdd_HHmmss");
    }
}
