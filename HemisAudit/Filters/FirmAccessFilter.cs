using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using HemisAudit.Data;
using HemisAudit.Models;

namespace HemisAudit.Filters
{
    // Blocks access for a firm that's been suspended by the service provider, or whose
    // license has expired. Service Provider users (FirmId == null) always pass through.
    public class FirmAccessFilter : IAsyncActionFilter
    {
        private static readonly HashSet<string> AllowedPaths = new(StringComparer.OrdinalIgnoreCase)
        {
            "/account/login",
            "/account/logout",
            "/account/forgotpassword",
            "/account/resetpassword",
            "/account/renewpassword",
            "/account/passwordexpired",
            "/account/accessdenied",
            "/account/firmaccessrestricted"
        };

        private readonly UserManager<ApplicationUser>  _users;
        private readonly SignInManager<ApplicationUser> _signIn;
        private readonly ApplicationDbContext           _db;

        public FirmAccessFilter(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, ApplicationDbContext db)
        {
            _users = users; _signIn = signIn; _db = db;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var path = context.HttpContext.Request.Path.Value ?? string.Empty;
            if (AllowedPaths.Contains(path)
                || path.StartsWith("/css", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/js", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (context.HttpContext.User?.Identity?.IsAuthenticated != true)
            {
                await next();
                return;
            }

            var user = await _users.GetUserAsync(context.HttpContext.User);
            if (user == null || user.FirmId == null)
            {
                // No firm to check (Service Provider account) — let it through.
                await next();
                return;
            }

            var firm = await _db.Firms.AsNoTracking().Include(f => f.License)
                .FirstOrDefaultAsync(f => f.Id == user.FirmId);
            var license = firm?.License;
            var blocked = firm == null || firm.IsDeleted
                || license == null
                || !string.Equals(license.Status, "Active", StringComparison.OrdinalIgnoreCase)
                || license.ExpiresAt < DateTime.UtcNow;

            if (blocked)
            {
                await _signIn.SignOutAsync();
                context.Result = new RedirectToActionResult("FirmAccessRestricted", "Account", null);
                return;
            }

            await next();
        }
    }
}
