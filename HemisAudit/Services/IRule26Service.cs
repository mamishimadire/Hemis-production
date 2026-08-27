using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule26Service
    {
        Task<TableListResult> GetTablesAsync(int clientId);
        Task<Rule26ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName, bool isProfTable);
        Task<Rule26VerifyResult> VerifyTablesAsync(Rule26VerifyRequest request);
        Task<Rule26ValidationSummary> RunValidationAsync(Rule26ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<Rule26ValidationSummary> GetExportSummaryAsync(Rule26ValidationRequest request);
        Task<int> GetPopulationCountAsync(Rule26ValidationRequest request);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule26WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule26RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule26WorkspaceSaveResult> SaveWorkspaceAsync(Rule26ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule26WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule26ValidationRequest request);
    }
}
