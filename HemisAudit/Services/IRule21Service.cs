using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule21Service
    {
        Task<Rule21TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule21ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName);
        Task<Rule21DistinctValuesResult> GetDistinctValuesAsync(int clientId, string tableName, string columnName, string? preferredValue);
        Task<Rule21VerifyResult> VerifyTablesAsync(Rule21VerifyRequest request);
        Task<Rule21ValidationSummary> RunValidationAsync(Rule21ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule21WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule21RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule21WorkspaceSaveResult> SaveWorkspaceAsync(Rule21ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule21WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule21ValidationRequest request);
        Task<Rule21ValidationSummary> GetExportSummaryAsync(Rule21ValidationRequest request);

        // Writes CSV rows directly to outputStream as they're read from the database - the
        // FLAGGED (exception) side has no cap; CLEAR stays a deliberate fixed-size sample.
        // Mirrors IRule12Service.StreamCsvExportAsync.
        Task StreamCsvExportAsync(Rule21ValidationRequest request, bool onlyExceptions, Stream outputStream);

        // Cheap population size check - prep SQL plus a COUNT(*), no result rows loaded.
        Task<int> GetPopulationCountAsync(Rule21ValidationRequest request);
    }
}
