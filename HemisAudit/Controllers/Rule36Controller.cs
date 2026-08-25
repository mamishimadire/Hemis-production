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
    public class Rule36Controller : Controller
    {
        private readonly IRule36Service _rule36;
        private readonly IExportService _export;
        private readonly IAuditLogService _audit;
        private readonly UserManager<ApplicationUser> _users;
        private readonly ISystemDatabaseService _systemDb;

        public Rule36Controller(
            IRule36Service rule36,
            IExportService export,
            IAuditLogService audit,
            UserManager<ApplicationUser> users,
            ISystemDatabaseService systemDb)
        {
            _rule36 = rule36;
            _export = export;
            _audit = audit;
            _users = users;
            _systemDb = systemDb;
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
            ViewBag.ModuleNavigation = ModuleSequenceNavigationHelper.BuildForWorkspace(36, clientId);
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetWorkspaceState(int clientId)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            await _systemDb.NormalizeCompletedRunStatusesAsync();

            if (clientId <= 0)
            {
                return Json(new
                {
                    success = true,
                    hasWorkspace = false
                });
            }

            if (!await _systemDb.CanAccessClientResultsAsync(clientId, user, role))
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                return Json(new
                {
                    success = false,
                    error = "You cannot access this engagement."
                });
            }

            var workspace = await _rule36.GetCurrentWorkspaceStateAsync(clientId, user?.Email);
            var resultsVisible = CanViewWorkspaceResults(role, workspace);

            if (workspace != null)
            {
                workspace.ResultsVisible = resultsVisible;
            }

            if (workspace != null && !resultsVisible) workspace.Summary = null;

            return Json(new
            {
                success = true,
                hasWorkspace = workspace != null,
                resultsVisible,
                workspace
            });
        }

        [HttpGet]
        public async Task<IActionResult> Run(int id)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            await _systemDb.NormalizeCompletedRunStatusesAsync();
            var review = await _rule36.GetSavedRunAsync(id, user?.Email);
            if (review == null)
                return NotFound();

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
            var isArchived = clientDetail?.IsArchived == true;
            ViewBag.IsArchived = isArchived;
            ViewBag.ModuleNavigation = ModuleSequenceNavigationHelper.BuildForSavedRun(
                36,
                review.ClientId,
                clientDetail?.ValidationRuns,
                role,
                review.CurrentUserEngagementRole);
            ViewBag.CanOpenWorkspace =
                !isArchived &&
                await _systemDb.CanAccessClientModuleAsync(review.ClientId, user, role) &&
                (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(review.CurrentUserEngagementRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase));

            review.GeneratedSql = _rule36.GenerateSql(new Rule36ValidationRequest
            {
                ClientId = review.ClientId,
                StudTable = review.Summary.StudTable,
                DeceasedTable = review.Summary.DeceasedTable,
                StudColumn = review.Summary.StudColumn,
                DeceasedColumn = review.Summary.DeceasedColumn
            });
            return View(review);
        }

        [HttpPost]
        public async Task<IActionResult> GetTables([FromBody] EngagementTableListRequest model) =>
            Json(await RequireDataAnalystAsync(async () => await _rule36.GetTablesAsync(model.ClientId)));

        [HttpPost]
        public async Task<IActionResult> GetColumns([FromBody] Rule36GetColumnsRequest model) =>
            Json(await RequireDataAnalystAsync(async () => await _rule36.GetColumnsAsync(model.ClientId, model.TableName, model.IsStudTable)));

        [HttpPost]
        public async Task<IActionResult> VerifyTables([FromBody] Rule36VerifyRequest request) =>
            Json(await RequireDataAnalystAsync(async () => await _rule36.VerifyDataAsync(request)));

        [HttpPost]
        public async Task<IActionResult> RunValidation([FromBody] Rule36ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (request.ClientId <= 0)
            {
                return Json(new Rule36ValidationSummary
                {
                    Success = false,
                    Error = "Select an approved engagement before running validation."
                });
            }

            if (!await _systemDb.CanAccessClientResultsAsync(request.ClientId, user, role))
            {
                return Json(new Rule36ValidationSummary
                {
                    Success = false,
                    Error = "You cannot access this engagement."
                });
            }

            var engagementRole = await _systemDb.GetEngagementRoleAsync(request.ClientId, user, role);
            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(engagementRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new Rule36ValidationSummary
                {
                    Success = false,
                    Error = "Only the assigned data analyst can run Rule 36."
                });
            }

            async Task<Rule36ValidationSummary> ExecuteValidationAsync(IRule36Service ruleService, IAuditLogService auditService)
            {
                var result = await ruleService.RunValidationAsync(request, user?.Email, user?.FullName ?? user?.Email);
                if (result.Success)
                {
                    await auditService.LogAsync(
                        "run_validation",
                        $"Rule 36 on client {request.ClientId}: {result.Status} ({result.FailCount} exceptions), run {result.SavedRunId}",
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
                    "Rule 36 validation",
                    async (sp, ct) => await ExecuteValidationAsync(
                        sp.GetRequiredService<IRule36Service>(),
                        sp.GetRequiredService<IAuditLogService>()));
            }

            var result = await ExecuteValidationAsync(_rule36, _audit);
            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> BeginWorkspaceEdit([FromBody] Rule36ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await CanEditWorkspaceAsync(request.ClientId, user, role))
            {
                return Json(new Rule36WorkspaceSaveResult
                {
                    Success = false,
                    Error = "Only the assigned data analyst can edit a saved workspace."
                });
            }

            if (!request.RunId.HasValue || request.RunId.Value <= 0)
            {
                return Json(new Rule36WorkspaceSaveResult
                {
                    Success = false,
                    Error = "Select a saved run before editing the workspace."
                });
            }

            var result = await _rule36.BeginWorkspaceEditAsync(request.RunId.Value, user!.Email!, user.FullName);
            if (result.Success)
            {
                await _audit.LogAsync(
                    "workspace_edit_started",
                    $"DataAnalyst started editing Rule 36 run {request.RunId.Value}. Existing signoffs were cleared.",
                    user?.Id,
                    user?.Email);
            }

            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> SaveWorkspace([FromBody] Rule36ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await CanEditWorkspaceAsync(request.ClientId, user, role))
            {
                return Json(new Rule36WorkspaceSaveResult
                {
                    Success = false,
                    Error = "Only the assigned data analyst can save a workspace."
                });
            }

            var result = await _rule36.SaveWorkspaceAsync(request, user!.Email!, user.FullName);
            if (result.Success)
            {
                await _audit.LogAsync(
                    "save_validation_workspace",
                    $"DataAnalyst saved Rule 36 workspace for client {request.ClientId}. Signoffs cleared: {result.ClearedSignoffCount ?? 0}",
                    user?.Id,
                    user?.Email);
            }

            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> SignOffWorkspace([FromBody] Rule36WorkspaceSignoffInputModel model)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (model.ClientId <= 0)
            {
                return Json(new
                {
                    success = false,
                    error = "Select an engagement before signing off."
                });
            }

            if (!await ValidationRunAccessPolicy.CanAssignedUserRemoveOwnSignoffAsync(_systemDb, model.ClientId, user, role))
            {
                return Json(new
                {
                    success = false,
                    error = "Only the assigned data analyst, manager, or director can sign off the workspace."
                });
            }

            if (!model.RunId.HasValue || model.RunId.Value <= 0)
            {
                return Json(new
                {
                    success = false,
                    error = "Run validation first so the workspace is saved."
                });
            }

            var review = await _rule36.GetSavedRunAsync(model.RunId.Value, user?.Email);
            if (review == null || review.ClientId != model.ClientId)
            {
                return Json(new
                {
                    success = false,
                    error = "The saved validation run could not be found for this engagement."
                });
            }

            var clientDetail = await _systemDb.GetClientDetailAsync(model.ClientId, user, role);
            if (clientDetail?.IsArchived == true)
            {
                return Json(new
                {
                    success = false,
                    error = "Archived engagements are read-only. Signoff is disabled."
                });
            }

            if (!ValidationRunAccessPolicy.CanCompleteReviewSignoff(role, review.CurrentUserEngagementRole, review.HasDataAnalystSignoff))
            {
                return Json(new
                {
                    success = false,
                    error = "The assigned data analyst must sign off before this review can be completed."
                });
            }

            try
            {
                await _rule36.AddOrUpdateSignoffAsync(model.RunId.Value, user!.Email!, model.Comment);
                await _audit.LogAsync(
                    "signoff_validation_run",
                    $"DataAnalyst signed off run {model.RunId.Value} from module workspace",
                    user.Id,
                    user.Email);
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    error = ex.Message
                });
            }

            var workspace = await _rule36.GetCurrentWorkspaceStateAsync(model.ClientId, user?.Email);
            var resultsVisible = CanViewWorkspaceResults(role, workspace);
            if (workspace != null)
            {
                workspace.ResultsVisible = resultsVisible;
            }
            return Json(new
            {
                success = true,
                message = "Signoff saved.",
                resultsVisible,
                workspace
            });
        }

        [HttpPost]
        public async Task<IActionResult> RemoveWorkspaceSignoff([FromBody] Rule36WorkspaceSignoffInputModel model)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (model.ClientId <= 0 || !model.RunId.HasValue || model.RunId.Value <= 0)
            {
                return Json(new
                {
                    success = false,
                    error = "Select a saved run before removing signoff."
                });
            }

            if (!await ValidationRunAccessPolicy.CanAssignedUserRemoveOwnSignoffAsync(_systemDb, model.ClientId, user, role))
            {
                return Json(new
                {
                    success = false,
                    error = "Only the assigned data analyst, manager, or director can remove signoff."
                });
            }

            var review = await _rule36.GetSavedRunAsync(model.RunId.Value, user?.Email);
            if (review == null || review.ClientId != model.ClientId)
            {
                return Json(new
                {
                    success = false,
                    error = "The saved validation run could not be found for this engagement."
                });
            }

            var clientDetail = await _systemDb.GetClientDetailAsync(model.ClientId, user, role);
            if (clientDetail?.IsArchived == true)
            {
                return Json(new
                {
                    success = false,
                    error = "Archived engagements are read-only. Signoff removal is disabled."
                });
            }

            if (!review.CurrentUserHasSignedOff)
            {
                return Json(new
                {
                    success = false,
                    error = "There is no signoff for your assigned engagement role to remove."
                });
            }

            try
            {
                await _rule36.RemoveSignoffAsync(model.RunId.Value, user!.Email!);
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    error = ex.Message
                });
            }

            var workspace = await _rule36.GetCurrentWorkspaceStateAsync(model.ClientId, user?.Email);
            var resultsVisible = CanViewWorkspaceResults(role, workspace);
            if (workspace != null)
            {
                workspace.ResultsVisible = resultsVisible;
            }
            var reopenedRunId = workspace?.RunId;
            var preservedHistory = reopenedRunId.HasValue && reopenedRunId.Value != model.RunId.Value;
            var message = preservedHistory
                ? $"Signoff removed. Run #{model.RunId.Value} moved to history and Run #{reopenedRunId.Value} is now the current workspace."
                : "Signoff removed.";
            await _audit.LogAsync(
                "remove_validation_signoff",
                preservedHistory
                    ? $"{review.CurrentUserEngagementRole} removed signoff for Rule 36 run {model.RunId.Value} from module workspace. Historical snapshot preserved; new current run {reopenedRunId.Value} created for continued review."
                    : $"{review.CurrentUserEngagementRole} removed signoff for Rule 36 run {model.RunId.Value} from module workspace",
                user.Id,
                user.Email);
            return Json(new
            {
                success = true,
                message,
                resultsVisible,
                workspace
            });
        }

        [HttpPost]
        public async Task<IActionResult> GenerateSql([FromBody] Rule36ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (request.ClientId > 0 && !await _systemDb.CanAccessClientResultsAsync(request.ClientId, user, role))
            {
                return Json(new SqlResult
                {
                    Success = false,
                    Error = "You cannot access this engagement."
                });
            }

            return Json(RequireDataAnalystResult(() => new SqlResult { Success = true, Sql = _rule36.GenerateSql(request) }));
        }
        [HttpPost]
        public async Task<IActionResult> GenerateRScript([FromBody] Rule36ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (request.ClientId > 0 && !await _systemDb.CanAccessClientResultsAsync(request.ClientId, user, role))
                return Json(new SqlResult { Success = false, Error = "You cannot access this engagement." });

            return Json(RequireDataAnalystResult(() => new SqlResult
            {
                Success = true,
                Sql = Rule36RScriptGenerator.Generate(request) + RScriptScaffold.BuildAutoExportFooter("Rule36")
            }));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AddSignoff(Rule36RunSignoffInputModel model)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            var review = await _rule36.GetSavedRunAsync(model.RunId, user?.Email);
            if (review == null)
                return NotFound();

            var clientDetail = await _systemDb.GetClientDetailAsync(review.ClientId, user, role);
            if (clientDetail?.IsArchived == true)
            {
                TempData["Error"] = "Archived engagements are read-only. Signoff is disabled.";
                return RedirectToAction(nameof(Run), new { id = model.RunId });
            }

            if (!await _systemDb.CanAccessClientResultsAsync(review.ClientId, user, role))
            {
                TempData["Error"] = "You do not have access to sign off this run.";
                return RedirectToAction("Index", "Dashboard");
            }

            if (!CanViewSavedRun(review, role))
            {
                TempData["Error"] = "Only analyst-signed validation results are available for review.";
                return RedirectToAction("Index", "Dashboard");
            }

            if (!review.IsCurrentRun)
            {
                TempData["Error"] = "History results are read-only. Signoff is only available on the current run.";
                return RedirectToAction(nameof(Run), new { id = model.RunId });
            }

            if (!review.CanCurrentUserSignOff)
            {
                TempData["Error"] = "Only the assigned data analyst, manager, or director can sign off this run.";
                return RedirectToAction(nameof(Run), new { id = model.RunId });
            }

            if (!ValidationRunAccessPolicy.CanCompleteReviewSignoff(role, review.CurrentUserEngagementRole, review.HasDataAnalystSignoff))
            {
                TempData["Error"] = "The assigned data analyst must sign off before this review can be completed.";
                return RedirectToAction(nameof(Run), new { id = model.RunId });
            }

            await _rule36.AddOrUpdateSignoffAsync(model.RunId, user!.Email!, model.Comment);
            await _audit.LogAsync(
                "signoff_validation_run",
                $"{review.CurrentUserEngagementRole} signed off run {model.RunId}",
                user.Id,
                user.Email);

            TempData["Success"] = "Signoff saved.";
            return RedirectToAction(nameof(Run), new { id = model.RunId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveSignoff(int runId)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            var review = await _rule36.GetSavedRunAsync(runId, user?.Email);
            if (review == null)
                return NotFound();

            var clientDetail = await _systemDb.GetClientDetailAsync(review.ClientId, user, role);
            if (clientDetail?.IsArchived == true)
            {
                TempData["Error"] = "Archived engagements are read-only. Signoff removal is disabled.";
                return RedirectToAction(nameof(Run), new { id = runId });
            }

            if (!await _systemDb.CanAccessClientResultsAsync(review.ClientId, user, role))
            {
                TempData["Error"] = "You do not have access to remove this signoff.";
                return RedirectToAction("Index", "Dashboard");
            }

            if (!review.IsCurrentRun)
            {
                TempData["Error"] = "History results are read-only. Signoff cannot be removed from a history run.";
                return RedirectToAction(nameof(Run), new { id = runId });
            }

            if (!review.CurrentUserHasSignedOff)
            {
                TempData["Error"] = "There is no signoff for your assigned engagement role to remove.";
                return RedirectToAction(nameof(Run), new { id = runId });
            }

            await _rule36.RemoveSignoffAsync(runId, user!.Email!);
            var workspace = await _rule36.GetCurrentWorkspaceStateAsync(review.ClientId, user?.Email);
            var redirectRunId = workspace?.RunId ?? runId;
            var preservedHistory = workspace?.RunId.HasValue == true && workspace.RunId.Value != runId;
            await _audit.LogAsync(
                "remove_validation_signoff",
                preservedHistory
                    ? $"{review.CurrentUserEngagementRole} removed signoff for Rule 36 run {runId}. Historical snapshot preserved; new current run {redirectRunId} created for continued review."
                    : $"{review.CurrentUserEngagementRole} removed signoff for Rule 36 run {runId}",
                user.Id,
                user.Email);

            TempData["Success"] = preservedHistory
                ? $"Signoff removed. Run #{runId} moved to history and Run #{redirectRunId} is now current."
                : "Signoff removed.";
            return RedirectToAction(nameof(Run), new { id = redirectRunId });
        }

        // ClosedXML builds the whole workbook in memory before it can be saved, and .xlsx caps
        // out at 1,048,576 rows per sheet regardless. Matches Excel's own actual maximum now the
        // Render service has 4GB (see Rule12Controller.ExcelExportRowSafetyLimit).
        private const int ExcelExportRowSafetyLimit = 1_048_576;

        [HttpGet]
        public async Task<IActionResult> DownloadSavedExcel(int runId)
        {
            var review = await LoadAuthorizedSavedRunAsync(runId, requireDownloadAccess: true);
            if (review == null)
                return RedirectToAction(nameof(Run), new { id = runId });

            var exportRequest = BuildRequestFromSummary(review.ClientId, review.Summary);
            var populationCount = await _rule36.GetPopulationCountAsync(exportRequest);
            if (populationCount > ExcelExportRowSafetyLimit)
            {
                TempData["Error"] = $"This engagement has {populationCount:N0} records, too many to export as one Excel file. Use the CSV download instead.";
                return RedirectToAction(nameof(Run), new { id = runId });
            }

            var resolved = await _rule36.GetExportSummaryAsync(exportRequest);
            var bytes = _export.ExportRule36Excel(resolved);
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Rule36_Deceased_Validation_Run_{runId}.xlsx");
        }

        [HttpGet]
        public async Task<IActionResult> DownloadSavedCsv(int runId)
        {
            var review = await LoadAuthorizedSavedRunAsync(runId, requireDownloadAccess: true);
            if (review == null)
                return RedirectToAction(nameof(Run), new { id = runId });

            var exportRequest = BuildRequestFromSummary(review.ClientId, review.Summary);
            Response.ContentType = "text/csv";
            Response.Headers.ContentDisposition = $"attachment; filename=\"Rule36_Validation_Results_Run_{runId}.csv\"";
            await _rule36.StreamCsvExportAsync(exportRequest, onlyExceptions: false, Response.Body);
            return new EmptyResult();
        }

        [HttpGet]
        public async Task<IActionResult> DownloadSavedExceptionsCsv(int runId)
        {
            var review = await LoadAuthorizedSavedRunAsync(runId, requireDownloadAccess: true);
            if (review == null)
                return RedirectToAction(nameof(Run), new { id = runId });

            var exportRequest = BuildRequestFromSummary(review.ClientId, review.Summary);
            Response.ContentType = "text/csv";
            Response.Headers.ContentDisposition = $"attachment; filename=\"Rule36_Deceased_Exceptions_Run_{runId}.csv\"";
            await _rule36.StreamCsvExportAsync(exportRequest, onlyExceptions: true, Response.Body);
            return new EmptyResult();
        }

        private static Rule36ValidationRequest BuildRequestFromSummary(int clientId, Rule36ValidationSummary summary) => new()
        {
            ClientId = clientId,
            StudTable = summary.StudTable,
            DeceasedTable = summary.DeceasedTable,
            StudColumn = summary.StudColumn,
            DeceasedColumn = summary.DeceasedColumn
        };

        [HttpGet]
        public async Task<IActionResult> DownloadSavedSql(int runId)
        {
            var review = await LoadAuthorizedSavedRunAsync(runId, requireDownloadAccess: true);
            if (review == null)
                return RedirectToAction(nameof(Run), new { id = runId });

            var request = new Rule36ValidationRequest
            {
                ClientId = review.ClientId,
                StudTable = review.Summary.StudTable,
                DeceasedTable = review.Summary.DeceasedTable,
                StudColumn = review.Summary.StudColumn,
                DeceasedColumn = review.Summary.DeceasedColumn
            };
            var bytes = _export.ExportSql(_rule36.GenerateSql(request));
            return File(bytes, "application/sql", $"Rule36_Deceased_Validation_Run_{runId}.sql");
        }

        [HttpPost]
        public async Task<IActionResult> DownloadExcel([FromBody] Rule36ValidationSummary summary)
        {
            try
            {
                var exportRequest = await ResolveExportRequestAsync(summary);

                var populationCount = await _rule36.GetPopulationCountAsync(exportRequest);
                if (populationCount > ExcelExportRowSafetyLimit)
                {
                    throw new InvalidOperationException(
                        $"This engagement has {populationCount:N0} records, too many to export as one Excel file.");
                }

                var resolved = await _rule36.GetExportSummaryAsync(exportRequest);
                var bytes = _export.ExportRule36Excel(resolved);
                return File(bytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"Rule36_Deceased_Validation_{Ts()}.xlsx");
            }
            catch (Exception ex)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Json(new { error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> GetExportInfo([FromBody] Rule36ValidationSummary summary)
        {
            try
            {
                var exportRequest = await ResolveExportRequestAsync(summary);
                var populationCount = await _rule36.GetPopulationCountAsync(exportRequest);
                return Json(new
                {
                    totalRecords = populationCount,
                    exceedsExcelLimit = populationCount > ExcelExportRowSafetyLimit,
                    excelLimit = ExcelExportRowSafetyLimit
                });
            }
            catch (Exception ex)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Json(new { error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DownloadCsv([FromBody] Rule36ValidationSummary summary)
        {
            try
            {
                var exportRequest = await ResolveExportRequestAsync(summary);

                Response.ContentType = "text/csv";
                Response.Headers.ContentDisposition = $"attachment; filename=\"Rule36_Validation_Results_{Ts()}.csv\"";
                await _rule36.StreamCsvExportAsync(exportRequest, onlyExceptions: false, Response.Body);
                return new EmptyResult();
            }
            catch (Exception ex)
            {
                if (!Response.HasStarted)
                {
                    Response.StatusCode = StatusCodes.Status400BadRequest;
                    return Json(new { error = ex.Message });
                }
                return new EmptyResult();
            }
        }

        [HttpPost]
        public async Task<IActionResult> DownloadExceptionsCsv([FromBody] Rule36ValidationSummary summary)
        {
            try
            {
                var exportRequest = await ResolveExportRequestAsync(summary);

                Response.ContentType = "text/csv";
                Response.Headers.ContentDisposition = $"attachment; filename=\"Rule36_Deceased_Exceptions_{Ts()}.csv\"";
                await _rule36.StreamCsvExportAsync(exportRequest, onlyExceptions: true, Response.Body);
                return new EmptyResult();
            }
            catch (Exception ex)
            {
                if (!Response.HasStarted)
                {
                    Response.StatusCode = StatusCodes.Status400BadRequest;
                    return Json(new { error = ex.Message });
                }
                return new EmptyResult();
            }
        }

        [HttpPost]
        public async Task<IActionResult> DownloadSql([FromBody] Rule36ValidationRequest request)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new
                {
                    success = false,
                    error = "Only the assigned data analyst can download the SQL script."
                });
            }

            var fileName = $"Rule36_Deceased_Validation_{Ts()}.sql";
            var bytes = _export.ExportSql(_rule36.GenerateSql(request));
            return File(bytes, "application/sql", fileName);
        }

        private async Task<Rule36RunReviewViewModel?> LoadAuthorizedSavedRunAsync(int runId, bool requireDownloadAccess)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            var review = await _rule36.GetSavedRunAsync(runId, user?.Email);
            if (review == null)
            {
                TempData["Error"] = "Saved validation run was not found.";
                return null;
            }

            if (!await _systemDb.CanAccessClientResultsAsync(review.ClientId, user, role))
            {
                TempData["Error"] = "You do not have access to this saved validation run.";
                return null;
            }

            if (!CanViewSavedRun(review, role))
            {
                TempData["Error"] = "Only analyst-signed validation results are available for review.";
                return null;
            }

            if (requireDownloadAccess && !CanDownloadSavedRun(review, role))
            {
                TempData["Error"] = "The assigned data analyst must sign off before other assigned users can download this run.";
                return null;
            }

            return review;
        }

        private static bool CanDownloadSavedRun(Rule36RunReviewViewModel review, string systemRole)
            => ValidationRunAccessPolicy.CanDownloadSignedResults(systemRole, review.CurrentUserEngagementRole, review.HasDataAnalystSignoff);

        private static bool CanViewSavedRun(Rule36RunReviewViewModel review, string systemRole)
            => ValidationRunAccessPolicy.CanViewSignedResults(systemRole, review.CurrentUserEngagementRole, review.HasDataAnalystSignoff);

        private static bool IsResultsOnlyRole(string role) =>
            string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Trainee", StringComparison.OrdinalIgnoreCase);

        private async Task<string> GetCurrentSystemRoleAsync(ApplicationUser? user)
        {
            var systemRole = await _systemDb.GetSystemRoleAsync(user);
            if (!string.IsNullOrWhiteSpace(systemRole))
                return systemRole!;

            var roles = user != null ? await _users.GetRolesAsync(user) : new List<string>();
            return roles.FirstOrDefault() ?? "";
        }

        private async Task<bool> CanEditWorkspaceAsync(int clientId, ApplicationUser? user, string role)
        {
            if (user == null)
                return false;

            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase))
                return false;

            if (clientId <= 0)
                return false;

            if (!await _systemDb.CanAccessClientResultsAsync(clientId, user, role))
                return false;

            var engagementRole = await _systemDb.GetEngagementRoleAsync(clientId, user, role);
            return ValidationRunAccessPolicy.IsAssignedDataAnalyst(engagementRole);
        }

        private async Task<bool> CanSignWorkspaceAsync(int clientId, ApplicationUser? user, string role)
        {
            if (user == null)
                return false;

            if (clientId <= 0)
                return false;

            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!await _systemDb.CanAccessClientResultsAsync(clientId, user, role))
                return false;

            var engagementRole = await _systemDb.GetEngagementRoleAsync(clientId, user, role);
            return string.Equals(engagementRole, role, StringComparison.OrdinalIgnoreCase) &&
                   ValidationRunAccessPolicy.CanAssignedUserSignOff(engagementRole);
        }

        private static bool CanViewWorkspaceResults(string role, Rule36WorkspaceStateViewModel? workspace)
        {
            if (workspace == null)
                return false;

            return ValidationRunAccessPolicy.CanViewSignedResults(role, workspace.CurrentUserEngagementRole, workspace.HasDataAnalystSignoff);
        }

        private async Task<Rule36ValidationRequest> ResolveExportRequestAsync(Rule36ValidationSummary summary)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (summary.ClientId <= 0 || !await _systemDb.CanAccessClientResultsAsync(summary.ClientId, user, role))
                throw new InvalidOperationException("You cannot access this engagement.");

            if (string.IsNullOrWhiteSpace(summary.StudTable) || string.IsNullOrWhiteSpace(summary.DeceasedTable))
                throw new InvalidOperationException("Run Rule 36 first before downloading results.");

            return BuildRequestFromSummary(summary.ClientId, summary);
        }

        private async Task<Rule36ValidationSummary> ResolveExportSummaryAsync(Rule36ValidationSummary summary)
        {
            var user = await _users.GetUserAsync(User);

            if (summary.SavedRunId is int savedRunId && savedRunId > 0)
            {
                var review = await _rule36.GetSavedRunAsync(savedRunId, user?.Email);
                if (review?.Summary != null)
                    return review.Summary;
            }

            if (summary.ClientId > 0)
            {
                var workspace = await _rule36.GetCurrentWorkspaceStateAsync(summary.ClientId, user?.Email);
                if (workspace?.RunId is int workspaceRunId && workspaceRunId > 0)
                {
                    var review = await _rule36.GetSavedRunAsync(workspaceRunId, user?.Email);
                    if (review?.Summary != null)
                        return review.Summary;
                }
            }

            return summary;
        }

        private async Task<object> RequireDataAnalystAsync<T>(Func<Task<T>> action)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    success = false,
                    error = "Only the assigned data analyst can configure or run Rule 36."
                };
            }

            return await action();
        }

        private object RequireDataAnalystResult(Func<SqlResult> factory)
        {
            var user = _users.GetUserAsync(User).GetAwaiter().GetResult();
            var role = GetCurrentSystemRoleAsync(user).GetAwaiter().GetResult();
            if (!string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    success = false,
                    error = "Only the assigned data analyst can generate the SQL script."
                };
            }

            return factory();
        }

        private static string Ts() => DateTime.Now.ToString("yyyyMMdd_HHmmss");

    }
}

