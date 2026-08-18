using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule27Service
    {
        Task<TableListResult> GetTablesAsync(int clientId);
        Task<Rule27ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName);
        Task<Rule27FilterValueResult> GetFilterValuesAsync(int clientId, string tableName, string filterColumn);
        Task<Rule27VerifyResult> VerifyTableAsync(Rule27VerifyRequest request);
        Task<Rule27ValidationSummary> RunValidationAsync(Rule27ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule27WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule27RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule27WorkspaceSaveResult> SaveWorkspaceAsync(Rule27ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule27WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule27ValidationRequest request);
    }
}
