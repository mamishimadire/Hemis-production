using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule67Service
    {
        Task<Rule67TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule67ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule67VerifyResult> VerifyTablesAsync(Rule67ValidationRequest request);
        Task<Rule67ValidationSummary> RunValidationAsync(Rule67ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule67ValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<Rule67WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule67RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule67WorkspaceSaveResult> SaveWorkspaceAsync(Rule67ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule67WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateSql(Rule67ValidationRequest request);
    }
}
