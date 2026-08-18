using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using HemisAudit.Models;
using System.Text.Json;

namespace HemisAudit.Data
{
    public static class DbInitializer
    {
        public static async Task SeedAsync(IServiceProvider services)
        {
            var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var db = services.GetRequiredService<ApplicationDbContext>();

            // Postgres schema is owned by EF Core migrations (Data/Migrations) — apply
            // any pending ones instead of the old SQLite-era EnsureCreated/PRAGMA patches.
            await db.Database.MigrateAsync();

            await SeedRolesAsync(roleManager);
            await SeedDefaultAdminAsync(userManager);
        }

        private static async Task SeedRolesAsync(RoleManager<IdentityRole> roleManager)
        {
            string[] roles = { "ServiceProvider", "Admin", "Director", "Manager", "DataAnalyst", "Trainee" };
            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                    await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        private static async Task SeedDefaultAdminAsync(UserManager<ApplicationUser> userManager)
        {
            const string adminEmail = "mamishimadire@gmail.com";

            var existing = await userManager.FindByEmailAsync(adminEmail);
            if (existing != null)
            {
                var needsSave = false;

                if (string.IsNullOrWhiteSpace(existing.PasswordHistory))
                {
                    var currentHash = existing.PasswordHash ?? string.Empty;
                    existing.PasswordHistory = JsonSerializer.Serialize(new[] { currentHash });
                    needsSave = true;
                }

                if (needsSave)
                    await userManager.UpdateAsync(existing);

                await userManager.SetLockoutEndDateAsync(existing, null);
                await userManager.ResetAccessFailedCountAsync(existing);

                if (!await userManager.IsInRoleAsync(existing, "Admin"))
                    await userManager.AddToRoleAsync(existing, "Admin");

                if (!await userManager.IsInRoleAsync(existing, "ServiceProvider"))
                    await userManager.AddToRoleAsync(existing, "ServiceProvider");

                return;
            }

            var admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                FirstName = "Mamishi",
                LastName = "Madire",
                EmployeeCode = "MADM007",
                EmailConfirmed = true,
                IsActive = true,
                PasswordSetDate = DateTime.UtcNow
            };

            var result = await userManager.CreateAsync(admin, "Admin@123");
            if (result.Succeeded)
            {
                var hash = admin.PasswordHash ?? string.Empty;
                admin.PasswordHistory = JsonSerializer.Serialize(new[] { hash });
                await userManager.AddToRoleAsync(admin, "Admin");
                await userManager.AddToRoleAsync(admin, "ServiceProvider");
                await userManager.UpdateAsync(admin);
            }
        }

    }
}
