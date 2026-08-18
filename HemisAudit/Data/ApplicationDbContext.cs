using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using HemisAudit.Models;

namespace HemisAudit.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        public DbSet<Firm>          Firms          { get; set; }
        public DbSet<FirmLicense>   FirmLicenses   { get; set; }

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
