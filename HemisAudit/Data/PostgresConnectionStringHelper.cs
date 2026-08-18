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
                CommandTimeout = commandTimeoutSeconds,
                // Without a floor, the pool prunes down to zero connections after any ~60s gap in
                // traffic (very likely for a freshly-deployed, low-traffic app). The next request -
                // e.g. a login - then pays the full cost of a fresh TCP+TLS+Postgres handshake to
                // Supabase from scratch, which is slow and occasionally times out outright, only to
                // succeed on retry once a connection exists. Keeping one warm connection alive at all
                // times avoids that cold-start tax entirely.
                MinPoolSize = 1,
                // Every caller of this helper builds the exact same connection string, so they all
                // share ONE Npgsql client-side pool. Without a ceiling it defaults to 100 - far above
                // Supabase's session-mode pooler limit (pool_size: 15 on this project). Under any real
                // concurrent load the app could burst past that and get hard-rejected with
                // "XX000 EMAXCONNSESSION: max clients reached". Capping below 15 makes Npgsql queue
                // extra demand client-side (up to Timeout=15s) instead of Supabase refusing it outright.
                MaxPoolSize = 10
            };
            return builder.ConnectionString;
        }
    }
}
