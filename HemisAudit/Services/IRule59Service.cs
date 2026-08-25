using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule59Service
    {
        Task<Rule59TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule59ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule59VerifyResult> VerifyDataAsync(Rule59ValidationRequest request);
        Task<Rule59ValidationSummary> RunValidationAsync(Rule59ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule59ValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<Rule59WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule59RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule59WorkspaceSaveResult> SaveWorkspaceAsync(Rule59ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule59WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateSql(Rule59ValidationRequest request);
        Task<Rule59ValidationSummary> GetExportSummaryAsync(Rule59ValidationRequest request);
        Task<int> GetPopulationCountAsync(Rule59ValidationRequest request);
    }
}
