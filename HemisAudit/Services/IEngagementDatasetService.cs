using Microsoft.AspNetCore.Http;
using HemisAudit.Models;

namespace HemisAudit.Services
{
    public class DatasetTableInfo
    {
        public string TableName { get; set; } = "";
        public List<string> Columns { get; set; } = new();
        public long RowCount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class DatasetRowsPage
    {
        public List<string> Columns { get; set; } = new();
        public List<Dictionary<string, object?>> Rows { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public long TotalRows { get; set; }
        public Dictionary<string, string?> Filters { get; set; } = new();
    }

    public class DatasetUploadResult
    {
        public string TableName { get; set; } = "";
        public int ColumnCount { get; set; }
        public long RowCount { get; set; }
    }

    public class EngagementDatabaseInfo
    {
        public int ClientId { get; set; }
        public string DatabaseName { get; set; } = "";
        public string SchemaName { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    // One column as the data analyst can review/edit it before the table is created —
    // mirrors the "Modify Columns" step of a SQL Server flat-file import.
    public class DatasetStagedColumn
    {
        public string OriginalHeader { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public string DataType { get; set; } = "nvarchar(max)"; // any valid SQL Server data type expression
        public bool AllowNulls { get; set; } = true;
    }

    public class DatasetStagingInfo
    {
        public string Token { get; set; } = "";
        public int ClientId { get; set; }
        public string OriginalFileName { get; set; } = "";
        public string TableName { get; set; } = "";
        public List<DatasetStagedColumn> Columns { get; set; } = new();
        public List<Dictionary<string, string?>> PreviewRows { get; set; } = new();
        // -1 means "not counted" — for large files only a bounded sample is read at staging
        // time (so the preview screen stays fast regardless of file size); the exact count is
        // only known once the background import finishes.
        public long TotalRowCount { get; set; }
        public bool TotalRowCountIsExact { get; set; } = true;
    }

    // Progress of a dataset import running in the background, polled from the browser so the
    // upload request itself never has to stay open for the whole import.
    public class DatasetUploadJobStatus
    {
        public int JobId { get; set; }
        public string Status { get; set; } = "Queued"; // Queued | Running | Completed | Failed
        public string TableName { get; set; } = "";
        public long? TotalRows { get; set; }
        public long ProcessedRows { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class DatasetDistinctValue
    {
        public string Value { get; set; } = "";
        public long Count { get; set; }
    }

    public interface IEngagementDatasetService
    {
        // ── Per-engagement "database" (a named container the analyst creates once) ────
        Task<EngagementDatabaseInfo?> GetDatabaseAsync(int clientId);
        Task<EngagementDatabaseInfo> CreateDatabaseAsync(int clientId, string databaseName, ApplicationUser creator);

        // ── Staged upload wizard: stage -> preview/edit columns -> commit ─────────────
        Task<DatasetStagingInfo> StageUploadAsync(int clientId, IFormFile file, string? requestedTableName);
        Task<DatasetStagingInfo> GetStagingAsync(string token);
        Task<DatasetStagingInfo> UpdateStagingColumnsAsync(string token, string tableName, List<DatasetStagedColumn> columns);
        Task CancelStagingAsync(string token);

        // ── Commit runs as a background job so a huge file can't time out the request ──
        Task<int> StartCommitAsync(string token, ApplicationUser user);
        Task RunCommitJobAsync(int jobId, CancellationToken cancellationToken);
        Task<DatasetUploadJobStatus?> GetUploadJobStatusAsync(int jobId, int clientId);

        // ── Browsing / editing committed tables ────────────────────────────────────────
        Task<List<DatasetTableInfo>> ListTablesAsync(int clientId);
        // Table names only — no per-table COUNT(*) scan and no column catalog lookup. Every
        // rule's table-picker dropdown only ever needs names (see GetTablesAsync across the
        // Rule*Service classes), so this is what they should call instead of ListTablesAsync,
        // which computes row counts none of them use.
        Task<List<string>> ListTableNamesAsync(int clientId);
        Task<DatasetRowsPage> ListRowsAsync(int clientId, string tableName, int page, int pageSize, Dictionary<string, string?>? filters = null);
        Task<Dictionary<string, object?>?> GetRowAsync(int clientId, string tableName, long rowId);
        Task UpdateRowAsync(int clientId, string tableName, long rowId, Dictionary<string, string?> values);
        Task DeleteRowAsync(int clientId, string tableName, long rowId);
        Task DeleteTableAsync(int clientId, string tableName);
        Task ExportTableCsvAsync(int clientId, string tableName, Stream destination);

        // ── Used by rule engines validating uploaded data instead of a live SQL Server ──
        Task<List<string>> GetValidatedColumnsAsync(int clientId, string tableName);
        Task<List<DatasetDistinctValue>> GetDistinctColumnValuesAsync(int clientId, string tableName, string columnName, int take = 20);

        // Rule validations join/filter on analyst-chosen columns that vary per run, so we can't
        // pre-index a table at upload time — we don't know which columns will matter yet. This
        // creates a normalized expression index (matching the UPPER(TRIM(CAST(col AS text)))
        // pattern every migrated rule's joins use) the first time a column is actually used as a
        // join/filter key, so a large uploaded table only pays for a full scan once per column
        // instead of once per validation run. Safe to call every run — CREATE INDEX IF NOT
        // EXISTS is a no-op after the first call for a given table+column.
        Task EnsureJoinIndexAsync(int clientId, string tableName, string columnName);
    }
}
