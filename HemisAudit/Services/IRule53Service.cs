using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule53Service
    {
        Task<Rule53TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule53ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule53VerifyResult> VerifyTablesAsync(Rule53ValidationRequest request);
        Task<Rule53ValidationSummary> RunValidationAsync(Rule53ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule53ValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<Rule53WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule53RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule53WorkspaceSaveResult> SaveWorkspaceAsync(Rule53ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule53WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateValidationSql(Rule53ValidationRequest request);
    }
}
