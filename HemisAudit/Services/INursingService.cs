using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface INursingService
    {
        Task<NursingTableDiscoveryResult> GetTablesAsync(int clientId);
        Task<NursingColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<NursingVerifyResult> VerifyTablesAsync(NursingVerifyRequest request);
        Task<NursingValidationSummary> RunValidationAsync(NursingValidationRequest request, string? userEmail = null, string? userName = null);
        string GenerateSql(NursingValidationRequest request);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<NursingValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<NursingWorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<NursingRunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<NursingWorkspaceSaveResult> SaveWorkspaceAsync(NursingValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<NursingWorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
    }
}
