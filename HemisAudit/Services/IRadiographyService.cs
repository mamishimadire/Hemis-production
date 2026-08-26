using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRadiographyService
    {
        Task<RadiographyTableDiscoveryResult> GetTablesAsync(int clientId);
        Task<RadiographyColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<RadiographyVerifyResult> VerifyTablesAsync(RadiographyVerifyRequest request);
        Task<RadiographyValidationSummary> RunValidationAsync(RadiographyValidationRequest request, string? userEmail = null, string? userName = null);
        string GenerateSql(RadiographyValidationRequest request);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<RadiographyValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<RadiographyWorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<RadiographyRunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<RadiographyWorkspaceSaveResult> SaveWorkspaceAsync(RadiographyValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<RadiographyWorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<RadiographyValidationSummary> GetExportSummaryAsync(RadiographyValidationRequest request);
        Task<int> GetPopulationCountAsync(RadiographyValidationRequest request);
    }
}
