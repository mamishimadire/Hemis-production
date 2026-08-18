using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IClinicalTechService
    {
        Task<ClinicalTechTableDiscoveryResult> GetTablesAsync(int clientId);
        Task<ClinicalTechColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<ClinicalTechVerifyResult> VerifyTablesAsync(ClinicalTechVerifyRequest request);
        Task<ClinicalTechValidationSummary> RunValidationAsync(ClinicalTechValidationRequest request, string? userEmail = null, string? userName = null);
        string GenerateSql(ClinicalTechValidationRequest request);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<ClinicalTechValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<ClinicalTechWorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<ClinicalTechRunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<ClinicalTechWorkspaceSaveResult> SaveWorkspaceAsync(ClinicalTechValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<ClinicalTechWorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
    }
}
