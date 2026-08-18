using System.ComponentModel.DataAnnotations;

namespace HemisAudit.ViewModels
{
    // ═══════════════════════════════════════════════════════════════════════════
    // SERVICE PROVIDER — FIRMS (tenant management)
    // ═══════════════════════════════════════════════════════════════════════════
    public class FirmListItemViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string FirmCode { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public int SeatCount { get; set; }
        public int SeatsUsed { get; set; }
        public int EngagementLimit { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Status { get; set; } = "";
    }

    public class CreateFirmViewModel
    {
        [Required, MaxLength(200)]
        public string FirmName { get; set; } = "";

        // The primary contact becomes the firm's first Admin login — a temporary
        // password is generated and shown once on success.
        [Required, MaxLength(200)]
        public string PrimaryContactName { get; set; } = "";

        [Required, EmailAddress]
        public string PrimaryContactEmail { get; set; } = "";

        [MaxLength(200)]
        public string? BillingContactName { get; set; }

        [EmailAddress]
        public string? BillingContactEmail { get; set; }

        [MaxLength(200)]
        public string? AdminContactName { get; set; }

        [EmailAddress]
        public string? AdminContactEmail { get; set; }

        [Required, Range(1, 10000)]
        public int SeatCount { get; set; } = 5;

        [Required, Range(1, 10000)]
        public int EngagementLimit { get; set; } = 5;

        [Required, DataType(DataType.Date)]
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddYears(1);
    }

    public class FirmRecycleBinItemViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string FirmCode { get; set; } = "";
        public DateTime? DeletedAt { get; set; }
        public string DeletedByName { get; set; } = "";
        public int SeatCount { get; set; }
        public int EngagementLimit { get; set; }
    }

    public class FirmUserRow
    {
        public string Id { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Role { get; set; } = "";
        public bool IsActive { get; set; }
    }

    public class FirmDetailViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string FirmCode { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public int SeatCount { get; set; }
        public int SeatsUsed { get; set; }
        public int EngagementLimit { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Status { get; set; } = "";
        public List<FirmUserRow> Users { get; set; } = new();

        public string PrimaryContactName { get; set; } = "";
        public string PrimaryContactEmail { get; set; } = "";
        public string? BillingContactName { get; set; }
        public string? BillingContactEmail { get; set; }
        public string? AdminContactName { get; set; }
        public string? AdminContactEmail { get; set; }
    }

    public class EditFirmLicenseViewModel
    {
        public int FirmId { get; set; }

        [Required, Range(1, 10000)]
        public int SeatCount { get; set; }

        [Required, Range(1, 10000)]
        public int EngagementLimit { get; set; }

        [Required, DataType(DataType.Date)]
        public DateTime ExpiresAt { get; set; }
    }
}
