using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MiniExcelLibs;
using Npgsql;
using NpgsqlTypes;
using HemisAudit.Models;

namespace HemisAudit.Services
{
    // Lets a data analyst create a named "database" for their engagement, then upload
    // CSV/Excel files into it through a preview-and-confirm wizard (mirroring the SQL Server
    // flat-file import flow), landing as real Postgres tables they can browse, edit, and
    // delete — no live SQL Server connection to the audited institution required. Each
    // engagement gets its own Postgres schema ("engagement_{clientId}"), computed
    // server-side only, never from user input; the analyst-chosen "database name" is a
    // friendly label stored in EngagementDatabases, not the schema name itself.
    public class EngagementDatasetService : IEngagementDatasetService
    {
        private const int MaxTablesPerEngagement = 50;
        // Postgres has a hard limit of 1600 columns per table — this isn't a tunable app
        // setting, a wider file genuinely cannot become one table and must be split.
        private const int MaxColumnsPerTable = 200;
        // No row-count or file-size cap by design — a genuinely huge upload is bounded only
        // by real server memory/time, not by an app-imposed number.
        private const int PreviewRowCount = 30;
        private const int TypeSampleRows = 200;
        // Staging (the Preview/Modify-columns wizard steps) only ever reads this many rows off
        // disk, no matter how large the underlying file is — keeps those pages fast for a
        // 500-row file and a 500-million-row file alike. The actual import (StartCommitAsync/
        // RunCommitJobAsync) streams the whole file in the background instead.
        private const int PreviewScanRows = 1000;
        private static readonly TimeSpan StagingTtl = TimeSpan.FromHours(2);

        private static readonly string[] SqlTypeSuggestions = new[]
        {
            "bit", "tinyint", "smallint", "int", "bigint",
            "decimal(18,2)", "numeric(18,2)", "money", "smallmoney",
            "float", "real",
            "date", "datetime", "datetime2", "datetimeoffset", "smalldatetime", "timestamp", "time",
            "char(10)", "nchar(10)", "varchar(50)", "nvarchar(50)", "varchar(max)", "nvarchar(max)",
            "text", "ntext", "uniqueidentifier", "xml",
            "binary(50)", "varbinary(50)", "varbinary(max)", "geography", "geometry", "hierarchyid", "sql_variant"
        };

        private static readonly Regex ValidSqlTypeRegex = new(
            @"^(bit|tinyint|smallint|int|bigint|decimal|numeric|money|smallmoney|float|real|date|datetime|datetime2|datetimeoffset|smalldatetime|timestamp|time|char|nchar|varchar|nvarchar|text|ntext|uniqueidentifier|xml|binary|varbinary|geography|geometry|hierarchyid|sql_variant)(\(\s*(max|\d+)(\s*,\s*\d+)?\s*\))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;
        private readonly IBackgroundTaskQueue _queue;

        public EngagementDatasetService(Microsoft.Extensions.Configuration.IConfiguration configuration, IWebHostEnvironment environment, IBackgroundTaskQueue queue)
        {
            _configuration = configuration;
            _environment = environment;
            _queue = queue;
        }

        private async Task<NpgsqlConnection> OpenConnectionAsync(int commandTimeoutSeconds = 60)
        {
            var connectionString = HemisAudit.Data.PostgresConnectionStringHelper.WithResiliencyDefaults(
                _configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."),
                commandTimeoutSeconds);
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }

        private static string SchemaFor(int clientId) => $"engagement_{clientId}";

        // The schema name is always SchemaFor(clientId) in practice (it's what CreateDatabaseAsync
        // stores), but we resolve it through the stored row rather than assuming, and this also
        // acts as an existence check — no database row means no schema to look in yet.
        private async Task<string> GetSchemaNameAsync(int clientId)
        {
            var database = await GetDatabaseAsync(clientId);
            return string.IsNullOrWhiteSpace(database?.SchemaName) ? SchemaFor(clientId) : database!.SchemaName;
        }

        // ── Engagement "database" (created once, gates uploads) ────────────────────────
        public async Task<EngagementDatabaseInfo?> GetDatabaseAsync(int clientId)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT ""DatabaseName"", ""SchemaName"", ""CreatedAt"" FROM ""EngagementDatabases"" WHERE ""ClientID"" = @id;";
            command.Parameters.AddWithValue("id", clientId);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            var schemaName = reader.GetString(1);
            return new EngagementDatabaseInfo
            {
                ClientId = clientId,
                DatabaseName = reader.GetString(0),
                // A handful of rows predate the SchemaName column and still have it blank —
                // engagement_{clientId} is always the real schema regardless, so callers should
                // never see a blank SchemaName and have to remember to fall back themselves.
                SchemaName = string.IsNullOrWhiteSpace(schemaName) ? SchemaFor(clientId) : schemaName,
                CreatedAt = reader.GetDateTime(2)
            };
        }

