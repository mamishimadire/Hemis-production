using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule31Service
    {
        Task<TableListResult> GetTablesAsync(int clientId);
        Task<Rule31ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName);
        Task<Rule31FilterValueResult> GetFilterValuesAsync(int clientId, string tableName, string errorTypeColumn);
        Task<Rule31VerifyResult> VerifyTableAsync(Rule31VerifyRequest request);
        Task<Rule31ValidationSummary> RunValidationAsync(Rule31ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule31WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule31RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule31WorkspaceSaveResult> SaveWorkspaceAsync(Rule31ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule31WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule31ValidationRequest request);
        Task<Rule31ValidationSummary> GetExportSummaryAsync(Rule31ValidationRequest request);
        Task StreamCsvExportAsync(Rule31ValidationRequest request, bool onlyExceptions, Stream outputStream);
        Task<int> GetPopulationCountAsync(Rule31ValidationRequest request);
    }
}
