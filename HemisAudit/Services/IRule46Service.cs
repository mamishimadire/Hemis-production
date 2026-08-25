using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule46Service
    {
        Task<Rule46TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule46ColumnDiscoveryResult> GetColumnsAsync(int clientId, string tableName, string tableRole);
        Task<Rule46VerifyResult> VerifyTablesAsync(Rule46ValidationRequest request);
        Task<Rule46ValidationSummary> RunValidationAsync(Rule46ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule46ValidationSummary?> GetStoredSummaryAsync(int runId);
        Task<Rule46WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null);
        Task<Rule46RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule46WorkspaceSaveResult> SaveWorkspaceAsync(Rule46ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule46WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        string GenerateValidationSql(Rule46ValidationRequest request);
        Task<Rule46ValidationSummary> GetExportSummaryAsync(Rule46ValidationRequest request);
        Task StreamCsvExportAsync(Rule46ValidationRequest request, Stream outputStream);
        Task<int> GetPopulationCountAsync(Rule46ValidationRequest request);
    }
}
