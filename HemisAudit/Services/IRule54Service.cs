using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule54Service
    {
        Task<Rule54TableListResult> GetTablesAsync(int clientId);
        Task<Rule54ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule54VerifyResult> VerifyDataAsync(Rule54ValidationRequest request);
        Task<Rule54ValidationSummary> RunValidationAsync(Rule54ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule54WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule54RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule54WorkspaceSaveResult> SaveWorkspaceAsync(Rule54ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule54WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateSql(Rule54ValidationRequest request);
    }
}
