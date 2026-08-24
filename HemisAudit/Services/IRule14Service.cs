using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule14Service
    {
        Task<Rule14TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<ColumnListResult> GetColumnsAsync(int clientId, string tableName);
        Task<Rule14VerifyResult> VerifyTablesAsync(Rule14VerifyRequest request);
        Task<Rule14ValidationSummary> RunValidationAsync(Rule14ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<Rule14ValidationSummary?> GetPendingValidationPreviewAsync(int clientId, string reviewerEmail);
        Task<bool> HasPendingValidationAsync(int clientId, string reviewerEmail);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule14WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule14RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false);
        Task<Rule14WorkspaceSaveResult> SaveWorkspaceAsync(Rule14ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule14WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule14ValidationRequest request);
        Task<Rule14ValidationSummary> GetExportSummaryAsync(Rule14ValidationRequest request);

        // Writes CSV rows directly to outputStream as they're read from the database - no cap,
        // no intermediate row list. Mirrors IRule12Service.StreamCsvExportAsync.
        Task StreamCsvExportAsync(Rule14ValidationRequest request, Stream outputStream);

        // Cheap population size check - prep SQL plus a COUNT(*), no result rows loaded.
        Task<int> GetPopulationCountAsync(Rule14ValidationRequest request);
    }
}
