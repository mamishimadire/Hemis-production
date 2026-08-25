using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule30Service
    {
        Task<TableListResult> GetTablesAsync(int clientId);
        Task<Rule32ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName);
        Task<Rule32FilterValueResult> GetFilterValuesAsync(int clientId, string tableName, string errorTypeColumn);
        Task<Rule32VerifyResult> VerifyTableAsync(Rule32VerifyRequest request);
        Task<Rule32ValidationSummary> RunValidationAsync(Rule32ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule32WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule32RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule32WorkspaceSaveResult> SaveWorkspaceAsync(Rule32ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule32WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule32ValidationRequest request);
        Task<Rule32ValidationSummary> GetExportSummaryAsync(Rule32ValidationRequest request);
        Task StreamCsvExportAsync(Rule32ValidationRequest request, bool onlyExceptions, Stream outputStream);
        Task<int> GetPopulationCountAsync(Rule32ValidationRequest request);
    }
}
