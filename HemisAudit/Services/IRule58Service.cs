using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule58Service
    {
        Task<Rule58TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule58ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule58VerifyResult> VerifyDataAsync(Rule58ValidationRequest request);
        Task<Rule58ValidationSummary> RunValidationAsync(Rule58ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule58WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule58RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule58WorkspaceSaveResult> SaveWorkspaceAsync(Rule58ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule58WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateSql(Rule58ValidationRequest request);
    }
}
