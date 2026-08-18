using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule66Service
    {
        Task<Rule66TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule66ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule66DistinctValuesResult> GetDistinctValuesAsync(int clientId, string tableName, string columnName);
        Task<Rule66VerifyResult> VerifyTablesAsync(Rule66ValidationRequest request);
        Task<Rule66ValidationSummary> RunValidationAsync(Rule66ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule66ValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<Rule66WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule66RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule66WorkspaceSaveResult> SaveWorkspaceAsync(Rule66ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule66WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateSql(Rule66ValidationRequest request);
    }
}
