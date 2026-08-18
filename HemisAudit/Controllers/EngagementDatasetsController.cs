using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using HemisAudit.Models;
using HemisAudit.Services;
using HemisAudit.ViewModels;

namespace HemisAudit.Controllers
{
    // A data analyst's per-engagement CSV/Excel upload + table browser/editor, backed by
    // Supabase Postgres — the replacement for requiring a live SQL Server connection to
    // the audited institution just to bring data into the system. Creating the engagement's
    // "database" and uploading/editing/deleting data is restricted to the DataAnalyst
    // assigned to the engagement; other assigned roles can still browse.
    [Authorize]
    public class EngagementDatasetsController : Controller
    {
        private readonly UserManager<ApplicationUser> _users;
        private readonly ISystemDatabaseService _systemDb;
        private readonly IEngagementDatasetService _datasets;
        private readonly IAuditLogService _audit;

        public EngagementDatasetsController(UserManager<ApplicationUser> users, ISystemDatabaseService systemDb,
            IEngagementDatasetService datasets, IAuditLogService audit)
        {
            _users = users; _systemDb = systemDb; _datasets = datasets; _audit = audit;
        }

        private async Task<string> GetCurrentSystemRoleAsync(ApplicationUser? user)
        {
            var systemRole = await _systemDb.GetSystemRoleAsync(user);
            if (!string.IsNullOrWhiteSpace(systemRole))
                return systemRole!;

            var roles = user != null ? await _users.GetRolesAsync(user) : new List<string>();
            return roles.FirstOrDefault() ?? "";
        }

        // Only the data analyst assigned to this engagement may create its database or
        // upload/edit/delete data. Everyone else with module access can browse read-only.
        private async Task<bool> IsDataAnalystForEngagementAsync(int clientId, ApplicationUser? user, string role)
        {
            var engagementRole = await _systemDb.GetEngagementRoleAsync(clientId, user, role);
            return string.Equals(engagementRole, "DataAnalyst", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<IActionResult> Index(int clientId)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await _systemDb.CanAccessClientModuleAsync(clientId, user, role))
            {
                TempData["Error"] = "You cannot access this engagement.";
                return RedirectToAction("Index", "Dashboard");
            }

            var canEdit = await IsDataAnalystForEngagementAsync(clientId, user, role);
            ViewBag.ClientId = clientId;
            ViewBag.CanEdit = canEdit;

            var database = await _datasets.GetDatabaseAsync(clientId);
            ViewBag.Database = database;

            var tables = database == null ? new List<DatasetTableInfo>() : await _datasets.ListTablesAsync(clientId);
            return View(tables);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateDatabase(int clientId, string databaseName)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await IsDataAnalystForEngagementAsync(clientId, user, role) || !await _systemDb.CanAccessClientModuleAsync(clientId, user, role))
            {
                TempData["Error"] = "Only the data analyst assigned to this engagement can create its database.";
                return RedirectToAction(nameof(Index), new { clientId });
            }

            try
            {
                var db = await _datasets.CreateDatabaseAsync(clientId, databaseName, user!);
                await _audit.LogAsync("create_engagement_database",
                    $"Created database '{db.DatabaseName}' for engagement {clientId}", user?.Id, user?.Email);
                TempData["Success"] = $"Database '{db.DatabaseName}' created.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Index), new { clientId });
        }

        // ── Upload wizard: Start (pick file) -> Preview (edit columns) -> Results ──────
        [HttpGet]
        public async Task<IActionResult> UploadStart(int clientId)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await IsDataAnalystForEngagementAsync(clientId, user, role) || !await _systemDb.CanAccessClientModuleAsync(clientId, user, role))
            {
                TempData["Error"] = "You cannot upload data for this engagement.";
                return RedirectToAction(nameof(Index), new { clientId });
            }

            var database = await _datasets.GetDatabaseAsync(clientId);
            if (database == null)
            {
                TempData["Error"] = "Create a database for this engagement before uploading a file.";
                return RedirectToAction(nameof(Index), new { clientId });
            }

