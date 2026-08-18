using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HemisAudit.Data;
using HemisAudit.Models;
using HemisAudit.Services;
using HemisAudit.ViewModels;

namespace HemisAudit.Controllers
{
    // Mamishi IT Audit Solutions (the platform owner) manages leased audit firms here —
    // grant/revoke access, seat count, engagement limit, expiry. Separate from AdminController,
    // which is scoped to a single firm's own users/engagements.
    [Authorize(Roles = "ServiceProvider")]
    public class FirmsController : Controller
    {
        private readonly UserManager<ApplicationUser> _users;
        private readonly ApplicationDbContext _db;
        private readonly IAuditLogService _audit;
        private readonly IPasswordPolicyService _passwordPolicy;
        private readonly ISystemDatabaseService _systemDb;

        public FirmsController(UserManager<ApplicationUser> users, ApplicationDbContext db,
            IAuditLogService audit, IPasswordPolicyService passwordPolicy, ISystemDatabaseService systemDb)
        {
            _users = users; _db = db; _audit = audit; _passwordPolicy = passwordPolicy; _systemDb = systemDb;
        }

        // A firm that has a ServiceProvider-role user on it is the platform operator's own
        // organization (e.g. Mamishi IT Audit Solutions running its own engagements), not a
        // leased client. The service provider manages leased clients here — never itself —
        // so this console must never be able to list, suspend, or otherwise touch that firm.
        private async Task<bool> IsServiceProviderOwnedFirmAsync(int firmId)
        {
            var ownedFirmIds = await GetServiceProviderOwnedFirmIdsAsync();
            return ownedFirmIds.Contains(firmId);
        }

        // Single set-based join instead of a per-user FindByIdAsync/GetRolesAsync round trip —
        // matters when this is evaluated per firm on a list page (was previously O(n) DB calls).
        private async Task<HashSet<int>> GetServiceProviderOwnedFirmIdsAsync()
        {
            var spRoleId = await _db.Roles.Where(r => r.Name == "ServiceProvider").Select(r => r.Id).FirstOrDefaultAsync();
            if (spRoleId == null) return new HashSet<int>();

            var firmIds = await (from ur in _db.UserRoles
                                  join u in _db.Users on ur.UserId equals u.Id
                                  where ur.RoleId == spRoleId && u.FirmId != null
                                  select u.FirmId!.Value)
                                  .Distinct()
                                  .ToListAsync();
            return firmIds.ToHashSet();
        }

