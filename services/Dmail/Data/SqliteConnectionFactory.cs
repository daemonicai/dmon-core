using Microsoft.Data.Sqlite;

namespace Daemonic.Dmail.Data;

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public string ConnectionString => _connectionString;

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        // WAL is persistent per-database; foreign-key enforcement is per-connection
        // and must be set on every pooled connection.
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync(ct);

        return connection;
    }
}
