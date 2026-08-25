using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule51Service
    {
        Task<Rule51TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule51ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule51VerifyResult> VerifyDataAsync(Rule51ValidationRequest request);
        Task<Rule51ValidationSummary> RunValidationAsync(Rule51ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule51ValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<Rule51WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule51RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule51WorkspaceSaveResult> SaveWorkspaceAsync(Rule51ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule51WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateSql(Rule51ValidationRequest request);
        Task<Rule51ValidationSummary> GetExportSummaryAsync(Rule51ValidationRequest request);
        Task<int> GetPopulationCountAsync(Rule51ValidationRequest request);
    }
}
