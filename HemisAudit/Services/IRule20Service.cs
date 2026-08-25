using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule20Service
    {
        Task<Rule20TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule20ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName);
        Task<Rule20VerifyResult> VerifyTablesAsync(Rule20VerifyRequest request);
        Task<Rule20ValidationSummary> RunValidationAsync(Rule20ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule20WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule20RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false);
        Task<Rule20WorkspaceSaveResult> SaveWorkspaceAsync(Rule20ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule20WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule20ValidationRequest request);
        Task<Rule20ValidationSummary> GetExportSummaryAsync(Rule20ValidationRequest request);

        // Writes CSV rows directly to outputStream as they're read from the database - no cap,
        // no intermediate row list. Mirrors IRule12Service.StreamCsvExportAsync.
        Task StreamCsvExportAsync(Rule20ValidationRequest request, Stream outputStream);

        // Cheap population size check - prep SQL plus a COUNT(*), no result rows loaded.
        Task<int> GetPopulationCountAsync(Rule20ValidationRequest request);
    }
}
