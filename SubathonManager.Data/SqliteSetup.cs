using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SubathonManager.Data;

[ExcludeFromCodeCoverage]
public static class SqliteSetup {
    private const int BusyTimeoutMs = 30_000;

    public static string ConnectionString(string dbPath) {
        return new SqliteConnectionStringBuilder {
            DataSource = dbPath,
            DefaultTimeout = BusyTimeoutMs / 1000
        }.ToString();
    }

    public static DbContextOptionsBuilder UseAppSqlite(this DbContextOptionsBuilder options, string dbPath) {
        return options
            .UseSqlite(ConnectionString(dbPath))
            .AddInterceptors(PragmaInterceptor.Instance);
    }

    [ExcludeFromCodeCoverage]
    private sealed class PragmaInterceptor : DbConnectionInterceptor {
        public static readonly PragmaInterceptor Instance = new();

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) {
            Apply(connection);
        }

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default) {
            Apply(connection);
            return Task.CompletedTask;
        }

        private static void Apply(DbConnection connection) {
            try {
                using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs};" +
                                  "PRAGMA journal_mode=WAL;" +
                                  "PRAGMA synchronous=NORMAL;";
                cmd.ExecuteNonQuery();
            }
            catch (Exception) {
                /**/
            }
        }
    }
}
