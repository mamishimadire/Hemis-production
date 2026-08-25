using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule52Service
    {
        Task<Rule52TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule52ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule52VerifyResult> VerifyTablesAsync(Rule52ValidationRequest request);
        Task<Rule52ValidationSummary> RunValidationAsync(Rule52ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule52ValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<Rule52WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule52RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule52WorkspaceSaveResult> SaveWorkspaceAsync(Rule52ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule52WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateValidationSql(Rule52ValidationRequest request);
        Task<Rule52ValidationSummary> GetExportSummaryAsync(Rule52ValidationRequest request);
        Task<int> GetPopulationCountAsync(Rule52ValidationRequest request);
    }
}
