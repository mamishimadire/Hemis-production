using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule11Service
    {
        Task<Rule11TableListResult> GetTablesAsync(int clientId);
        Task<ColumnListResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule11FilterValueResult> GetFilterValuesAsync(int clientId, string qualTable, string approvalColumn);
        Task<Rule11VerifyResult> VerifyDataAsync(Rule11VerifyRequest request);
        Task<Rule11ValidationSummary> RunValidationAsync(Rule11ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule11WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule11RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule11WorkspaceSaveResult> SaveWorkspaceAsync(Rule11ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule11WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateSql(Rule11ValidationRequest request);
    }
}
