using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule39Service
    {
        Task<Rule39TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule39ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName);
        Task<Rule39DistinctValuesResult> GetDistinctValuesAsync(int clientId, string tableName, string columnName, string? preferredValue);
        Task<Rule39VerifyResult> VerifyTablesAsync(Rule39VerifyRequest request);
        Task<Rule39ValidationSummary> RunValidationAsync(Rule39ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule39WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule39RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule39WorkspaceSaveResult> SaveWorkspaceAsync(Rule39ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule39WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule39ValidationRequest request);
    }
}
