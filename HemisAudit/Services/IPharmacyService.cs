using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IPharmacyService
    {
        Task<PharmacyTableDiscoveryResult> GetTablesAsync(int clientId);
        Task<PharmacyColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<PharmacyVerifyResult> VerifyTablesAsync(PharmacyVerifyRequest request);
        Task<PharmacyValidationSummary> RunValidationAsync(PharmacyValidationRequest request, string? userEmail = null, string? userName = null);
        string GenerateSql(PharmacyValidationRequest request);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<PharmacyValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<PharmacyWorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<PharmacyRunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<PharmacyWorkspaceSaveResult> SaveWorkspaceAsync(PharmacyValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<PharmacyWorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
    }
}