        // ── Firm List ───────────────────────────────────────────────────────────
        public async Task<IActionResult> Index(string? q)
        {
            var query = _db.Firms.Include(f => f.License).Where(f => !f.IsDeleted).AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(f => EF.Functions.ILike(f.Name, $"%{q}%") || EF.Functions.ILike(f.FirmCode, $"%{q}%"));

            // Sequential, not Task.WhenAll — these all share one DbContext, which EF Core
            // does not allow concurrent operations against. Still just 4 round trips total
            // (was 1 + up to ~5 per firm before), so this is the fix, not a compromise.
            var firms = await query.OrderBy(f => f.Name).ToListAsync();
            var ownedFirmIds = await GetServiceProviderOwnedFirmIdsAsync();
            var seatsUsedByFirm = await _users.Users
                .Where(u => u.IsActive && u.FirmId != null)
                .GroupBy(u => u.FirmId!.Value)
                .Select(g => new { FirmId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.FirmId, x => x.Count);
            var recycleBinCount = await _db.Firms.CountAsync(f => f.IsDeleted);

            var list = new List<FirmListItemViewModel>();
            foreach (var f in firms)
            {
                if (ownedFirmIds.Contains(f.Id))
                    continue;

                list.Add(new FirmListItemViewModel
                {
                    Id = f.Id,
                    Name = f.Name,
                    FirmCode = f.FirmCode,
                    CreatedAt = f.CreatedAt,
                    SeatCount = f.License?.SeatCount ?? 0,
                    SeatsUsed = seatsUsedByFirm.TryGetValue(f.Id, out var used) ? used : 0,
                    EngagementLimit = f.License?.EngagementLimit ?? 0,
                    ExpiresAt = f.License?.ExpiresAt ?? DateTime.MinValue,
                    Status = f.License?.Status ?? "Unlicensed"
                });
            }

            var now = DateTime.UtcNow;
            ViewBag.TotalFirms = list.Count;
            ViewBag.ActiveFirms = list.Count(f => f.Status == "Active" && f.ExpiresAt >= now);
            ViewBag.SuspendedOrExpired = list.Count(f => f.Status != "Active" || f.ExpiresAt < now);
            ViewBag.TotalSeatsUsed = list.Sum(f => f.SeatsUsed);
            ViewBag.ExpiringSoon = list.Count(f => f.ExpiresAt >= now && f.ExpiresAt <= now.AddDays(30));
            ViewBag.SearchQuery = q;
            ViewBag.RecycleBinCount = recycleBinCount;

            return View(list);
        }

        // ── Recycle Bin ─────────────────────────────────────────────────────────
        public async Task<IActionResult> RecycleBin(string? q)
        {
            var query = _db.Firms.Include(f => f.License).Where(f => f.IsDeleted).AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(f => EF.Functions.ILike(f.Name, $"%{q}%") || EF.Functions.ILike(f.FirmCode, $"%{q}%"));

            var firms = await query.OrderByDescending(f => f.DeletedAt).ToListAsync();

            var deleterIds = firms.Where(f => f.DeletedByUserId != null).Select(f => f.DeletedByUserId!).Distinct().ToList();
            var deleterEmailsById = await _users.Users
                .Where(u => deleterIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Email ?? "");

            var list = new List<FirmRecycleBinItemViewModel>();
            foreach (var f in firms)
            {
                var deletedByName = f.DeletedByUserId != null && deleterEmailsById.TryGetValue(f.DeletedByUserId, out var email)
                    ? email
                    : "Unknown";
                list.Add(new FirmRecycleBinItemViewModel
                {
                    Id = f.Id,
                    Name = f.Name,
                    FirmCode = f.FirmCode,
                    DeletedAt = f.DeletedAt,
                    DeletedByName = deletedByName,
                    SeatCount = f.License?.SeatCount ?? 0,
                    EngagementLimit = f.License?.EngagementLimit ?? 0
                });
            }

            ViewBag.SearchQuery = q;
            return View(list);
        }

        // ── Create Firm GET ────────────────────────────────────────────────────
        [HttpGet]
        public IActionResult Create() => View(new CreateFirmViewModel());

        // ── Create Firm POST (firm + license + first admin, in one step) ──────
        // The primary contact becomes the firm's first Admin login. We generate the
        // password ourselves (rather than asking the service provider to invent one for
        // someone else) and show it once on success.
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreateFirmViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            if (await _db.Firms.AnyAsync(f => f.Name == model.FirmName))
            {
                ModelState.AddModelError(nameof(model.FirmName), "A firm with this name already exists.");
                return View(model);
            }

            if (await _users.FindByEmailAsync(model.PrimaryContactEmail) != null)
            {
                ModelState.AddModelError(nameof(model.PrimaryContactEmail), "A user with this email already exists.");
                return View(model);
            }

            var (firstName, lastName) = SplitName(model.PrimaryContactName);
            var admin = new ApplicationUser
            {
                UserName = model.PrimaryContactEmail,
                Email = model.PrimaryContactEmail,
                FirstName = firstName,
                LastName = lastName,
                EmailConfirmed = true,
                IsActive = true,
                PasswordSetDate = DateTime.UtcNow,
                MustChangePassword = true
            };

            var generatedPassword = GenerateTemporaryPassword();
            var policyErrors = _passwordPolicy.ValidatePassword(admin, generatedPassword);
            if (policyErrors.Count > 0)
            {
                // Extremely unlikely given the generator's charset, but never silently proceed.
                ModelState.AddModelError("", "Could not generate a compliant password — please try again.");
                return View(model);
            }

            var serviceProvider = await _users.GetUserAsync(User);

            var firm = new Firm
            {
                Name = model.FirmName,
                CreatedByUserId = serviceProvider?.Id,
                PrimaryContactName = model.PrimaryContactName,
                PrimaryContactEmail = model.PrimaryContactEmail,
                BillingContactName = model.BillingContactName,
                BillingContactEmail = model.BillingContactEmail,
                AdminContactName = model.AdminContactName,
                AdminContactEmail = model.AdminContactEmail
            };
            _db.Firms.Add(firm);
            await _db.SaveChangesAsync();

            // Sequential, unique, human-shareable code derived from the firm's own id —
            // this is what the firm's users enter on the Client login tab.
            firm.FirmCode = (100000 + firm.Id).ToString();
            await _db.SaveChangesAsync();

            var license = new FirmLicense
            {
                FirmId = firm.Id,
                SeatCount = model.SeatCount,
                EngagementLimit = model.EngagementLimit,
                ExpiresAt = DateTime.SpecifyKind(model.ExpiresAt, DateTimeKind.Utc),
                Status = "Active",
                UpdatedByUserId = serviceProvider?.Id
            };
            _db.FirmLicenses.Add(license);
            await _db.SaveChangesAsync();

            admin.FirmId = firm.Id;
            var identityUserCreated = false;

            try
            {
                var result = await _users.CreateAsync(admin, generatedPassword);
                if (!result.Succeeded)
                {
                    foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
                    await RollBackFirmAsync(firm, license);
                    return View(model);
                }

                identityUserCreated = true;

                var addRoleResult = await _users.AddToRoleAsync(admin, "Admin");
                if (!addRoleResult.Succeeded)
                {
                    foreach (var e in addRoleResult.Errors) ModelState.AddModelError("", e.Description);
                    await _users.DeleteAsync(admin);
                    await RollBackFirmAsync(firm, license);
                    return View(model);
                }

                admin.PasswordHistory = _passwordPolicy.BuildPasswordHistory(null, admin.PasswordHash ?? string.Empty);
                await _users.UpdateAsync(admin);

                await _systemDb.EnsureUserMirrorAsync(admin, "Admin");
            }
            catch (Exception ex)
            {
                if (identityUserCreated)
                    await _users.DeleteAsync(admin);
                await RollBackFirmAsync(firm, license);

                ModelState.AddModelError("", $"The firm admin could not be created: {ex.Message}");
                return View(model);
            }

            await _audit.LogAsync("create_firm",
                $"Created firm '{firm.Name}' (seats {model.SeatCount}, engagement limit {model.EngagementLimit}) with admin {admin.Email}",
                serviceProvider?.Id, serviceProvider?.Email);

            TempData["Success"] = $"Firm '{firm.Name}' created. Client code: {firm.FirmCode}.";
            TempData["GeneratedCredentialsEmail"] = admin.Email;
            TempData["GeneratedCredentialsPassword"] = generatedPassword;
            return RedirectToAction(nameof(Detail), new { id = firm.Id });
        }