        public async Task<EngagementDatabaseInfo> CreateDatabaseAsync(int clientId, string databaseName, ApplicationUser creator)
        {
            var name = (databaseName ?? "").Trim();
            if (name.Length == 0)
                throw new InvalidOperationException("Enter a database name.");
            if (name.Length > 200)
                throw new InvalidOperationException("Database name is too long.");

            await using var connection = await OpenConnectionAsync();

            var existing = await GetDatabaseAsync(clientId);
            if (existing != null)
                throw new InvalidOperationException($"This engagement already has a database ('{existing.DatabaseName}').");

            await using (var schemaCommand = connection.CreateCommand())
            {
                schemaCommand.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{SchemaFor(clientId)}\";";
                await schemaCommand.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO ""EngagementDatabases"" (""ClientID"", ""DatabaseName"", ""SchemaName"", ""CreatedByUserID"", ""CreatedAt"")
                VALUES (@id, @name, @schemaName, @userId, now());";
            command.Parameters.AddWithValue("id", clientId);
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("schemaName", SchemaFor(clientId));
            command.Parameters.AddWithValue("userId", creator.Id);
            await command.ExecuteNonQueryAsync();

            return new EngagementDatabaseInfo { ClientId = clientId, DatabaseName = name, SchemaName = SchemaFor(clientId), CreatedAt = DateTime.UtcNow };
        }

        // ── Staged upload wizard ────────────────────────────────────────────────────────
        private string StagingRoot => Path.Combine(_environment.ContentRootPath, "App_Data", "dataset-staging");

        private class StagingMeta
        {
            public string Token { get; set; } = "";
            public int ClientId { get; set; }
            public string OriginalFileName { get; set; } = "";
            public string OriginalExtension { get; set; } = "";
            public string TableName { get; set; } = "";
            public List<DatasetStagedColumn> Columns { get; set; } = new();
            public long TotalRowCount { get; set; }
            public bool TotalRowCountIsExact { get; set; } = true;
            public DateTime CreatedAt { get; set; }
        }

        private void CleanupStaleStagingDirs()
        {
            if (!Directory.Exists(StagingRoot)) return;
            foreach (var dir in Directory.GetDirectories(StagingRoot))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > StagingTtl)
                        Directory.Delete(dir, recursive: true);
                }
                catch (IOException) { /* best-effort cleanup */ }
            }
        }

        private async Task<StagingMeta> LoadMetaAsync(string token)
        {
            var safeToken = SanitizeToken(token);
            var metaPath = Path.Combine(StagingRoot, safeToken, "meta.json");
            if (!File.Exists(metaPath))
                throw new InvalidOperationException("This upload session has expired — start the upload again.");
            var json = await File.ReadAllTextAsync(metaPath);
            return JsonSerializer.Deserialize<StagingMeta>(json)
                ?? throw new InvalidOperationException("This upload session has expired — start the upload again.");
        }

        private async Task SaveMetaAsync(StagingMeta meta)
        {
            var dir = Path.Combine(StagingRoot, meta.Token);
            Directory.CreateDirectory(dir);
            var metaPath = Path.Combine(dir, "meta.json");
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta));
        }

        private static string SanitizeToken(string token)
        {
            // Tokens are our own GUIDs, but treat as untrusted since they round-trip through URLs.
            if (string.IsNullOrWhiteSpace(token) || !Regex.IsMatch(token, "^[a-f0-9]{32}$"))
                throw new InvalidOperationException("Invalid upload session.");
            return token;
        }

        public async Task<DatasetStagingInfo> StageUploadAsync(int clientId, IFormFile file, string? requestedTableName)
        {
            if (file == null || file.Length == 0)
                throw new InvalidOperationException("Choose a CSV or Excel file to upload.");

            var database = await GetDatabaseAsync(clientId)
                ?? throw new InvalidOperationException("Create a database for this engagement before uploading a file.");
            var schema = string.IsNullOrWhiteSpace(database.SchemaName) ? SchemaFor(clientId) : database.SchemaName;

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var excelType = extension switch
            {
                ".csv" => ExcelType.CSV,
                ".xlsx" => ExcelType.XLSX,
                _ => throw new InvalidOperationException("Only .csv or .xlsx files are supported.")
            };

            CleanupStaleStagingDirs();

            await using var connection = await OpenConnectionAsync();
            var tableCount = await CountTablesAsync(connection, schema);
            if (tableCount >= MaxTablesPerEngagement)
                throw new InvalidOperationException($"This engagement already has {MaxTablesPerEngagement} tables — the maximum allowed.");

            var token = Guid.NewGuid().ToString("N");
            var dir = Path.Combine(StagingRoot, token);
            Directory.CreateDirectory(dir);
            var originalPath = Path.Combine(dir, "original" + extension);
            await using (var fileStream = File.Create(originalPath))
                await file.CopyToAsync(fileStream);

            // Bounded on purpose — staging only needs a sample for the preview grid and type
            // inference, not the whole file. A multi-million-row file would otherwise make this
            // very first step of the wizard slow (or memory-heavy) for no benefit; the real
            // import streams the full file later, in the background, via StartCommitAsync.
            var (headers, rows) = ParseFile(originalPath, excelType, PreviewScanRows);
            if (rows.Count == 0)
            {
                Directory.Delete(dir, recursive: true);
                throw new InvalidOperationException("The file has no data rows.");
            }
            if (headers.Count > MaxColumnsPerTable)
            {
                Directory.Delete(dir, recursive: true);
                throw new InvalidOperationException($"File has {headers.Count} columns — the maximum per table is {MaxColumnsPerTable} (a Postgres limit). Split this file into multiple tables and upload separately.");
            }

            var columnNames = SanitizeColumnNames(headers);
            var columnTypes = InferColumnTypes(rows, headers);
            var tableBaseName = SanitizeIdentifier(string.IsNullOrWhiteSpace(requestedTableName)
                ? Path.GetFileNameWithoutExtension(file.FileName)
                : requestedTableName);
            if (await TableExistsAsync(connection, schema, tableBaseName))
                throw new InvalidOperationException($"A table named '{tableBaseName}' already exists in this engagement. Choose a different table name.");
            var tableName = tableBaseName;

            var isExact = rows.Count < PreviewScanRows;
            var meta = new StagingMeta
            {
                Token = token,
                ClientId = clientId,
                OriginalFileName = file.FileName,
                OriginalExtension = extension,
                TableName = tableName,
                TotalRowCount = rows.Count,
                TotalRowCountIsExact = isExact,
                CreatedAt = DateTime.UtcNow,
                Columns = headers.Select((h, i) => new DatasetStagedColumn
                {
                    OriginalHeader = h,
                    ColumnName = columnNames[i],
                    DataType = columnTypes[i].ToString(),
                    AllowNulls = true
                }).ToList()
            };
            await SaveMetaAsync(meta);

            return BuildStagingInfo(meta, headers, rows);
        }

        public async Task<DatasetStagingInfo> GetStagingAsync(string token)
        {
            var meta = await LoadMetaAsync(token);
            var (headers, rows) = ParseFile(
                Path.Combine(StagingRoot, meta.Token, "original" + meta.OriginalExtension),
                meta.OriginalExtension == ".csv" ? ExcelType.CSV : ExcelType.XLSX,
                PreviewScanRows);
            return BuildStagingInfo(meta, headers, rows);
        }

        public async Task<DatasetStagingInfo> UpdateStagingColumnsAsync(string token, string tableName, List<DatasetStagedColumn> columns)
        {
            var meta = await LoadMetaAsync(token);
            var updatedTableName = SanitizeIdentifier(string.IsNullOrWhiteSpace(tableName) ? meta.TableName : tableName);

            await using var connection = await OpenConnectionAsync();
            var schema = await GetSchemaNameAsync(meta.ClientId);
            if (!string.Equals(updatedTableName, meta.TableName, StringComparison.Ordinal) && await TableExistsAsync(connection, schema, updatedTableName))
                throw new InvalidOperationException($"A table named '{updatedTableName}' already exists in this engagement. Choose a different table name.");

            meta.TableName = updatedTableName;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var updated = new List<DatasetStagedColumn>();
            foreach (var original in meta.Columns)
            {
                var edited = columns.FirstOrDefault(c => c.OriginalHeader == original.OriginalHeader);
                var requestedName = SanitizeIdentifier(edited?.ColumnName ?? original.ColumnName);
                var uniqueName = requestedName;
                var n = 2;
                while (!seen.Add(uniqueName))
                {
                    uniqueName = $"{requestedName}_{n}";
                    n++;
                }

                var requestedType = edited?.DataType ?? original.DataType;
                var dataType = ValidateSqlType(requestedType)
                    ?? throw new InvalidOperationException($"Invalid SQL data type: '{requestedType}'.");

                updated.Add(new DatasetStagedColumn
                {
                    OriginalHeader = original.OriginalHeader,
                    ColumnName = uniqueName,
                    DataType = dataType,
                    AllowNulls = edited?.AllowNulls ?? original.AllowNulls
                });
            }
            meta.Columns = updated;
            await SaveMetaAsync(meta);

            // Callers only use this to persist the edited columns before calling
            // StartCommitAsync, not to re-render a preview — skip re-reading the file (a full
            // pass would otherwise run on every commit regardless of file size).
            return new DatasetStagingInfo
            {
                Token = meta.Token,
                ClientId = meta.ClientId,
                OriginalFileName = meta.OriginalFileName,
                TableName = meta.TableName,
                Columns = meta.Columns,
                TotalRowCount = meta.TotalRowCount,
                TotalRowCountIsExact = meta.TotalRowCountIsExact
            };
        }

        // Fast, synchronous part of a commit: validates, creates the (empty) table, and hands
        // the actual data import off to a background job — so the HTTP request that started
        // the upload returns immediately regardless of how many rows the file has, instead of
        // staying open (and risking a timeout) for the whole import.
        public async Task<int> StartCommitAsync(string token, ApplicationUser user)
        {
            var meta = await LoadMetaAsync(token);

            _ = await GetDatabaseAsync(meta.ClientId)
                ?? throw new InvalidOperationException("This engagement's database no longer exists — start again.");

            await using var connection = await OpenConnectionAsync();
            var schema = await GetSchemaNameAsync(meta.ClientId);

            await using (var schemaCommand = connection.CreateCommand())
            {
                schemaCommand.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{schema}\";";
                await schemaCommand.ExecuteNonQueryAsync();
            }

            var tableCount = await CountTablesAsync(connection, schema);
            if (tableCount >= MaxTablesPerEngagement)
                throw new InvalidOperationException($"This engagement already has {MaxTablesPerEngagement} tables — the maximum allowed.");

            if (await TableExistsAsync(connection, schema, meta.TableName))
                throw new InvalidOperationException($"A table named '{meta.TableName}' already exists in this engagement.");

            var tableName = meta.TableName;

            var columnTypeNames = meta.Columns.Select(c => MapSqlServerTypeToPostgresType(c.DataType)).ToList();
            var createSql = $"CREATE TABLE \"{schema}\".\"{tableName}\" (\"RowId\" bigserial PRIMARY KEY, " +
                string.Join(", ", meta.Columns.Select((c, i) =>
                    $"\"{c.ColumnName}\" {columnTypeNames[i]}{(c.AllowNulls ? "" : " NOT NULL")}")) + ");";
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = createSql;
                await create.ExecuteNonQueryAsync();
            }

            // Uploaded client data is confidential audit evidence. Enabling RLS with no policies
            // blocks Supabase's PostgREST anon/authenticated API roles from reading or writing it
            // while leaving this app's own connection (the table owner) fully able to read/write,
            // since table owners bypass RLS by default in Postgres.
            await using (var rls = connection.CreateCommand())
            {
                rls.CommandText = $"ALTER TABLE \"{schema}\".\"{tableName}\" ENABLE ROW LEVEL SECURITY;";
                await rls.ExecuteNonQueryAsync();
            }

            int jobId;
            await using (var insertJob = connection.CreateCommand())
            {
                insertJob.CommandText = @"
                    INSERT INTO ""DatasetUploadJobs"" (""ClientID"", ""TableName"", ""Token"", ""Status"", ""TotalRows"", ""CreatedByUserID"")
                    VALUES (@clientId, @tableName, @token, 'Queued', @totalRows, @userId)
                    RETURNING ""JobID"";";
                insertJob.Parameters.AddWithValue("clientId", meta.ClientId);
                insertJob.Parameters.AddWithValue("tableName", tableName);
                insertJob.Parameters.AddWithValue("token", token);
                insertJob.Parameters.AddWithValue("totalRows", (object?)(meta.TotalRowCountIsExact ? meta.TotalRowCount : (long?)null) ?? DBNull.Value);
                insertJob.Parameters.AddWithValue("userId", user.Id);
                jobId = (int)(await insertJob.ExecuteScalarAsync())!;
            }

            var clientId = meta.ClientId;
            var userId = user.Id;
            var userEmail = user.Email;
            _queue.QueueBackgroundWorkItem(async (services, ct) =>
            {
                var svc = services.GetRequiredService<IEngagementDatasetService>();
                await svc.RunCommitJobAsync(jobId, ct);

                var status = await svc.GetUploadJobStatusAsync(jobId, clientId);
                if (status == null) return;
                var audit = services.GetRequiredService<IAuditLogService>();
                if (status.Status == "Completed")
                    await audit.LogAsync("upload_dataset", $"Uploaded '{status.TableName}' ({status.ProcessedRows} rows) for engagement {clientId}", userId, userEmail);
                else if (status.Status == "Failed")
                    await audit.LogAsync("upload_dataset_failed", $"Failed importing '{status.TableName}' for engagement {clientId}: {status.ErrorMessage}", userId, userEmail);
            });

            return jobId;
        }

        // Runs on the background queue (see QueuedHostedService) — streams the staged file
        // straight into a Postgres COPY BINARY import without ever materializing the whole
        // file in memory, so file size only affects how long this takes, not whether it fits.
        public async Task RunCommitJobAsync(int jobId, CancellationToken cancellationToken)
        {
            await using var jobConnection = await OpenConnectionAsync();
            var job = await LoadJobRowAsync(jobConnection, jobId);
            if (job == null) return;

            await SetJobRunningAsync(jobConnection, jobId);

            StagingMeta meta;
            try
            {
                meta = await LoadMetaAsync(job.Token);
            }
            catch (Exception ex)
            {
                await FailJobAsync(jobConnection, job, ex.Message);
                return;
            }

            var schema = await GetSchemaNameAsync(job.ClientId);
            var dir = Path.Combine(StagingRoot, meta.Token);
            var path = Path.Combine(dir, "original" + meta.OriginalExtension);
            var excelType = meta.OriginalExtension == ".csv" ? ExcelType.CSV : ExcelType.XLSX;

            long processed = 0;
            var lastProgressWrite = DateTime.UtcNow;

            // A dedicated connection for progress UPDATEs — the import connection stays busy
            // inside the COPY protocol for the whole run and can't service other commands.
            await using var progressConnection = await OpenConnectionAsync();

            try
            {
                // Unlimited command timeout: a multi-hundred-million-row COPY can legitimately run
                // far longer than the app's normal 60s query timeout, and this is a background job
                // (tracked via DatasetUploadJobs, cancellable via the CancellationToken) rather than
                // a request a user is blocked waiting on, so there's no reason to cap it client-side.
                await using var importConnection = await OpenConnectionAsync(commandTimeoutSeconds: 0);
                await using (var setTimeout = importConnection.CreateCommand())
                {
                    // Belt-and-braces: also clear any server/role-level statement_timeout default
                    // for this session, in case one is set independently of Npgsql's own timeout.
                    setTimeout.CommandText = "SET statement_timeout = 0;";
                    await setTimeout.ExecuteNonQueryAsync();
                }

                var copySql = $"COPY \"{schema}\".\"{job.TableName}\" (" +
                    string.Join(", ", meta.Columns.Select(c => $"\"{c.ColumnName}\"")) + ") FROM STDIN (FORMAT BINARY)";

                var raggedRowNumbers = new List<long>();
                await using (var importer = await importConnection.BeginBinaryImportAsync(copySql))
                {
                    foreach (var (row, wasRagged) in StreamRowsForFile(path, excelType))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await importer.StartRowAsync();
                        foreach (var column in meta.Columns)
                        {
                            var raw = row.TryGetValue(column.OriginalHeader, out var v) ? v : null;
                            if (!column.AllowNulls && (raw == null || string.IsNullOrWhiteSpace(raw.ToString())))
                                throw new InvalidOperationException($"Column '{column.ColumnName}' is set to not allow blanks, but this row has no value for it.");
                            await WriteValueAsync(importer, raw, column.DataType);
                        }
                        processed++;
                        // A ragged row (field count didn't match the header) was still imported
                        // best-effort — capture which ones, up to a sane cap, so the analyst can
                        // go check those specific rows in the source file after the fact.
                        if (wasRagged && raggedRowNumbers.Count < 50)
                            raggedRowNumbers.Add(processed);

                        if (processed % 2000 == 0 || (DateTime.UtcNow - lastProgressWrite) > TimeSpan.FromSeconds(2))
                        {
                            await UpdateJobProgressAsync(progressConnection, jobId, processed);
                            lastProgressWrite = DateTime.UtcNow;
                        }
                    }
                    await importer.CompleteAsync();
                }

                string? warning = null;
                if (raggedRowNumbers.Count > 0)
                {
                    var shown = string.Join(", ", raggedRowNumbers.Take(20));
                    var more = raggedRowNumbers.Count > 20 ? $" and {raggedRowNumbers.Count - 20} more" : "";
                    warning = $"Imported, but {raggedRowNumbers.Count} row(s) had a different number of columns than the header row (rows: {shown}{more}) — likely an unescaped comma in the source file. Those rows' values may be shifted into the wrong columns; review them in the table.";
                }

                await CompleteJobAsync(progressConnection, jobId, processed, warning);
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
            }
            catch (Exception ex)
            {
                await using var cleanup = await OpenConnectionAsync();
                await using (var drop = cleanup.CreateCommand())
                {
                    drop.CommandText = $"DROP TABLE IF EXISTS \"{schema}\".\"{job.TableName}\";";
                    try { await drop.ExecuteNonQueryAsync(); } catch { /* best-effort */ }
                }
                await FailJobAsync(cleanup, job, DescribeImportFailure(ex, processed));
            }
        }

        // Turns a raw parsing/library exception into something an analyst can act on, anchored
        // to the row it happened near (rows before this one already streamed through fine).
        private static string DescribeImportFailure(Exception ex, long processed)
        {
            var nearRow = processed + 1;
            if (ex is KeyNotFoundException)
            {
                // MiniExcel's CSV reader throws this internally when a data row has MORE fields
                // than the header row did — almost always an unescaped/unquoted comma (or a stray
                // delimiter) inside a text value a few rows past the last one that imported clean.
                return $"Import stopped near row {nearRow:N0} — that row (or one shortly after it) has more columns " +
                       "than the header row. This is usually an unescaped comma inside a text value in the source " +
                       "file (e.g. an address or name containing a comma that isn't quoted). Fix that row in the " +
                       "source file and upload again.";
            }
            return $"Import stopped near row {nearRow:N0}: {ex.Message}";
        }

        public async Task<DatasetUploadJobStatus?> GetUploadJobStatusAsync(int jobId, int clientId)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT ""JobID"", ""Status"", ""TableName"", ""TotalRows"", ""ProcessedRows"", ""ErrorMessage""
                FROM ""DatasetUploadJobs"" WHERE ""JobID"" = @jobId AND ""ClientID"" = @clientId;";
            command.Parameters.AddWithValue("jobId", jobId);
            command.Parameters.AddWithValue("clientId", clientId);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            return new DatasetUploadJobStatus
            {
                JobId = reader.GetInt32(0),
                Status = reader.GetString(1),
                TableName = reader.GetString(2),
                TotalRows = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                ProcessedRows = reader.GetInt64(4),
                ErrorMessage = reader.IsDBNull(5) ? null : reader.GetString(5)
            };
        }

        private class UploadJobRow
        {
            public int JobId { get; set; }
            public int ClientId { get; set; }
            public string TableName { get; set; } = "";
            public string Token { get; set; } = "";
        }

        private static async Task<UploadJobRow?> LoadJobRowAsync(NpgsqlConnection connection, int jobId)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT ""JobID"", ""ClientID"", ""TableName"", ""Token"" FROM ""DatasetUploadJobs"" WHERE ""JobID"" = @jobId;";
            command.Parameters.AddWithValue("jobId", jobId);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return new UploadJobRow
            {
                JobId = reader.GetInt32(0),
                ClientId = reader.GetInt32(1),
                TableName = reader.GetString(2),
                Token = reader.GetString(3)
            };
        }

        private static async Task SetJobRunningAsync(NpgsqlConnection connection, int jobId)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE ""DatasetUploadJobs"" SET ""Status"" = 'Running', ""StartedAt"" = now() WHERE ""JobID"" = @jobId;";
            command.Parameters.AddWithValue("jobId", jobId);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task UpdateJobProgressAsync(NpgsqlConnection connection, int jobId, long processed)
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = @"UPDATE ""DatasetUploadJobs"" SET ""ProcessedRows"" = @processed WHERE ""JobID"" = @jobId;";
                command.Parameters.AddWithValue("processed", processed);
                command.Parameters.AddWithValue("jobId", jobId);
                await command.ExecuteNonQueryAsync();
            }
            catch
            {
                // Progress reporting is best-effort — never let it take down the import itself.
            }
        }

        private static async Task CompleteJobAsync(NpgsqlConnection connection, int jobId, long processed, string? warning = null)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE ""DatasetUploadJobs""
                SET ""Status"" = 'Completed', ""ProcessedRows"" = @processed, ""TotalRows"" = @processed, ""CompletedAt"" = now(), ""ErrorMessage"" = @warning
                WHERE ""JobID"" = @jobId;";
            command.Parameters.AddWithValue("processed", processed);
            command.Parameters.AddWithValue("jobId", jobId);
            command.Parameters.AddWithValue("warning", (object?)warning ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task FailJobAsync(NpgsqlConnection connection, UploadJobRow job, string message)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE ""DatasetUploadJobs"" SET ""Status"" = 'Failed', ""ErrorMessage"" = @message, ""CompletedAt"" = now() WHERE ""JobID"" = @jobId;";
            command.Parameters.AddWithValue("message", message);
            command.Parameters.AddWithValue("jobId", job.JobId);
            await command.ExecuteNonQueryAsync();
        }

        public Task CancelStagingAsync(string token)
        {
            var safeToken = SanitizeToken(token);
            var dir = Path.Combine(StagingRoot, safeToken);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return Task.CompletedTask;
        }

        private static DatasetStagingInfo BuildStagingInfo(StagingMeta meta, List<string> headers, List<IDictionary<string, object>> rows)
        {
            var preview = rows.Take(PreviewRowCount).Select(row =>
            {
                var dict = new Dictionary<string, string?>();
                foreach (var h in headers)
                    dict[h] = row.TryGetValue(h, out var v) && v != null ? v.ToString() : null;
                return dict;
            }).ToList();

            return new DatasetStagingInfo
            {
                Token = meta.Token,
                ClientId = meta.ClientId,
                OriginalFileName = meta.OriginalFileName,
                TableName = meta.TableName,
                Columns = meta.Columns,
                PreviewRows = preview,
                TotalRowCount = meta.TotalRowCount,
                TotalRowCountIsExact = meta.TotalRowCountIsExact
            };
        }

        // Reads only enough of the file to decide the header names (row 0, via bounded .Take(1)
        // scans) — never the whole file — so this decision costs the same for a 10-row file and
        // a 500-million-row file. Every branch here treats the file's physical first row as the
        // header and every subsequent row as data — the two MiniExcel queries below only differ
        // in how they decide what to *name* the columns.
        private static List<string> DetectHeaders(string path, ExcelType excelType)
        {
            using var rawStream = File.OpenRead(path);
            var firstRaw = MiniExcel.Query(rawStream, useHeaderRow: false, excelType: excelType)
                .Cast<IDictionary<string, object>>()
                .Take(1)
                .ToList();

            if (firstRaw.Count == 0)
                return new List<string>();

            var rawHeaderCandidate = DeduplicateHeaders(firstRaw[0].Values
                .Select((value, index) => string.IsNullOrWhiteSpace(value?.ToString()) ? $"col_{index + 1}" : value!.ToString()!.Trim())
                .ToList());

            if (LooksLikeHeaderRowValues(firstRaw[0].Values.ToList()))
                return rawHeaderCandidate;

            using var headerStream = File.OpenRead(path);
            var firstWithHeader = MiniExcel.Query(headerStream, useHeaderRow: true, excelType: excelType)
                .Cast<IDictionary<string, object>>()
                .Take(1)
                .ToList();

            if (firstWithHeader.Count == 0)
                return new List<string>();

            var headers = firstWithHeader[0].Keys.ToList();
            if (LooksLikeGenericHeaders(headers) && LooksLikeHeaderRowValues(firstWithHeader[0].Values.ToList()))
                return rawHeaderCandidate;

            return headers;
        }

        // Lazily yields every data row keyed by the already-decided headers — the file is read
        // once, forward-only, and nothing beyond the current row is ever held in memory.
        //
        // Always reads raw (useHeaderRow: false) and maps values to headers positionally by
        // index, rather than letting MiniExcel build its own header-keyed dictionary per row
        // (useHeaderRow: true). MiniExcel's own header-keyed mode throws a raw
        // KeyNotFoundException deep inside its CSV reader the moment a data row has a different
        // column count than the header row (e.g. an unescaped comma inside a text value) — a
        // realistic occurrence in any multi-million-row file. Positional mapping via
        // MapRowValuesToHeaders is already bounds-checked (extra values are ignored, missing
        // ones become null), so a ragged row degrades gracefully instead of aborting the import.
        private static IEnumerable<(IDictionary<string, object> Row, bool WasRagged)> StreamRows(string path, ExcelType excelType, List<string> headers)
        {
            using var stream = File.OpenRead(path);
            var query = MiniExcel.Query(stream, useHeaderRow: false, excelType: excelType).Cast<IDictionary<string, object>>().Skip(1);
            foreach (var row in query)
                yield return (MapRowValuesToHeaders(row, headers), row.Count != headers.Count);
        }

        // Streams the whole file — used by the background import job (RunCommitJobAsync), which
        // needs every row but must never hold them all in memory at once. WasRagged flags a row
        // whose raw field count didn't match the header count (extra/missing values were still
        // mapped best-effort — see StreamRows) so the caller can warn the analyst instead of
        // silently importing possibly-misaligned data.
        private static IEnumerable<(IDictionary<string, object> Row, bool WasRagged)> StreamRowsForFile(string path, ExcelType excelType)
        {
            var headers = DetectHeaders(path, excelType);
            if (headers.Count == 0) yield break;
            foreach (var entry in StreamRows(path, excelType, headers))
                yield return entry;
        }

        // maxRows bounds staging/preview reads so those wizard steps stay fast regardless of
        // file size; omit it (as the background import does via StreamRowsForFile) to get every
        // row without ever building this List at all.
        private static (List<string> Headers, List<IDictionary<string, object>> Rows) ParseFile(string path, ExcelType excelType, int? maxRows = null)
        {
            var headers = DetectHeaders(path, excelType);
            if (headers.Count == 0)
                return (headers, new List<IDictionary<string, object>>());

            var query = StreamRows(path, excelType, headers).Select(entry => entry.Row);
            var rows = maxRows.HasValue ? query.Take(maxRows.Value).ToList() : query.ToList();
            return (headers, rows);
        }

        private static bool LooksLikeGenericHeaders(List<string> headers)
        {
            if (headers.Count == 0) return true;

            return headers.All(h => string.IsNullOrWhiteSpace(h)
                || Regex.IsMatch(h, "^(Column|column)\\d+$")
                || Regex.IsMatch(h, "^[A-Z]+$")
                || Regex.IsMatch(h, "^[A-Z]+_\\d+$")
                || Regex.IsMatch(h, "^[A-Z]+_?[A-Z]+$")
                || Regex.IsMatch(h, "^col_\\d+$", RegexOptions.IgnoreCase));
        }

        private static bool LooksLikeHeaderRowValues(List<object> values)
        {
            if (values.Count == 0) return false;

            var headerLike = 0;
            var total = 0;

            foreach (var value in values)
            {
                if (value == null) continue;
                var text = value.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                total++;
                if (Regex.IsMatch(text, "^[A-Za-z][A-Za-z0-9_ ]*$") && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) && !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && !bool.TryParse(text, out _))
                {
                    headerLike++;
                }
            }

            return total > 0 && headerLike * 2 >= total;
        }

        // Two columns sharing the exact same header text would otherwise collide as the same key
        // in the row dictionaries built below, silently discarding every value from all but the
        // last duplicate — even though the final table gets unique column names further down the
        // pipeline. Renaming duplicates here (before any row is ever keyed by header text) is what
        // actually keeps every column's data intact.
        private static List<string> DeduplicateHeaders(List<string> headers)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>(headers.Count);
            foreach (var header in headers)
            {
                var candidate = header;
                var n = 2;
                while (!seen.Add(candidate))
                    candidate = $"{header}_{n++}";
                result.Add(candidate);
            }
            return result;
        }

        private static IDictionary<string, object> MapRowValuesToHeaders(IDictionary<string, object> row, List<string> headers)
        {
            var values = row.Values.ToList();
            var result = new Dictionary<string, object>();

            for (var i = 0; i < headers.Count; i++)
            {
                result[headers[i]] = i < values.Count ? values[i] : null!;
            }

            return result;
        }

        // ── Type inference (used at staging time as the initial suggestion) ────────────
        private static List<string> InferColumnTypes(List<IDictionary<string, object>> rows, List<string> headers)
        {
            var types = new List<string>();
            var sample = rows.Take(TypeSampleRows).ToList();

            foreach (var header in headers)
            {
                var values = sample
                    .Where(row => row.TryGetValue(header, out var value) && value != null)
                    .Select(row => row[header]!.ToString()!.Trim())
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                types.Add(InferSqlDataType(values));
            }

            return types;
        }

        private static string InferSqlDataType(List<string> values)
        {
            if (values.Count == 0)
                return "nvarchar(max)";

            if (values.All(IsBooleanValue))
                return "bit";

            if (values.All(IsGuidValue))
                return "uniqueidentifier";

            if (values.All(IsTimeValue))
                return "time";

            if (values.All(IsDateTimeOffsetValue))
                return "datetimeoffset";

            if (values.All(IsDateValue))
                return "date";

            if (values.All(IsDateTimeValue))
                return "datetime2";

            if (values.All(IsIntegerValue))
                return ChooseIntegerType(values);

            if (values.All(IsDecimalValue))
                return ChooseNumericType(values);

            return ChooseTextType(values);
        }

        private static bool IsBooleanValue(string text)
        {
            return bool.TryParse(text, out _) || text == "0" || text == "1";
        }

        private static bool IsGuidValue(string text)
        {
            return Guid.TryParse(text, out _);
        }

        private static bool IsTimeValue(string text)
        {
            // Must look like a clock time (H:MM[:SS[.fff]]), not just anything TimeSpan.Parse
            // accepts — a bare integer like "5" parses as a 5-day TimeSpan, which would
            // silently misclassify plain numeric columns as "time" and overflow Postgres's
            // time-of-day range (0 to 24:00:00) on import.
            if (!Regex.IsMatch(text, @"^\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?$"))
                return false;
            return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts)
                && ts >= TimeSpan.Zero && ts <= TimeSpan.FromHours(24);
        }

        private static bool IsDateTimeOffsetValue(string text)
        {
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _)
                && (Regex.IsMatch(text, @"[Zz]$") || Regex.IsMatch(text, @"[+-]\d{2}:\d{2}$"));
        }

        private static bool IsDateValue(string text)
        {
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
                return false;
            return dt.TimeOfDay == TimeSpan.Zero && !Regex.IsMatch(text, @"\d{1,2}:\d{2}");
        }

        private static bool IsDateTimeValue(string text)
        {
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _))
                return false;
            return Regex.IsMatch(text, @"\d{1,2}:\d{2}");
        }

        private static bool IsIntegerValue(string text)
        {
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        private static bool IsDecimalValue(string text)
        {
            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
        }

        private static string ChooseIntegerType(List<string> values)
        {
            var min = long.MaxValue;
            var max = long.MinValue;

            foreach (var text in values)
            {
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    return "int";
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            if (min >= 0 && max <= 255) return "tinyint";
            if (min >= short.MinValue && max <= short.MaxValue) return "smallint";
            if (min >= int.MinValue && max <= int.MaxValue) return "int";
            return "bigint";
        }

        private static string ChooseNumericType(List<string> values)
        {
            var precision = 0;
            var scale = 0;

            foreach (var text in values)
            {
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                    return "numeric(18,2)";

                var trimmed = text.Trim();
                var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var digitsBefore = parts[0].TrimStart('-', '+').Length;
                var currentScale = parts.Length > 1 ? parts[1].Length : 0;
                precision = Math.Max(precision, digitsBefore + currentScale);
                scale = Math.Max(scale, currentScale);
            }

            precision = Math.Clamp(precision, 1, 38);
            scale = Math.Clamp(scale, 0, precision);

            if (scale == 0 && values.All(IsIntegerValue))
                return ChooseIntegerType(values);

            return $"numeric({precision},{scale})";
        }

        // Always suggests the unbounded type rather than sizing a varchar(N) off the sample.
        // Type inference only ever looks at a bounded sample of rows (TypeSampleRows, drawn
        // from the staging preview scan) — for a large file, a later row can easily be longer
        // than anything in that sample, and a too-narrow varchar(N) fails the import partway
        // through (a real Postgres 22001 "value too long" seen in production on a >1M-row
        // file). Postgres's text/nvarchar(max) has no storage or performance cost over a
        // fixed-width varchar(N), so there's no real tradeoff — the analyst can still narrow it
        // deliberately in the Modify Columns step if they specifically want a length constraint.
        private static string ChooseTextType(List<string> values) => "nvarchar(max)";

        private static string? ValidateSqlType(string rawDataType)
        {
            if (string.IsNullOrWhiteSpace(rawDataType))
                return null;

            var normalized = NormalizeSqlType(rawDataType);
            return ValidSqlTypeRegex.IsMatch(normalized) ? normalized : null;
        }

        private static string NormalizeSqlType(string rawDataType)
        {
            var s = (rawDataType ?? "").Trim();
            s = Regex.Replace(s, @"\s+", " ");
            s = Regex.Replace(s, @"\s*\(\s*", "(");
            s = Regex.Replace(s, @"\s*\)\s*", ")");
            return s.ToLowerInvariant();
        }

        private static string MapSqlServerTypeToPostgresType(string sqlType)
        {
            var normalized = NormalizeSqlType(sqlType);
            var baseType = GetSqlTypeBase(normalized);

            return baseType switch
            {
                "tinyint" => "smallint",
                "smallint" => "smallint",
                "int" => "integer",
                "bigint" => "bigint",
                "bit" => "boolean",
                "money" => "numeric(19,4)",
                "smallmoney" => "numeric(10,4)",
                "float" => "double precision",
                "real" => "real",
                "date" => "date",
                "time" => "time",
                "datetimeoffset" => "timestamptz",
                "datetime2" => "timestamptz",
                "datetime" => "timestamptz",
                "smalldatetime" => "timestamptz",
                "timestamp" => "timestamptz",
                "uniqueidentifier" => "uuid",
                "binary" => "bytea",
                "varbinary" => "bytea",
                "xml" => "text",
                "hierarchyid" => "text",
                "geography" => "text",
                "geometry" => "text",
                "sql_variant" => "text",
                "text" => "text",
                "ntext" => "text",
                "char" => normalized.Contains("max") ? "text" : normalized,
                "nchar" => normalized.Contains("max") ? "text" : $"char{normalized.Substring(5)}",
                "varchar" => normalized.Contains("max") ? "text" : normalized,
                "nvarchar" => normalized.Contains("max") ? "text" : $"varchar{normalized.Substring(8)}",
                "decimal" => normalized,
                "numeric" => normalized,
                _ => "text"
            };
        }

        private static string GetSqlTypeBase(string normalizedType)
        {
            var index = normalizedType.IndexOf('(');
            return index < 0 ? normalizedType : normalizedType[..index];
        }

        private static async Task WriteValueAsync(NpgsqlBinaryImporter importer, object? raw, string sqlType)
        {
            var text = raw?.ToString();
            if (raw == null || string.IsNullOrWhiteSpace(text))
            {
                await importer.WriteNullAsync();
                return;
            }

            var normalized = NormalizeSqlType(sqlType);
            var baseType = GetSqlTypeBase(normalized);

            switch (baseType)
            {
                case "bit":
                    await importer.WriteAsync(ParseBoolean(text), NpgsqlDbType.Boolean);
                    break;
                case "tinyint":
                case "smallint":
                    await importer.WriteAsync(short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture), NpgsqlDbType.Smallint);
                    break;
                case "int":
                    await importer.WriteAsync(int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture), NpgsqlDbType.Integer);
                    break;
                case "bigint":
                    await importer.WriteAsync(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture), NpgsqlDbType.Bigint);
                    break;
                case "decimal":
                case "numeric":
                case "money":
                case "smallmoney":
                    await importer.WriteAsync(decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture), NpgsqlDbType.Numeric);
                    break;
                case "float":
                    await importer.WriteAsync(double.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture), NpgsqlDbType.Double);
                    break;
                case "real":
                    await importer.WriteAsync(float.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture), NpgsqlDbType.Real);
                    break;
                case "date":
                    await importer.WriteAsync(DateTime.Parse(text, CultureInfo.InvariantCulture).Date, NpgsqlDbType.Date);
                    break;
                case "time":
                    await importer.WriteAsync(ParseTimeOfDay(text), NpgsqlDbType.Time);
                    break;
                case "datetimeoffset":
                case "datetime2":
                case "datetime":
                case "smalldatetime":
                case "timestamp":
                    var dt = DateTime.Parse(text, CultureInfo.InvariantCulture);
                    await importer.WriteAsync(DateTime.SpecifyKind(dt, DateTimeKind.Utc), NpgsqlDbType.TimestampTz);
                    break;
                case "uniqueidentifier":
                    await importer.WriteAsync(Guid.Parse(text), NpgsqlDbType.Uuid);
                    break;
                case "binary":
                case "varbinary":
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        await importer.WriteAsync(HexStringToBytes(text.Substring(2)), NpgsqlDbType.Bytea);
                    else
                        await importer.WriteAsync(Convert.FromBase64String(text), NpgsqlDbType.Bytea);
                    break;
                default:
                    await importer.WriteAsync(text, NpgsqlDbType.Text);
                    break;
            }
        }

        private static TimeSpan ParseTimeOfDay(string text)
        {
            var ts = TimeSpan.Parse(text, CultureInfo.InvariantCulture);
            if (ts < TimeSpan.Zero || ts > TimeSpan.FromHours(24))
                throw new OverflowException($"'{text}' is out of range for a time-of-day column (must be between 00:00:00 and 24:00:00).");
            return ts;
        }

        private static bool ParseBoolean(string text)
        {
            if (bool.TryParse(text, out var result))
                return result;
            if (text == "0") return false;
            if (text == "1") return true;
            throw new FormatException($"Invalid boolean value '{text}'.");
        }

        private static byte[] HexStringToBytes(string hex)
        {
            if (hex.Length % 2 != 0) hex = "0" + hex;
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        // ── Identifier sanitization ────────────────────────────────────────────────────
        private static string SanitizeIdentifier(string raw)
        {
            // Every column/table name this app builds into SQL is always double-quoted
            // ("schema"."table", "column"), so Postgres has no problem with a name that starts
            // with a digit or matches a reserved word — quoting sidesteps both restrictions.
            // No need to prefix or rewrite anything beyond making the characters themselves valid.
            var s = (raw ?? "").Trim().ToLowerInvariant();
            s = Regex.Replace(s, "[^a-z0-9_]+", "_");
            if (s.Length == 0) s = "col";
            if (s.Length > 63) s = s.Substring(0, 63);
            return s;
        }

        private static List<string> SanitizeColumnNames(List<string> headers)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;

            foreach (var header in headers)
            {
                index++;
                var candidate = SanitizeIdentifier(string.IsNullOrWhiteSpace(header) ? $"col_{index}" : header);
                var unique = candidate;
                var n = 2;
                while (!seen.Add(unique))
                {
                    unique = $"{candidate}_{n}";
                    n++;
                }
                result.Add(unique);
            }

            return result;
        }

        private async Task<string> ResolveUniqueTableNameAsync(NpgsqlConnection connection, string schema, string baseName)
        {
            var candidate = baseName;
            var n = 2;
            while (await TableExistsAsync(connection, schema, candidate))
            {
                candidate = $"{baseName}_{n}";
                n++;
            }
            return candidate;
        }

        private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string schema, string table)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @s AND table_name = @t);";
            command.Parameters.AddWithValue("s", schema);
            command.Parameters.AddWithValue("t", table);
            return (bool)(await command.ExecuteScalarAsync())!;
        }

        private static async Task<int> CountTablesAsync(NpgsqlConnection connection, string schema)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM information_schema.tables WHERE table_schema = @s;";
            command.Parameters.AddWithValue("s", schema);
            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        // Never trust a caller-supplied table/column name directly — always resolve it back
        // through the catalog for this engagement's schema first.
        private static async Task<string> RequireExistingTableAsync(NpgsqlConnection connection, string schema, string tableName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = @s AND table_name = @t;";
            command.Parameters.AddWithValue("s", schema);
            command.Parameters.AddWithValue("t", tableName);
            var result = await command.ExecuteScalarAsync() as string;
            return result ?? throw new InvalidOperationException("Table not found in this engagement.");
        }

        private static async Task<List<string>> GetColumnsAsync(NpgsqlConnection connection, string schema, string table)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = @s AND table_name = @t AND column_name <> 'RowId'
                ORDER BY ordinal_position;";
            command.Parameters.AddWithValue("s", schema);
            command.Parameters.AddWithValue("t", table);
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
            return columns;
        }

        // ── Used by rule engines reading uploaded data instead of a live SQL Server ────
        // Always re-validates the table against the catalog before returning its columns —
        // never trust a caller-supplied table name directly, per the safety invariant used
        // throughout this file.
        public async Task<List<string>> GetValidatedColumnsAsync(int clientId, string tableName)
        {
            await using var connection = await OpenConnectionAsync();
            var schema = await GetSchemaNameAsync(clientId);
            var table = await RequireExistingTableAsync(connection, schema, tableName);
            return await GetColumnsAsync(connection, schema, table);
        }

        public async Task<List<DatasetDistinctValue>> GetDistinctColumnValuesAsync(int clientId, string tableName, string columnName, int take = 20)
        {
            await using var connection = await OpenConnectionAsync();
            var schema = await GetSchemaNameAsync(clientId);
            var table = await RequireExistingTableAsync(connection, schema, tableName);
            var validColumns = await GetColumnsAsync(connection, schema, table);
            if (!validColumns.Contains(columnName, StringComparer.Ordinal))
                throw new InvalidOperationException($"Column '{columnName}' was not found in table '{table}'.");

            await using var command = connection.CreateCommand();
            command.CommandText = $@"
                SELECT TRIM(CAST(""{columnName}"" AS text)) AS value, COUNT(*) AS record_count
                FROM ""{schema}"".""{table}""
                WHERE ""{columnName}"" IS NOT NULL
                GROUP BY TRIM(CAST(""{columnName}"" AS text))
                ORDER BY COUNT(*) DESC, value ASC
                LIMIT @take;";
            command.Parameters.AddWithValue("take", Math.Clamp(take, 1, 200));

            var results = new List<DatasetDistinctValue>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new DatasetDistinctValue
                {
                    Value = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Count = reader.GetInt64(1)
                });
            }
            return results;
        }

        public async Task EnsureJoinIndexAsync(int clientId, string tableName, string columnName)
        {
            var schema = await GetSchemaNameAsync(clientId);
            // Unlimited timeout: building an index over a very large uploaded table (hundreds of
            // thousands to millions of rows) can legitimately take longer than the app's normal
            // 60s query timeout the first time a column is used as a join/filter key.
            await using var connection = await OpenConnectionAsync(commandTimeoutSeconds: 0);
            var table = await RequireExistingTableAsync(connection, schema, tableName);
            var validColumns = await GetColumnsAsync(connection, schema, table);
            if (!validColumns.Contains(columnName, StringComparer.Ordinal))
                return; // caller's own column-existence check will raise a clearer error

            // Name is a stable hash of schema+table+column rather than those values directly —
            // keeps it within Postgres's 63-byte identifier limit regardless of table/column
            // name length, and collision-free without needing to sanitize arbitrary characters.
            var indexName = "idx_join_" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{schema}.{table}.{columnName}")))[..24].ToLowerInvariant();

            await using var command = connection.CreateCommand();
            command.CommandText = $@"CREATE INDEX IF NOT EXISTS ""{indexName}""
                ON ""{schema}"".""{table}"" ((UPPER(TRIM(CAST(""{columnName}"" AS text)))));";
            await command.ExecuteNonQueryAsync();
        }

        public async Task ExportTableCsvAsync(int clientId, string tableName, Stream destination)
        {
            await using var connection = await OpenConnectionAsync();
            var schema = await GetSchemaNameAsync(clientId);
            var table = await RequireExistingTableAsync(connection, schema, tableName);
            var columns = await GetColumnsAsync(connection, schema, table);
            var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));

            using var reader = await connection.BeginTextExportAsync(
                $"COPY (SELECT {columnList} FROM \"{schema}\".\"{table}\") TO STDOUT WITH (FORMAT csv, HEADER true);");
            await using var writer = new StreamWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
            var buffer = new char[8192];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                await writer.WriteAsync(buffer, 0, read);
            await writer.FlushAsync();
        }

        // ── Listing / browsing ─────────────────────────────────────────────────────────
        // Fixed at 3 round trips total regardless of table count (was 2 + 2*N — every table
        // used to cost two more round trips to Supabase, which is real, noticeable latency
        // from outside its eu-north-1 region).
        // Fast path for table-picker dropdowns: a single catalog query, no per-table COUNT(*)
        // scan (that's what made ListTablesAsync slow — and, on large tables against a flaky
        // Supabase link, prone to timing out entirely — for callers that only ever use the name).
        public async Task<List<string>> ListTableNamesAsync(int clientId)
        {
            await using var connection = await OpenConnectionAsync();
            var schema = await GetSchemaNameAsync(clientId);

            var tableNames = new List<string>();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT table_name FROM information_schema.tables
                WHERE table_schema = @s ORDER BY table_name;";
            command.Parameters.AddWithValue("s", schema);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
            return tableNames;
        }

        public async Task<List<DatasetTableInfo>> ListTablesAsync(int clientId)
        {
            await using var connection = await OpenConnectionAsync();
            var schema = await GetSchemaNameAsync(clientId);

            var tableNames = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT table_name FROM information_schema.tables
                    WHERE table_schema = @s ORDER BY table_name;";
                command.Parameters.AddWithValue("s", schema);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    tableNames.Add(reader.GetString(0));
            }

            if (tableNames.Count == 0) return new List<DatasetTableInfo>();

            var columnsByTable = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT table_name, column_name FROM information_schema.columns
                    WHERE table_schema = @s AND column_name <> 'RowId'
                    ORDER BY table_name, ordinal_position;";
                command.Parameters.AddWithValue("s", schema);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var table = reader.GetString(0);
                    if (!columnsByTable.TryGetValue(table, out var list))
                        columnsByTable[table] = list = new List<string>();
                    list.Add(reader.GetString(1));
                }
            }

            var countsByTable = new Dictionary<string, long>(StringComparer.Ordinal);
            await using (var command = connection.CreateCommand())
            {
                var unionParts = new List<string>();
                for (var i = 0; i < tableNames.Count; i++)
                {
                    var paramName = $"tbl{i}";
                    unionParts.Add($"SELECT @{paramName} AS tbl, COUNT(*) AS c FROM \"{schema}\".\"{tableNames[i]}\"");
                    command.Parameters.AddWithValue(paramName, tableNames[i]);
                }
                command.CommandText = string.Join(" UNION ALL ", unionParts);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    countsByTable[reader.GetString(0)] = reader.GetInt64(1);
            }

            var result = tableNames.Select(t => new DatasetTableInfo
            {
                TableName = t,
                Columns = columnsByTable.TryGetValue(t, out var cols) ? cols : new List<string>(),
                RowCount = countsByTable.TryGetValue(t, out var count) ? count : 0
            }).ToList();
            return result;
        }

        public async Task<DatasetRowsPage> ListRowsAsync(int clientId, string tableName, int page, int pageSize, Dictionary<string, string?>? filters = null)
        {
            await using var connection = await OpenConnectionAsync();
            var schema = SchemaFor(clientId);
            var table = await RequireExistingTableAsync(connection, schema, tableName);
            var columns = await GetColumnsAsync(connection, schema, table);

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 500);

            var filterClauses = new List<string>();
            var filterParams = new List<(string Name, object Value)>();
            if (filters is not null)
            {
                foreach (var (column, value) in filters)
                {
                    var trimmed = value?.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                        continue;

                    var matchedColumn = columns.FirstOrDefault(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));
                    if (matchedColumn == null)
                        continue;

                    var paramName = $"p{filterParams.Count}";
                    filterClauses.Add($"COALESCE(CAST(\"{matchedColumn}\" AS TEXT), '') ILIKE @{paramName}");
                    filterParams.Add((paramName, $"%{trimmed}%"));
                }
            }

            var whereClause = filterClauses.Count > 0 ? "WHERE " + string.Join(" AND ", filterClauses) : string.Empty;

            await using var countCmd = connection.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(1) FROM \"{schema}\".\"{table}\" {whereClause};";
            foreach (var param in filterParams)
                countCmd.Parameters.AddWithValue(param.Name, param.Value);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

            var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"RowId\", {columnList} FROM \"{schema}\".\"{table}\" {whereClause} ORDER BY \"RowId\" LIMIT @take OFFSET @skip;";
            command.Parameters.AddWithValue("take", pageSize);
            command.Parameters.AddWithValue("skip", (page - 1) * pageSize);
            foreach (var param in filterParams)
                command.Parameters.AddWithValue(param.Name, param.Value);

            var rows = new List<Dictionary<string, object?>>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object?>();
                    for (var i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
            }

            return new DatasetRowsPage
            {
                Columns = columns,
                Rows = rows,
                Page = page,
                PageSize = pageSize,
                TotalRows = total,
                Filters = filters ?? new Dictionary<string, string?>()
            };
        }


        public async Task<Dictionary<string, object?>?> GetRowAsync(int clientId, string tableName, long rowId)
        {
            await using var connection = await OpenConnectionAsync();
            var schema = SchemaFor(clientId);
            var table = await RequireExistingTableAsync(connection, schema, tableName);
            var columns = await GetColumnsAsync(connection, schema, table);
            var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"RowId\", {columnList} FROM \"{schema}\".\"{table}\" WHERE \"RowId\" = @id;";
            command.Parameters.AddWithValue("id", rowId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            return row;
        }

        // ── Editing ─────────────────────────────────────────────────────────────────────
        // Every edited value arrives from the form as a plain string. AddWithValue would send
        // it to Postgres typed as text regardless of the column's real type, which Postgres
        // rejects for anything non-text (date, numeric, boolean, ...) — so each value is
        // converted and bound with its column's actual Postgres type first.
        public async Task UpdateRowAsync(int clientId, string tableName, long rowId, Dictionary<string, string?> values)
        {
            await using var connection = await OpenConnectionAsync();
            var schema = SchemaFor(clientId);
            var table = await RequireExistingTableAsync(connection, schema, tableName);
            var columnTypes = await GetColumnTypesAsync(connection, schema, table);

            var setClauses = new List<string>();
            await using var command = connection.CreateCommand();
            var paramIndex = 0;

            foreach (var (column, value) in values)
            {
                if (!columnTypes.TryGetValue(column, out var pgType)) continue;
                var paramName = $"p{paramIndex++}";
                setClauses.Add($"\"{column}\" = @{paramName}");
                AddTypedParameter(command, paramName, value, pgType, column);
            }

            if (setClauses.Count == 0) return;

            command.CommandText = $"UPDATE \"{schema}\".\"{table}\" SET {string.Join(", ", setClauses)} WHERE \"RowId\" = @rowId;";
            command.Parameters.AddWithValue("rowId", rowId);
            await command.ExecuteNonQueryAsync();
        }

        private static void AddTypedParameter(NpgsqlCommand command, string paramName, string? rawValue, string postgresDataType, string columnName)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                command.Parameters.Add(new NpgsqlParameter(paramName, DBNull.Value));
                return;
            }

            try
            {
                switch (postgresDataType)
                {
                    case "smallint":
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Smallint) { Value = short.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture) });
                        break;
                    case "integer":
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Integer) { Value = int.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture) });
                        break;
                    case "bigint":
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Bigint) { Value = long.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture) });
                        break;
                    case "numeric":
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Numeric) { Value = decimal.Parse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture) });
                        break;
                    case "double precision":
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Double) { Value = double.Parse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture) });
                        break;
                    case "real":
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Real) { Value = float.Parse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture) });
                        break;
                    case "boolean":
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Boolean) { Value = ParseBoolean(rawValue) });
                        break;
                    case "date":
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Date) { Value = DateTime.Parse(rawValue, CultureInfo.InvariantCulture).Date });
                        break;
                    case "time without time zone":
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Time) { Value = ParseTimeOfDay(rawValue) });
                        break;
                    case "timestamp with time zone":
                    case "timestamp without time zone":
                        var dt = DateTime.Parse(rawValue, CultureInfo.InvariantCulture);
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(dt, DateTimeKind.Utc) });
                        break;
                    case "uuid":
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Uuid) { Value = Guid.Parse(rawValue) });
                        break;
                    case "bytea":
                        var bytes = rawValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? HexStringToBytes(rawValue.Substring(2))
                            : Convert.FromBase64String(rawValue);
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Bytea) { Value = bytes });
                        break;
                    default:
                        command.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Text) { Value = rawValue });
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new InvalidOperationException($"'{rawValue}' isn't a valid value for column '{columnName}' ({postgresDataType}).");
            }
        }

        private static async Task<Dictionary<string, string>> GetColumnTypesAsync(NpgsqlConnection connection, string schema, string table)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT column_name, data_type FROM information_schema.columns
                WHERE table_schema = @s AND table_name = @t AND column_name <> 'RowId'
                ORDER BY ordinal_position;";
            command.Parameters.AddWithValue("s", schema);
            command.Parameters.AddWithValue("t", table);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result[reader.GetString(0)] = reader.GetString(1);
            return result;
        }

        public async Task DeleteRowAsync(int clientId, string tableName, long rowId)
        {
            await using var connection = await OpenConnectionAsync();
            var schema = SchemaFor(clientId);
            var table = await RequireExistingTableAsync(connection, schema, tableName);

            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM \"{schema}\".\"{table}\" WHERE \"RowId\" = @id;";
            command.Parameters.AddWithValue("id", rowId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task DeleteTableAsync(int clientId, string tableName)
        {
            await using var connection = await OpenConnectionAsync();
            var schema = SchemaFor(clientId);
            var table = await RequireExistingTableAsync(connection, schema, tableName);

            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE \"{schema}\".\"{table}\";";
            await command.ExecuteNonQueryAsync();
        }
    }
}
