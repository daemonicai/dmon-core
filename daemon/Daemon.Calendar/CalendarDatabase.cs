using Microsoft.Data.Sqlite;

namespace Daemon.Calendar;

internal sealed class CalendarDatabase
{
    private readonly string _dbPath;

    public CalendarDatabase(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                uid         TEXT PRIMARY KEY,
                title       TEXT NOT NULL,
                description TEXT,
                location    TEXT,
                start_utc   TEXT NOT NULL,
                end_utc     TEXT NOT NULL,
                last_sync   TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS events_fts USING fts5(
                uid UNINDEXED, title, description,
                content=events, content_rowid=rowid
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Clear()
    {
        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM events_fts;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM events;";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Upsert(IEnumerable<CalendarRow> rows)
    {
        string lastSync = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        using SqliteCommand eventsCmd = conn.CreateCommand();
        using SqliteCommand ftsCmd = conn.CreateCommand();

        eventsCmd.Transaction = tx;
        eventsCmd.CommandText = """
            INSERT OR REPLACE INTO events (uid, title, description, location, start_utc, end_utc, last_sync)
            VALUES (:uid, :title, :description, :location, :start_utc, :end_utc, :last_sync);
            """;
        SqliteParameter evUid = eventsCmd.Parameters.Add(":uid", SqliteType.Text);
        SqliteParameter evTitle = eventsCmd.Parameters.Add(":title", SqliteType.Text);
        SqliteParameter evDesc = eventsCmd.Parameters.Add(":description", SqliteType.Text);
        SqliteParameter evLoc = eventsCmd.Parameters.Add(":location", SqliteType.Text);
        SqliteParameter evStart = eventsCmd.Parameters.Add(":start_utc", SqliteType.Text);
        SqliteParameter evEnd = eventsCmd.Parameters.Add(":end_utc", SqliteType.Text);
        SqliteParameter evSync = eventsCmd.Parameters.Add(":last_sync", SqliteType.Text);
        evSync.Value = lastSync;

        ftsCmd.Transaction = tx;
        // SELECT rowid back from events after each INSERT OR REPLACE so the FTS row gets
        // the live events rowid. Using last_insert_rowid() is wrong under OR REPLACE
        // (it returns the new rowid, which differs from the original under row replacement).
        ftsCmd.CommandText = """
            INSERT INTO events_fts (rowid, uid, title, description)
            SELECT rowid, uid, title, description FROM events WHERE uid = :uid;
            """;
        SqliteParameter ftsUid = ftsCmd.Parameters.Add(":uid", SqliteType.Text);

        foreach (CalendarRow row in rows)
        {
            evUid.Value = row.Uid;
            evTitle.Value = row.Title;
            evDesc.Value = (object?)row.Description ?? DBNull.Value;
            evLoc.Value = (object?)row.Location ?? DBNull.Value;
            evStart.Value = row.StartUtc;
            evEnd.Value = row.EndUtc;
            eventsCmd.ExecuteNonQuery();

            ftsUid.Value = row.Uid;
            ftsCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public CalendarRow? FindNext(string term, string after)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.uid, e.title, e.description, e.location, e.start_utc, e.end_utc
            FROM events e
            JOIN events_fts f ON e.rowid = f.rowid
            WHERE events_fts MATCH :term
              AND e.start_utc >= :after
            ORDER BY e.start_utc ASC
            LIMIT 1;
            """;
        cmd.Parameters.Add(":term", SqliteType.Text).Value = term;
        cmd.Parameters.Add(":after", SqliteType.Text).Value = after;

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadRow(reader);
    }

    public IReadOnlyList<CalendarRow> ListUpcoming(int maxResults, string after)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.uid, e.title, e.description, e.location, e.start_utc, e.end_utc
            FROM events e
            WHERE e.start_utc >= :after
            ORDER BY e.start_utc ASC
            LIMIT :maxResults;
            """;
        cmd.Parameters.Add(":after", SqliteType.Text).Value = after;
        cmd.Parameters.Add(":maxResults", SqliteType.Integer).Value = maxResults;

        using SqliteDataReader reader = cmd.ExecuteReader();
        List<CalendarRow> results = [];
        while (reader.Read())
            results.Add(ReadRow(reader));

        return results;
    }

    public int Count()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private SqliteConnection Open()
    {
        SqliteConnection conn = new($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static CalendarRow ReadRow(SqliteDataReader reader) =>
        new(
            Uid: reader.GetString(0),
            Title: reader.GetString(1),
            Description: reader.IsDBNull(2) ? null : reader.GetString(2),
            Location: reader.IsDBNull(3) ? null : reader.GetString(3),
            StartUtc: reader.GetString(4),
            EndUtc: reader.GetString(5));
}