        private static (string FirstName, string LastName) SplitName(string fullName)
        {
            var trimmed = (fullName ?? "").Trim();
            var spaceIndex = trimmed.IndexOf(' ');
            return spaceIndex < 0
                ? (trimmed, "")
                : (trimmed.Substring(0, spaceIndex), trimmed.Substring(spaceIndex + 1).Trim());
        }

        private static string GenerateTemporaryPassword()
        {
            const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string lower = "abcdefghijkmnopqrstuvwxyz";
            const string digits = "23456789";
            const string special = "!@#$%&*";
            var rng = Random.Shared;

            var chars = new[]
            {
                upper[rng.Next(upper.Length)],
                lower[rng.Next(lower.Length)],
                digits[rng.Next(digits.Length)],
                special[rng.Next(special.Length)]
            }.ToList();

            const string all = upper + lower + digits + special;
            for (var i = 0; i < 8; i++)
                chars.Add(all[rng.Next(all.Length)]);

            return new string(chars.OrderBy(_ => rng.Next()).ToArray());
        }

        private async Task RollBackFirmAsync(Firm firm, FirmLicense license)
        {
            _db.FirmLicenses.Remove(license);
            _db.Firms.Remove(firm);
            await _db.SaveChangesAsync();
        }

        // ── Firm Detail (users + license editor) ───────────────────────────────
        public async Task<IActionResult> Detail(int id)
        {
            var firm = await _db.Firms.Include(f => f.License).FirstOrDefaultAsync(f => f.Id == id);
            if (firm == null) return NotFound();

            if (firm.IsDeleted)
            {
                TempData["Error"] = $"'{firm.Name}' is in the recycle bin.";
                return RedirectToAction(nameof(RecycleBin));
            }

            if (await IsServiceProviderOwnedFirmAsync(id))
            {
                TempData["Error"] = "This is your own organization, not a leased client — it isn't managed from here.";
                return RedirectToAction(nameof(Index));
            }

            var firmUsers = await _users.Users.Where(u => u.FirmId == id).OrderBy(u => u.LastName).ToListAsync();
            var rows = new List<FirmUserRow>();
            foreach (var u in firmUsers)
            {
                var roles = await _users.GetRolesAsync(u);
                rows.Add(new FirmUserRow
                {
                    Id = u.Id,
                    FullName = u.FullName,
                    Email = u.Email ?? "",
                    Role = roles.FirstOrDefault() ?? "",
                    IsActive = u.IsActive
                });
            }

            return View(new FirmDetailViewModel
            {
                Id = firm.Id,
                Name = firm.Name,
                FirmCode = firm.FirmCode,
                CreatedAt = firm.CreatedAt,
                SeatCount = firm.License?.SeatCount ?? 0,
                SeatsUsed = rows.Count(r => r.IsActive),
                EngagementLimit = firm.License?.EngagementLimit ?? 0,
                ExpiresAt = firm.License?.ExpiresAt ?? DateTime.MinValue,
                Status = firm.License?.Status ?? "Unlicensed",
                Users = rows,
                PrimaryContactName = firm.PrimaryContactName,
                PrimaryContactEmail = firm.PrimaryContactEmail,
                BillingContactName = firm.BillingContactName,
                BillingContactEmail = firm.BillingContactEmail,
                AdminContactName = firm.AdminContactName,
                AdminContactEmail = firm.AdminContactEmail
            });
        }

