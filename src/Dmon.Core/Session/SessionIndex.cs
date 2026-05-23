using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Dmon.Core.Session;

public interface ISessionIndex
{
    Task UpsertAsync(SessionMeta meta, string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionIndexEntry>> ListAsync(CancellationToken cancellationToken = default);
    Task RebuildAsync(string sessionsRoot, CancellationToken cancellationToken = default);
}

public sealed record SessionIndexEntry
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public required string Path { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }
    public string? ParentSession { get; init; }
}

public sealed class SessionIndex : ISessionIndex
{
    private readonly string _dbPath;
    private readonly string _sessionsRoot;
    private readonly ILogger<SessionIndex>? _logger;

    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS sessions (
            id TEXT PRIMARY KEY,
            name TEXT,
            path TEXT NOT NULL,
            created TEXT NOT NULL,
            modified TEXT NOT NULL,
            parentSession TEXT
        );
        """;

    public SessionIndex(string sessionsRoot, ILogger<SessionIndex>? logger = null)
    {
        _sessionsRoot = sessionsRoot;
        _dbPath = System.IO.Path.Combine(sessionsRoot, "sessions.db");
        _logger = logger;
    }

    public async Task UpsertAsync(SessionMeta meta, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await UpsertCoreAsync(meta, path, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            _logger?.LogWarning(ex, "Index corrupt during upsert for session {SessionId}. Rebuilding.", meta.Id);

            await RebuildAsync(_sessionsRoot, cancellationToken).ConfigureAwait(false);

            try
            {
                await UpsertCoreAsync(meta, path, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException retryEx)
            {
                // The index is a cache; loss of a single upsert is acceptable.
                _logger?.LogError(retryEx, "Index upsert failed after rebuild for session {SessionId}. Swallowing.", meta.Id);
            }
        }
    }

    private async Task UpsertCoreAsync(SessionMeta meta, string path, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection = OpenConnection();

        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (id, name, path, created, modified, parentSession)
            VALUES ($id, $name, $path, $created, $modified, $parentSession)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                path = excluded.path,
                modified = excluded.modified,
                parentSession = excluded.parentSession;
            """;

        cmd.Parameters.AddWithValue("$id", meta.Id);
        cmd.Parameters.AddWithValue("$name", meta.Name is null ? (object)DBNull.Value : meta.Name);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$created", meta.Created.ToString("O"));
        cmd.Parameters.AddWithValue("$modified", meta.Modified.ToString("O"));
        cmd.Parameters.AddWithValue("$parentSession", meta.ParentSession is null ? (object)DBNull.Value : meta.ParentSession);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionIndexEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await ListCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            _logger?.LogWarning(ex, "Index corrupt during list. Rebuilding.");

            await RebuildAsync(_sessionsRoot, cancellationToken).ConfigureAwait(false);

            try
            {
                return await ListCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException retryEx)
            {
                _logger?.LogError(retryEx, "Index list failed after rebuild. Returning empty list.");
                return [];
            }
        }
    }

    private async Task<IReadOnlyList<SessionIndexEntry>> ListCoreAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection = OpenConnection();

        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, path, created, modified, parentSession FROM sessions ORDER BY modified DESC;";

        List<SessionIndexEntry> results = [];

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SessionIndexEntry
            {
                Id = reader.GetString(0),
                Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                Path = reader.GetString(2),
                Created = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Modified = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ParentSession = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return results;
    }

    public async Task RebuildAsync(string sessionsRoot, CancellationToken cancellationToken = default)
    {
        if (File.Exists(_dbPath))
        {
            // Clear the connection pool before deleting — on Windows an open pool handle causes IOException.
            SqliteConnection.ClearAllPools();
            File.Delete(_dbPath);
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (string metaFile in Directory.EnumerateFiles(sessionsRoot, "meta.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string sessionDir = System.IO.Path.GetDirectoryName(metaFile)!;

            try
            {
                string json = await File.ReadAllTextAsync(metaFile, cancellationToken).ConfigureAwait(false);
                SessionMeta? meta = JsonSerializer.Deserialize<SessionMeta>(json);

                if (meta is null)
                {
                    continue;
                }

                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO sessions (id, name, path, created, modified, parentSession)
                    VALUES ($id, $name, $path, $created, $modified, $parentSession)
                    ON CONFLICT(id) DO UPDATE SET
                        name = excluded.name,
                        path = excluded.path,
                        modified = excluded.modified,
                        parentSession = excluded.parentSession;
                    """;

                cmd.Parameters.AddWithValue("$id", meta.Id);
                cmd.Parameters.AddWithValue("$name", meta.Name is null ? (object)DBNull.Value : meta.Name);
                cmd.Parameters.AddWithValue("$path", sessionDir);
                cmd.Parameters.AddWithValue("$created", meta.Created.ToString("O"));
                cmd.Parameters.AddWithValue("$modified", meta.Modified.ToString("O"));
                cmd.Parameters.AddWithValue("$parentSession", meta.ParentSession is null ? (object)DBNull.Value : meta.ParentSession);

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Skipping malformed meta.json at {Path}", metaFile);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_dbPath)!);

        await using SqliteConnection connection = OpenConnection();

        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = CreateTableSql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }
}
