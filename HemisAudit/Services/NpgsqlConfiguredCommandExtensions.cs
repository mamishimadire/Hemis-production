using Npgsql;

namespace HemisAudit.Services
{
    // Postgres counterpart to SqlLargeDataExtensions.cs. SystemDatabaseService now talks to the
    // shared Supabase Postgres database via Npgsql; the Rule*Service.cs files stay on
    // Microsoft.Data.SqlClient against institutions' own SQL Servers and keep using the
    // SqlConnection overload in SqlLargeDataExtensions.cs.
    internal static class NpgsqlConfiguredCommandExtensions
    {
        public const int LargeDataCommandTimeoutSeconds = 0;
        public const int LargeDataConnectionTimeoutSeconds = 180;

        public static NpgsqlCommand CreateConfiguredCommand(this NpgsqlConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandTimeout = LargeDataCommandTimeoutSeconds;
            return command;
        }

        public static NpgsqlCommand WithLargeDataTimeout(this NpgsqlCommand command)
        {
            command.CommandTimeout = LargeDataCommandTimeoutSeconds;
            return command;
        }
    }
}
