using System.IO;
using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule12Service
    {
        Task<Rule12TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<ColumnListResult> GetColumnsAsync(int clientId, string tableName);
        Task<Rule12VerifyResult> VerifyTablesAsync(Rule12VerifyRequest request);
        Task<Rule12ValidationSummary> RunValidationAsync(Rule12ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<Rule12ValidationSummary?> GetPendingValidationPreviewAsync(int clientId, string reviewerEmail);
        Task<bool> HasPendingValidationAsync(int clientId, string reviewerEmail);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule12WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule12RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null, bool includeFullResults = false);
        Task<Rule12WorkspaceSaveResult> SaveWorkspaceAsync(Rule12ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule12WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule12ValidationRequest request);
        Task<Rule12ValidationSummary> GetExportSummaryAsync(Rule12ValidationRequest request);

        // Cheap population size check - runs the same server-side prep SQL as a full export but
        // stops at a COUNT(*), so the caller can decide whether a full in-memory load (Excel) is
        // safe before attempting one, without ever materializing a single result row itself.
        Task<int> GetPopulationCountAsync(Rule12ValidationRequest request);

        // Writes CSV rows directly to outputStream as they're read from the database - no
        // intermediate row list, no in-memory StringBuilder. Unlike GetExportSummaryAsync + the
        // ExportService CSV writer, memory use stays roughly constant regardless of row count, so
        // this is the reliable path for a full-population export on a large engagement.
        Task StreamCsvExportAsync(Rule12ValidationRequest request, Stream outputStream);
    }
}
