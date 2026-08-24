using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule22Service
    {
        Task<Rule22TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule22ColumnResult> GetProfColumnsAsync(int clientId, string profTable);
        Task<Rule22VerifyResult> VerifyTablesAsync(Rule22VerifyRequest request);
        Task<Rule22ValidationSummary> RunValidationAsync(Rule22ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule22WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule22RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule22WorkspaceSaveResult> SaveWorkspaceAsync(Rule22ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule22WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule22ValidationRequest request);
        Task<Rule22ValidationSummary> GetExportSummaryAsync(Rule22ValidationRequest request);

        // Writes CSV rows directly to outputStream as they're read from the database - no cap,
        // no intermediate row list. Mirrors IRule12Service.StreamCsvExportAsync.
        Task StreamCsvExportAsync(Rule22ValidationRequest request, Stream outputStream);

        // Cheap population size check - prep SQL plus a COUNT(*), no result rows loaded.
        Task<int> GetPopulationCountAsync(Rule22ValidationRequest request);
    }
}
