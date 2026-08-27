using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule34Service
    {
        Task<TableListResult> GetTablesAsync(int clientId);
        Task<Rule34ColumnSelectionResult> GetColumnsAsync(int clientId, string tableName);
        Task<Rule34VerifyResult> VerifyTableAsync(Rule34VerifyRequest request);
        Task<Rule34HolidayLoadResult> LoadHolidaysAsync(int startYear, int endYear);
        Task<Rule34ValidationSummary> RunValidationAsync(Rule34ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule34WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule34RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule34WorkspaceSaveResult> SaveWorkspaceAsync(Rule34ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule34WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule34ValidationRequest request);
        Task<Rule34ValidationSummary> GetExportSummaryAsync(Rule34ValidationRequest request);
        Task<int> GetPopulationCountAsync(Rule34ValidationRequest request);
    }
}
