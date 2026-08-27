using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule10Service
    {
        Task<Rule10TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule10ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName);
        Task<Rule10VerifyResult> VerifyTablesAsync(Rule10VerifyRequest request);
        Task<Rule10ValidationSummary> RunValidationAsync(Rule10ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<Rule10ValidationSummary?> GetPendingValidationPreviewAsync(int ruleNumber, int clientId, string reviewerEmail);
        Task<bool> HasPendingValidationAsync(int ruleNumber, int clientId, string reviewerEmail);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule10WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int ruleNumber, int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule10RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false);
        Task<Rule10WorkspaceSaveResult> SaveWorkspaceAsync(Rule10ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule10WorkspaceSaveResult> BeginWorkspaceEditAsync(int ruleNumber, int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule10ValidationRequest request);
        Task<Rule10ValidationSummary> GetExportSummaryAsync(Rule10ValidationRequest request);
        Task<int> GetPopulationCountAsync(Rule10ValidationRequest request);
    }
}
