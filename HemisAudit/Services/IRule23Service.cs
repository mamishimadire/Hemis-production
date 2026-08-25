using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface IRule23Service
    {
        Task<Rule23TableDiscoveryResult> GetTablesAsync(int clientId);
        Task<Rule23AuditColumnResult> GetAuditColumnsAsync(int clientId, string auditTable);
        Task<Rule23VerifyResult> VerifyTablesAsync(Rule23VerifyRequest request);
        Task<Rule23ValidationSummary> RunValidationAsync(Rule23ValidationRequest request, string? userEmail = null, string? userName = null);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<Rule23WorkspaceStateViewModel?> GetCurrentWorkspaceStateAsync(int clientId, string? currentUserEmail = null, bool includeSummary = true);
        Task<Rule23RunReviewViewModel?> GetSavedRunAsync(int runId, string? currentUserEmail = null);
        Task<Rule23WorkspaceSaveResult> SaveWorkspaceAsync(Rule23ValidationRequest request, string reviewerEmail, string? reviewerName = null);
        Task<Rule23WorkspaceSaveResult> BeginWorkspaceEditAsync(int runId, string reviewerEmail, string? reviewerName = null);
        Task AddOrUpdateSignoffAsync(int runId, string reviewerEmail, string comment);
        Task RemoveSignoffAsync(int runId, string reviewerEmail);
        Task<string> GenerateSqlAsync(Rule23ValidationRequest request);
        Task<Rule23ValidationSummary> GetExportSummaryAsync(Rule23ValidationRequest request);
        Task StreamCsvExportAsync(Rule23ValidationRequest request, Stream outputStream);
        Task<int> GetPopulationCountAsync(Rule23ValidationRequest request);
    }
}
