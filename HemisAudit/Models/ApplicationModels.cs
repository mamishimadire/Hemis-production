using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace HemisAudit.Models
{
    // ═══════════════════════════════════════════════════════════════════════════
    // APPLICATION USER  (extends ASP.NET Identity)
    // ═══════════════════════════════════════════════════════════════════════════
    public class ApplicationUser : IdentityUser
    {
        [Required, MaxLength(100)]
        public string FirstName { get; set; } = "";

        [Required, MaxLength(100)]
        public string LastName { get; set; } = "";

        public string FullName => $"{FirstName} {LastName}".Trim();

        [MaxLength(20)]
        public string EmployeeCode { get; set; } = "";     // e.g. MADM007

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastLoginAt { get; set; }
        public DateTime? PasswordSetDate { get; set; } = DateTime.UtcNow;
        public DateTime? PasswordChangedAt { get; set; }
        public string? PasswordHistory { get; set; }
        // Superseded by ProfilePictureData below - files written to the container's local disk
        // (wwwroot/uploads/profiles/) don't survive a restart/redeploy on Render, so any path
        // stored here from before that change points at a file that's already gone. Column stays
        // only so existing rows don't need a migration to drop it; nothing reads it any more.
        public string? ProfilePicturePath { get; set; }

        public byte[]? ProfilePictureData { get; set; }
        public string? ProfilePictureContentType { get; set; }

        [MaxLength(50)]
        public string? Gender { get; set; }

        [MaxLength(150)]
        public string? Department { get; set; }

        [MaxLength(500)]
        public string? OfficeAddress { get; set; }

        // Tenant this user belongs to. Null only for ServiceProvider (platform) users.
        public int? FirmId { get; set; }
        public Firm? Firm { get; set; }

        // Set whenever the service provider (or a firm's own Admin) hands this user a
        // temporary password — forces a password change on their very next login.
        public bool MustChangePassword { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FIRM  (tenant: an independent audit firm leasing this system)
    // ═══════════════════════════════════════════════════════════════════════════
    public class Firm
    {
        public int Id { get; set; }

        [Required, MaxLength(200)]
        public string Name { get; set; } = "";

        // Sequential, unique code given to the firm when the service provider grants
        // access. The firm's users enter this at login to identify their tenant.
        [Required, MaxLength(20)]
        public string FirmCode { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? CreatedByUserId { get; set; }

        // Onboarding contacts
        [MaxLength(200)]
        public string PrimaryContactName { get; set; } = "";
        [MaxLength(255)]
        public string PrimaryContactEmail { get; set; } = "";

        [MaxLength(200)]
        public string? BillingContactName { get; set; }
        [MaxLength(255)]
        public string? BillingContactEmail { get; set; }

        [MaxLength(200)]
        public string? AdminContactName { get; set; }
        [MaxLength(255)]
        public string? AdminContactEmail { get; set; }

        // Soft delete — a firm sent to the recycle bin is hidden from the active list and
        // loses access, but stays fully intact until someone deletes it permanently.
        public bool IsDeleted { get; set; }
        public DateTime? DeletedAt { get; set; }
        [MaxLength(450)]
        public string? DeletedByUserId { get; set; }

        // Navigation
        public FirmLicense? License { get; set; }
        public ICollection<ApplicationUser> Users { get; set; } = new List<ApplicationUser>();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FIRM LICENSE  (seats, engagement limit, expiry — set by the service provider
    // after reviewing the firm's proof of payment)
    // ═══════════════════════════════════════════════════════════════════════════
    public class FirmLicense
    {
        public int Id { get; set; }

        public int FirmId { get; set; }
        public Firm Firm { get; set; } = null!;

        public int SeatCount { get; set; }
        public int EngagementLimit { get; set; }
        public DateTime ExpiresAt { get; set; }

        // Active | Suspended | Expired
        [MaxLength(20)]
        public string Status { get; set; } = "Active";

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? UpdatedByUserId { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CLIENT  (plain DTO shape for the engagement selector used across rule pages —
    // populated from ISystemDatabaseService results, not an EF-mapped entity.
    // The real "Clients"/engagements table lives in Postgres via SystemDatabaseService.)
    // ═══════════════════════════════════════════════════════════════════════════
    public class Client
    {
        public int Id { get; set; }

        [Required, MaxLength(200)]
        public string Name { get; set; } = "";

        [MaxLength(20)]
        public string FiscalYear { get; set; } = "";

        [MaxLength(500)]
        public string? Description { get; set; }

        [MaxLength(20)]
        public string Status { get; set; } = "Active";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string CreatedByUserId { get; set; } = "";

        public bool IsActive { get; set; } = true;
    }
}
