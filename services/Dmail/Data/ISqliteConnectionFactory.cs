using Microsoft.Data.Sqlite;

namespace Daemonic.Dmail.Data;

/// <summary>
/// Opens short-lived, pooled <see cref="SqliteConnection"/> instances per operation.
/// Microsoft.Data.Sqlite pools by connection string (Pooling=True), so opening and
/// disposing per operation is cheap while keeping the single-file, single-writer design.
/// </summary>
public interface ISqliteConnectionFactory
{
    /// <summary>The connection string shared with the SK SqliteVec store.</summary>
    string ConnectionString { get; }

    /// <summary>
    /// Opens a pooled connection with WAL and foreign keys applied.
    /// The caller owns the connection and must dispose it (use <c>await using</c>).
    /// The vec0 extension is loaded by the SK SqliteVec store on its own connections;
    /// raw connections from this factory only touch FTS5 and base tables, so they do
    /// not load vec0.
    /// </summary>
    Task<SqliteConnection> OpenAsync(CancellationToken ct = default);
}
