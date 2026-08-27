using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IMopService
    {
        Task<MopTableDiscoveryResult> GetTablesAsync(int clientId);
        Task<MopColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<MopVerifyResult> VerifyTablesAsync(MopVerifyRequest request);
        Task<MopValidationSummary> RunValidationAsync(MopValidationRequest request, string? userEmail = null, string? userName = null);
        string GenerateSql(MopValidationRequest request);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<MopValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<MopWorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<MopRunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<MopWorkspaceSaveResult> SaveWorkspaceAsync(MopValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<MopWorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<MopValidationSummary> GetExportSummaryAsync(MopValidationRequest request);
        Task<int> GetPopulationCountAsync(MopValidationRequest request);
    }
}
