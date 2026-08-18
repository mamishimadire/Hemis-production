using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using HemisAudit.Models;

namespace HemisAudit.Data
{
    // IDataProtectionKeyContext backs the antiforgery/auth-cookie key ring with this same
    // Postgres database instead of the container's local disk - see Program.cs for why.
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>, IDataProtectionKeyContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        public DbSet<Firm>          Firms          { get; set; }
        public DbSet<FirmLicense>   FirmLicenses   { get; set; }
        public DbSet<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey> DataProtectionKeys { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // ── Firm ──────────────────────────────────────────────────────────
            builder.Entity<Firm>(e =>
            {
                e.HasIndex(f => f.Name);
                e.HasIndex(f => f.FirmCode).IsUnique();
                e.HasOne(f => f.License)
                 .WithOne(l => l.Firm)
                 .HasForeignKey<FirmLicense>(l => l.FirmId)
                 .OnDelete(DeleteBehavior.Cascade);
                e.HasMany(f => f.Users)
                 .WithOne(u => u.Firm)
                 .HasForeignKey(u => u.FirmId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<FirmLicense>(e =>
            {
                e.HasIndex(l => l.FirmId).IsUnique();
            });
        }
    }
}
