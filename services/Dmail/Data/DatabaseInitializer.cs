namespace Dmail.Data;

public sealed class DatabaseInitializer
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public DatabaseInitializer(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // OpenAsync applies WAL (persistent) + foreign_keys.
        await using var connection = await _connectionFactory.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS accounts (
                email TEXT PRIMARY KEY,
                provider_type TEXT NOT NULL DEFAULT 'gmail',
                access_token_encrypted TEXT,
                refresh_token_encrypted TEXT,
                token_expiry TEXT,
                labels TEXT,
                last_sync TEXT,
                account_state TEXT NOT NULL DEFAULT 'pending_auth',
                indexed_email_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS data_emails (
                uid INTEGER NOT NULL,
                account TEXT NOT NULL,
                subject TEXT NOT NULL DEFAULT '',
                body TEXT NOT NULL DEFAULT '',
                from_addr TEXT NOT NULL DEFAULT '',
                date TEXT NOT NULL,
                labels TEXT,
                pending_embedding INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (account, uid)
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS emails_fts USING fts5(
                subject,
                body,
                content='data_emails',
                content_rowid='rowid'
            );

            -- External-content FTS5 is NOT auto-maintained by SQLite: keep emails_fts
            -- in sync with data_emails via triggers keyed on the content rowid.
            CREATE TRIGGER IF NOT EXISTS data_emails_ai AFTER INSERT ON data_emails BEGIN
                INSERT INTO emails_fts(rowid, subject, body)
                VALUES (new.rowid, new.subject, new.body);
            END;

            CREATE TRIGGER IF NOT EXISTS data_emails_ad AFTER DELETE ON data_emails BEGIN
                INSERT INTO emails_fts(emails_fts, rowid, subject, body)
                VALUES ('delete', old.rowid, old.subject, old.body);
            END;

            CREATE TRIGGER IF NOT EXISTS data_emails_au AFTER UPDATE ON data_emails BEGIN
                INSERT INTO emails_fts(emails_fts, rowid, subject, body)
                VALUES ('delete', old.rowid, old.subject, old.body);
                INSERT INTO emails_fts(rowid, subject, body)
                VALUES (new.rowid, new.subject, new.body);
            END;

            CREATE TABLE IF NOT EXISTS backfill_state (
                account TEXT PRIMARY KEY,
                last_processed_uid INTEGER,
                status TEXT NOT NULL DEFAULT 'pending',
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (account) REFERENCES accounts(email) ON DELETE CASCADE
            );
        ";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
