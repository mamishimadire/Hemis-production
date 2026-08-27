using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule68Service
    {
        Task<Rule68TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule68ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule68VerifyResult> VerifyTablesAsync(Rule68ValidationRequest request);
        Task<Rule68ValidationSummary> RunValidationAsync(Rule68ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<Rule68ValidationSummary> GetExportSummaryAsync(Rule68ValidationRequest request);
        Task<int> GetPopulationCountAsync(Rule68ValidationRequest request);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule68ValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<Rule68WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule68RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule68WorkspaceSaveResult> SaveWorkspaceAsync(Rule68ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule68WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateSql(Rule68ValidationRequest request);
    }
}
