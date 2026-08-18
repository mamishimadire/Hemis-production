using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IBiomedicalService
    {
        Task<BiomedicalTableDiscoveryResult> GetTablesAsync(int clientId);
        Task<BiomedicalColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<BiomedicalVerifyResult> VerifyTablesAsync(BiomedicalVerifyRequest request);
        Task<BiomedicalValidationSummary> RunValidationAsync(BiomedicalValidationRequest request, string? userEmail = null, string? userName = null);
        string GenerateSql(BiomedicalValidationRequest request);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<BiomedicalValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<BiomedicalWorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<BiomedicalRunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<BiomedicalWorkspaceSaveResult> SaveWorkspaceAsync(BiomedicalValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<BiomedicalWorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
    }
}
