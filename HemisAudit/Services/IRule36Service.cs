using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule36Service
    {
        Task<TableListResult> GetTablesAsync(int clientId);
        Task<Rule36ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName, bool isStudTable);
        Task<Rule36VerifyResult> VerifyDataAsync(Rule36VerifyRequest request);
        Task<Rule36ValidationSummary> RunValidationAsync(Rule36ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule36WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule36RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule36WorkspaceSaveResult> SaveWorkspaceAsync(Rule36ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule36WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateSql(Rule36ValidationRequest request);
    }
}
