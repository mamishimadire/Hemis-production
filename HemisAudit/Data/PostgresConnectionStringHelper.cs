using Npgsql;

namespace HemisAudit.Data
{
    // Supabase's connection pooler (Supavisor/PgBouncer) closes idle physical connections
    // server-side. Npgsql's own connection pool doesn't know a pooled connection died until it
    // tries to use it, which surfaces as an unhandled SocketException/IOException mid-request
    // instead of a clean retry. Recycling idle connections client-side faster than the pooler
    // does means a dead connection is essentially never handed back out.
    public static class PostgresConnectionStringHelper
    {
        public static string WithResiliencyDefaults(string connectionString, int commandTimeoutSeconds = 60)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                KeepAlive = 30,
                ConnectionIdleLifetime = 60,
                ConnectionPruningInterval = 10,
                Timeout = 15,
                CommandTimeout = commandTimeoutSeconds
            };
            return builder.ConnectionString;
        }
    }
}