        // ── Edit License ────────────────────────────────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> EditLicense(EditFirmLicenseViewModel model)
        {
            if (await IsServiceProviderOwnedFirmAsync(model.FirmId))
            {
                TempData["Error"] = "This is your own organization, not a leased client — its license isn't managed from here.";
                return RedirectToAction(nameof(Index));
            }

            var license = await _db.FirmLicenses.FirstOrDefaultAsync(l => l.FirmId == model.FirmId);
            if (license == null) return NotFound();

            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Could not update the license — check the values and try again.";
                return RedirectToAction(nameof(Detail), new { id = model.FirmId });
            }

            var serviceProvider = await _users.GetUserAsync(User);

            license.SeatCount = model.SeatCount;
            license.EngagementLimit = model.EngagementLimit;
            license.ExpiresAt = DateTime.SpecifyKind(model.ExpiresAt, DateTimeKind.Utc);
            license.UpdatedAt = DateTime.UtcNow;
            license.UpdatedByUserId = serviceProvider?.Id;
            await _db.SaveChangesAsync();

            await _audit.LogAsync("edit_firm_license",
                $"Updated license for firm {model.FirmId}: seats={model.SeatCount}, engagements={model.EngagementLimit}, expires={model.ExpiresAt:yyyy-MM-dd}",
                serviceProvider?.Id, serviceProvider?.Email);

            TempData["Success"] = "License updated.";
            return RedirectToAction(nameof(Detail), new { id = model.FirmId });
        }