            ViewBag.ClientId = clientId;
            ViewBag.Database = database;
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        [DisableRequestSizeLimit] // no cap by design — see EngagementDatasetService
        public async Task<IActionResult> UploadStage(int clientId, IFormFile file, string? tableName)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await IsDataAnalystForEngagementAsync(clientId, user, role) || !await _systemDb.CanAccessClientModuleAsync(clientId, user, role))
            {
                TempData["Error"] = "You cannot upload data for this engagement.";
                return RedirectToAction(nameof(Index), new { clientId });
            }

            try
            {
                var staging = await _datasets.StageUploadAsync(clientId, file, tableName);
                return RedirectToAction(nameof(UploadPreview), new { clientId, token = staging.Token });
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(UploadStart), new { clientId });
            }
        }

        [HttpGet]
        public async Task<IActionResult> UploadPreview(int clientId, string token)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await IsDataAnalystForEngagementAsync(clientId, user, role) || !await _systemDb.CanAccessClientModuleAsync(clientId, user, role))
            {
                TempData["Error"] = "You cannot upload data for this engagement.";
                return RedirectToAction(nameof(Index), new { clientId });
            }

            try
            {
                var staging = await _datasets.GetStagingAsync(token);
                if (staging.ClientId != clientId)
                {
                    TempData["Error"] = "This upload session doesn't belong to this engagement.";
                    return RedirectToAction(nameof(Index), new { clientId });
                }

                ViewBag.ClientId = clientId;
                return View(staging);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(UploadStart), new { clientId });
            }
        }

        [HttpGet]
        public async Task<IActionResult> UploadModifyColumns(int clientId, string token)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await IsDataAnalystForEngagementAsync(clientId, user, role) || !await _systemDb.CanAccessClientModuleAsync(clientId, user, role))
            {
                TempData["Error"] = "You cannot upload data for this engagement.";
                return RedirectToAction(nameof(Index), new { clientId });
            }

            try
            {
                var staging = await _datasets.GetStagingAsync(token);
                if (staging.ClientId != clientId)
                {
                    TempData["Error"] = "This upload session doesn't belong to this engagement.";
                    return RedirectToAction(nameof(Index), new { clientId });
                }

                ViewBag.ClientId = clientId;
                return View(staging);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(UploadStart), new { clientId });
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadCommit(int clientId, string token, string tableName,
            [FromForm] List<string> columnName, [FromForm] List<string> dataType, [FromForm] List<string> originalHeader,
            [FromForm] List<string>? allowNulls)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await IsDataAnalystForEngagementAsync(clientId, user, role) || !await _systemDb.CanAccessClientModuleAsync(clientId, user, role))
            {
                TempData["Error"] = "You cannot upload data for this engagement.";
                return RedirectToAction(nameof(Index), new { clientId });
            }

            var allowNullsSet = (allowNulls ?? new List<string>()).ToHashSet(StringComparer.Ordinal);
            var columns = originalHeader.Select((header, i) => new DatasetStagedColumn
            {
                OriginalHeader = header,
                ColumnName = i < columnName.Count ? columnName[i] : header,
                DataType = i < dataType.Count ? dataType[i] : "Text",
                AllowNulls = allowNullsSet.Contains(header)
            }).ToList();

            int jobId;
            try
            {
                await _datasets.UpdateStagingColumnsAsync(token, tableName, columns);
                jobId = await _datasets.StartCommitAsync(token, user!);
                await _audit.LogAsync("upload_dataset_started",
                    $"Started importing '{tableName}' for engagement {clientId} (job {jobId})",
                    user?.Id, user?.Email);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(UploadPreview), new { clientId, token });
            }

            return RedirectToAction(nameof(UploadProgress), new { clientId, jobId });
        }

        [HttpGet]
        public IActionResult UploadProgress(int clientId, int jobId)
        {
            ViewBag.ClientId = clientId;
            ViewBag.JobId = jobId;
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> UploadJobStatus(int clientId, int jobId)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await _systemDb.CanAccessClientModuleAsync(clientId, user, role))
                return Forbid();

            var status = await _datasets.GetUploadJobStatusAsync(jobId, clientId);
            if (status == null)
                return NotFound();

            return Json(status);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadCancel(int clientId, string token)
        {
            await _datasets.CancelStagingAsync(token);
            TempData["Success"] = "Upload cancelled.";
            return RedirectToAction(nameof(Index), new { clientId });
        }

        public async Task<IActionResult> Table(int clientId, string tableName, int page = 1, Dictionary<string, string?>? filters = null)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await _systemDb.CanAccessClientModuleAsync(clientId, user, role))
            {
                TempData["Error"] = "You cannot access this engagement.";
                return RedirectToAction("Index", "Dashboard");
            }

            var canEdit = await IsDataAnalystForEngagementAsync(clientId, user, role);

            DatasetRowsPage rows;
            try
            {
                rows = await _datasets.ListRowsAsync(clientId, tableName, page, pageSize: 50, filters: filters);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(Index), new { clientId });
            }

            ViewBag.ClientId = clientId;
            ViewBag.TableName = tableName;
            ViewBag.CanEdit = canEdit;
            return View(rows);
        }

        [HttpGet]
        public async Task<IActionResult> DownloadTable(int clientId, string tableName)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);

            if (!await _systemDb.CanAccessClientModuleAsync(clientId, user, role))
            {
                TempData["Error"] = "You cannot access this engagement.";
                return RedirectToAction("Index", "Dashboard");
            }

            Response.ContentType = "text/csv";
            Response.Headers["Content-Disposition"] = $"attachment; filename=\"{tableName}.csv\"";
            try
            {
                await _datasets.ExportTableCsvAsync(clientId, tableName, Response.Body);
            }
            catch (Exception ex)
            {
                await _audit.LogAsync("download_dataset_error", $"Failed to export '{tableName}' for engagement {clientId}: {ex.Message}", user?.Id, user?.Email);
                throw;
            }

            await _audit.LogAsync("download_dataset", $"Downloaded '{tableName}' for engagement {clientId}", user?.Id, user?.Email);
            return new EmptyResult();
        }

        [HttpGet]
        public async Task<IActionResult> EditRow(int clientId, string tableName, long rowId)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!await IsDataAnalystForEngagementAsync(clientId, user, role))
            {
                TempData["Error"] = "Only the assigned data analyst can edit this data.";
                return RedirectToAction(nameof(Table), new { clientId, tableName });
            }

            var row = await _datasets.GetRowAsync(clientId, tableName, rowId);
            if (row == null)
            {
                TempData["Error"] = "Row not found.";
                return RedirectToAction(nameof(Table), new { clientId, tableName });
            }

            ViewBag.ClientId = clientId;
            ViewBag.TableName = tableName;
            ViewBag.RowId = rowId;
            return View(row);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> EditRow(int clientId, string tableName, long rowId, [FromForm] Dictionary<string, string?> values)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!await IsDataAnalystForEngagementAsync(clientId, user, role))
            {
                TempData["Error"] = "Only the assigned data analyst can edit this data.";
                return RedirectToAction(nameof(Table), new { clientId, tableName });
            }

            values.Remove("__RequestVerificationToken");
            try
            {
                await _datasets.UpdateRowAsync(clientId, tableName, rowId, values);
                TempData["Success"] = "Row updated.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(EditRow), new { clientId, tableName, rowId });
            }

            return RedirectToAction(nameof(Table), new { clientId, tableName });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRow(int clientId, string tableName, long rowId)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!await IsDataAnalystForEngagementAsync(clientId, user, role))
            {
                TempData["Error"] = "Only the assigned data analyst can delete rows.";
                return RedirectToAction(nameof(Table), new { clientId, tableName });
            }

            await _datasets.DeleteRowAsync(clientId, tableName, rowId);
            TempData["Success"] = "Row deleted.";
            return RedirectToAction(nameof(Table), new { clientId, tableName });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTable(int clientId, string tableName)
        {
            var user = await _users.GetUserAsync(User);
            var role = await GetCurrentSystemRoleAsync(user);
            if (!await IsDataAnalystForEngagementAsync(clientId, user, role))
            {
                TempData["Error"] = "Only the assigned data analyst can delete tables.";
                return RedirectToAction(nameof(Index), new { clientId });
            }

            await _datasets.DeleteTableAsync(clientId, tableName);
            await _audit.LogAsync("delete_dataset_table", $"Deleted table '{tableName}' for engagement {clientId}", user?.Id, user?.Email);
            TempData["Success"] = $"Table '{tableName}' deleted.";
            return RedirectToAction(nameof(Index), new { clientId });
        }
    }
}
