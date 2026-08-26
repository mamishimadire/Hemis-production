using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IBiokinieticService
    {
        Task<BiokinieticTableDiscoveryResult> GetTablesAsync(int clientId);
        Task<BiokinieticColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<BiokinieticVerifyResult> VerifyTablesAsync(BiokinieticVerifyRequest request);
        Task<BiokinieticValidationSummary> RunValidationAsync(BiokinieticValidationRequest request, string? userEmail = null, string? userName = null);
        string GenerateSql(BiokinieticValidationRequest request);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<BiokinieticValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<BiokinieticWorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<BiokinieticRunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<BiokinieticWorkspaceSaveResult> SaveWorkspaceAsync(BiokinieticValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<BiokinieticWorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<BiokinieticValidationSummary> GetExportSummaryAsync(BiokinieticValidationRequest request);
        Task<int> GetPopulationCountAsync(BiokinieticValidationRequest request);
    }
}