        // ── Suspend / Reactivate ────────────────────────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Suspend(int id)
        {
            if (await IsServiceProviderOwnedFirmAsync(id))
            {
                TempData["Error"] = "You cannot suspend your own organization's access.";
                return RedirectToAction(nameof(Index));
            }

            await SetLicenseStatusAsync(id, "Suspended");
            TempData["Success"] = "Firm access suspended.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reactivate(int id)
        {
            await SetLicenseStatusAsync(id, "Active");
            TempData["Success"] = "Firm access reactivated.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        private async Task SetLicenseStatusAsync(int firmId, string status)
        {
            var license = await _db.FirmLicenses.FirstOrDefaultAsync(l => l.FirmId == firmId);
            if (license == null) return;

            var serviceProvider = await _users.GetUserAsync(User);
            license.Status = status;
            license.UpdatedAt = DateTime.UtcNow;
            license.UpdatedByUserId = serviceProvider?.Id;
            await _db.SaveChangesAsync();

            await _audit.LogAsync("firm_status_change", $"Firm {firmId} status set to {status}",
                serviceProvider?.Id, serviceProvider?.Email);
        }

        // ── Move to Recycle Bin ─────────────────────────────────────────────────
        // Reversible: hides the firm from the active list and blocks its users' access
        // (FirmAccessFilter), but keeps everything intact until it's permanently deleted.
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SoftDelete(int id, bool fromList = false)
        {
            if (await IsServiceProviderOwnedFirmAsync(id))
            {
                TempData["Error"] = "You cannot delete your own organization.";
                return RedirectToAction(nameof(Index));
            }

            var firm = await _db.Firms.FirstOrDefaultAsync(f => f.Id == id);
            if (firm == null) return NotFound();

            var serviceProvider = await _users.GetUserAsync(User);
            firm.IsDeleted = true;
            firm.DeletedAt = DateTime.UtcNow;
            firm.DeletedByUserId = serviceProvider?.Id;
            await _db.SaveChangesAsync();

            await _audit.LogAsync("recycle_firm", $"Moved firm '{firm.Name}' (code {firm.FirmCode}) to the recycle bin",
                serviceProvider?.Id, serviceProvider?.Email);

            TempData["Success"] = $"'{firm.Name}' was moved to the recycle bin. You can restore it anytime before it's permanently deleted.";
            return RedirectToAction(nameof(Index));
        }

        // ── Restore from Recycle Bin ────────────────────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Restore(int id)
        {
            var firm = await _db.Firms.FirstOrDefaultAsync(f => f.Id == id);
            if (firm == null) return NotFound();

            var serviceProvider = await _users.GetUserAsync(User);
            firm.IsDeleted = false;
            firm.DeletedAt = null;
            firm.DeletedByUserId = null;
            await _db.SaveChangesAsync();

            await _audit.LogAsync("restore_firm", $"Restored firm '{firm.Name}' (code {firm.FirmCode}) from the recycle bin",
                serviceProvider?.Id, serviceProvider?.Email);

            TempData["Success"] = $"'{firm.Name}' was restored.";
            return RedirectToAction(nameof(RecycleBin));
        }

        // ── Delete Firm Permanently ─────────────────────────────────────────────
        // Irreversible: removes the firm, its license, and every one of its users.
        // Refuses if the firm still has engagements — those carry validation history and
        // must be deleted/reassigned first so this can't silently orphan audit data.
        // Reachable straight from the firm list/Danger Zone, or from the recycle bin
        // once a firm has already been moved there — returnTo keeps failures on the
        // page the service provider started from.
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id, string returnTo = "detail")
        {
            IActionResult BackOnFailure() => returnTo switch
            {
                "list" => RedirectToAction(nameof(Index)),
                "bin" => RedirectToAction(nameof(RecycleBin)),
                _ => RedirectToAction(nameof(Detail), new { id })
            };

            if (await IsServiceProviderOwnedFirmAsync(id))
            {
                TempData["Error"] = "You cannot delete your own organization.";
                return RedirectToAction(nameof(Index));
            }

            var firm = await _db.Firms.Include(f => f.License).FirstOrDefaultAsync(f => f.Id == id);
            if (firm == null) return NotFound();

            var engagementCount = await _systemDb.GetEngagementCountForFirmAsync(id);
            if (engagementCount > 0)
            {
                TempData["Error"] = $"'{firm.Name}' still has {engagementCount} engagement(s). Delete or reassign them before deleting the firm.";
                return BackOnFailure();
            }

            var serviceProvider = await _users.GetUserAsync(User);
            var firmUsers = await _users.Users.Where(u => u.FirmId == id).ToListAsync();

            foreach (var firmUser in firmUsers)
            {
                firmUser.IsActive = false;
                await _users.UpdateAsync(firmUser);
                await _systemDb.DeleteUserMirrorAsync(firmUser, serviceProvider!, "ServiceProvider");

                var deleteResult = await _users.DeleteAsync(firmUser);
                if (!deleteResult.Succeeded)
                {
                    TempData["Error"] = $"Could not delete user {firmUser.Email}: {string.Join(", ", deleteResult.Errors.Select(e => e.Description))}";
                    return BackOnFailure();
                }
            }

            if (firm.License != null)
                _db.FirmLicenses.Remove(firm.License);
            _db.Firms.Remove(firm);
            await _db.SaveChangesAsync();

            await _audit.LogAsync("delete_firm",
                $"Permanently deleted firm '{firm.Name}' (code {firm.FirmCode}) and {firmUsers.Count} user(s)",
                serviceProvider?.Id, serviceProvider?.Email);

            TempData["Success"] = $"Firm '{firm.Name}' and all its users were permanently deleted.";
            return returnTo == "bin" ? RedirectToAction(nameof(RecycleBin)) : RedirectToAction(nameof(Index));
        }

        // ── Reset a client user's password (e.g. a locked-out firm Admin) ──────
        // Generates a fresh temporary password and forces a change on next login —
        // same flow as the initial onboarding password.
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetUserPassword(int firmId, string userId)
        {
            if (await IsServiceProviderOwnedFirmAsync(firmId))
            {
                TempData["Error"] = "This is your own organization, not a leased client — its users aren't managed from here.";
                return RedirectToAction(nameof(Index));
            }

            var targetUser = await _users.FindByIdAsync(userId);
            if (targetUser == null || targetUser.FirmId != firmId)
            {
                TempData["Error"] = "User not found in this firm.";
                return RedirectToAction(nameof(Detail), new { id = firmId });
            }

            var newPassword = GenerateTemporaryPassword();
            var policyErrors = _passwordPolicy.ValidatePassword(targetUser, newPassword);
            if (policyErrors.Count > 0)
            {
                TempData["Error"] = "Could not generate a compliant password — try again.";
                return RedirectToAction(nameof(Detail), new { id = firmId });
            }

            var resetToken = await _users.GeneratePasswordResetTokenAsync(targetUser);
            var result = await _users.ResetPasswordAsync(targetUser, resetToken, newPassword);
            if (!result.Succeeded)
            {
                TempData["Error"] = string.Join(", ", result.Errors.Select(e => e.Description));
                return RedirectToAction(nameof(Detail), new { id = firmId });
            }

            targetUser.MustChangePassword = true;
            targetUser.PasswordHistory = _passwordPolicy.BuildPasswordHistory(null, targetUser.PasswordHash ?? string.Empty);
            await _users.UpdateAsync(targetUser);
            await _users.SetLockoutEndDateAsync(targetUser, null);
            await _users.ResetAccessFailedCountAsync(targetUser);

            var serviceProvider = await _users.GetUserAsync(User);
            await _audit.LogAsync("reset_user_password", $"Reset password for {targetUser.Email} (firm {firmId})",
                serviceProvider?.Id, serviceProvider?.Email);

            TempData["Success"] = $"Password reset for {targetUser.Email}.";
            TempData["GeneratedCredentialsEmail"] = targetUser.Email;
            TempData["GeneratedCredentialsPassword"] = newPassword;
            return RedirectToAction(nameof(Detail), new { id = firmId });
        }
    }
}
