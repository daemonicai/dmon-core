using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dmon.Core.Telemetry;

namespace Dmon.Core.Session;

public interface ISessionStore
{
    Task<SessionMeta> CreateAsync(string? name = null, CancellationToken cancellationToken = default);
    Task<SessionMeta> LoadAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionMeta>> ListAsync(CancellationToken cancellationToken = default);
    Task UpdateMetaAsync(SessionMeta meta, CancellationToken cancellationToken = default);
    string GetSessionDirectory(string sessionId);
    Task<SessionMeta> ForkAsync(string sourceSessionId, string entryId, string? name = null, CancellationToken cancellationToken = default);
    Task<SessionMeta> CloneAsync(string sourceSessionId, string? name = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<object>> ReadMessagesAsync(string sessionId, bool applyCompaction = true, CancellationToken cancellationToken = default);
}

public sealed class SessionStore : ISessionStore
{
    private readonly ISessionDirectoryResolver _resolver;
    private readonly ILogger<SessionStore> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly int _compactionThreshold;

    public SessionStore(
        ISessionDirectoryResolver resolver,
        ILogger<SessionStore> logger,
        ILoggerFactory loggerFactory,
        IConfiguration configuration)
    {
        _resolver = resolver;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _compactionThreshold = configuration.GetValue("Dmon:Session:Compaction:Threshold", 100);
    }

    /// <summary>
    /// The message count at which auto-compaction should be triggered.
    /// Read from <c>Dmon:Session:Compaction:Threshold</c>, default 100.
    /// </summary>
    public int CompactionThreshold => _compactionThreshold;

    public async Task<SessionMeta> CreateAsync(string? name = null, CancellationToken cancellationToken = default)
    {
        using Activity? activity = DmonTelemetry.Source.StartActivity("session.create");

        string root = GetRoot();
        string id = Guid.NewGuid().ToString();
        string sessionDir = Path.Combine(root, id);

        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "attachments"));

        // Touch messages.jsonl
        File.Create(Path.Combine(sessionDir, "messages.jsonl")).Dispose();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionMeta meta = new()
        {
            Id = id,
            Name = name,
            Created = now,
            Modified = now
        };

        await WriteMetaAsync(sessionDir, meta, cancellationToken).ConfigureAwait(false);
        await GetIndex(root).UpsertAsync(meta, sessionDir, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Session {SessionId} created at {Path}.", id, sessionDir);

        return meta;
    }

    public async Task<SessionMeta> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        string root = GetRoot();
        string sessionDir = Path.Combine(root, sessionId);
        string metaPath = Path.Combine(sessionDir, "meta.json");

        if (!File.Exists(metaPath))
        {
            throw new InvalidOperationException($"Session '{sessionId}' not found.");
        }

        string json = await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<SessionMeta>(json)
            ?? throw new InvalidOperationException($"meta.json for session '{sessionId}' is malformed.");
    }

    public async Task<IReadOnlyList<SessionMeta>> ListAsync(CancellationToken cancellationToken = default)
    {
        string root = GetRoot();
        SessionIndex index = GetIndex(root);

        IReadOnlyList<SessionIndexEntry> entries = await index.ListAsync(cancellationToken).ConfigureAwait(false);

        // If the index is empty, check whether session directories exist and rebuild if so.
        if (entries.Count == 0 && Directory.Exists(root) && Directory.EnumerateDirectories(root).Any())
        {
            await index.RebuildAsync(root, cancellationToken).ConfigureAwait(false);
            entries = await index.ListAsync(cancellationToken).ConfigureAwait(false);
        }

        // Map index entries to SessionMeta. ForkEntryId, Tokens, and Cost are not stored in the index;
        // they use default values here. Full meta.json reads happen only in LoadAsync.
        return entries
            .Select(e => new SessionMeta
            {
                Id = e.Id,
                Name = e.Name,
                Created = e.Created,
                Modified = e.Modified,
                ParentSession = e.ParentSession
            })
            .ToList();
    }

    public async Task UpdateMetaAsync(SessionMeta meta, CancellationToken cancellationToken = default)
    {
        string root = GetRoot();
        string sessionDir = Path.Combine(root, meta.Id);

        SessionMeta updated = meta with { Modified = DateTimeOffset.UtcNow };

        await WriteMetaAsync(sessionDir, updated, cancellationToken).ConfigureAwait(false);
        await GetIndex(root).UpsertAsync(updated, sessionDir, cancellationToken).ConfigureAwait(false);
    }

    public string GetSessionDirectory(string sessionId)
    {
        return Path.Combine(GetRoot(), sessionId);
    }

    public async Task<SessionMeta> ForkAsync(
        string sourceSessionId,
        string entryId,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = DmonTelemetry.Source.StartActivity("session.fork");

        string root = GetRoot();
        string sourceDir = Path.Combine(root, sourceSessionId);

        if (!Directory.Exists(sourceDir))
        {
            throw new InvalidOperationException($"Source session '{sourceSessionId}' not found.");
        }

        string newId = Guid.NewGuid().ToString();
        string newDir = Path.Combine(root, newId);

        // 1. Full recursive copy of source → new directory.
        CopyDirectoryRecursive(sourceDir, newDir);

        // 2. Truncate messages.jsonl at the line containing entryId.
        string messagesPath = Path.Combine(newDir, "messages.jsonl");
        List<string> retainedLines = await TruncateAtEntryIdAsync(messagesPath, entryId, cancellationToken).ConfigureAwait(false);

        // 3. Remove unreferenced attachments.
        PruneUnreferencedAttachments(newDir, retainedLines);

        // 4. Rewrite meta.json.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionMeta newMeta = new()
        {
            Id = newId,
            Name = name,
            Created = now,
            Modified = now,
            ParentSession = sourceSessionId,
            ForkEntryId = entryId
        };

        await WriteMetaAsync(newDir, newMeta, cancellationToken).ConfigureAwait(false);

        // 5. Upsert into index.
        await GetIndex(root).UpsertAsync(newMeta, newDir, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Session {Source} forked to {New} at entry {EntryId}.", sourceSessionId, newId, entryId);

        return newMeta;
    }

    public async Task<SessionMeta> CloneAsync(
        string sourceSessionId,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = DmonTelemetry.Source.StartActivity("session.clone");

        string root = GetRoot();
        string sourceDir = Path.Combine(root, sourceSessionId);

        if (!Directory.Exists(sourceDir))
        {
            throw new InvalidOperationException($"Source session '{sourceSessionId}' not found.");
        }

        string newId = Guid.NewGuid().ToString();
        string newDir = Path.Combine(root, newId);

        // Full recursive copy.
        CopyDirectoryRecursive(sourceDir, newDir);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionMeta newMeta = new()
        {
            Id = newId,
            Name = name,
            Created = now,
            Modified = now,
            ParentSession = sourceSessionId,
            ForkEntryId = null
        };

        await WriteMetaAsync(newDir, newMeta, cancellationToken).ConfigureAwait(false);
        await GetIndex(root).UpsertAsync(newMeta, newDir, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Session {Source} cloned to {New}.", sourceSessionId, newId);

        return newMeta;
    }

    public async Task<IReadOnlyList<object>> ReadMessagesAsync(
        string sessionId,
        bool applyCompaction = true,
        CancellationToken cancellationToken = default)
    {
        string root = GetRoot();
        string messagesPath = Path.Combine(root, sessionId, "messages.jsonl");

        if (!File.Exists(messagesPath))
        {
            return [];
        }

        List<string> lines = [];

        await using FileStream stream = new(messagesPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Encoding.UTF8);

        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        if (!applyCompaction)
        {
            return lines.Select(l => (object)JsonSerializer.Deserialize<JsonElement>(l)).ToList();
        }

        // Find the last compaction marker by file position (line index). UUIDs are not lexicographically
        // monotonic so we must not use string comparison of entryIds for ordering.
        int lastCompactionIndex = -1;
        string? supersedesUpTo = null;

        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Contains("\"type\":\"compaction\"") || lines[i].Contains("\"type\": \"compaction\""))
            {
                try
                {
                    CompactionMessage? compaction = JsonSerializer.Deserialize<CompactionMessage>(lines[i]);

                    if (compaction is not null)
                    {
                        lastCompactionIndex = i;
                        supersedesUpTo = compaction.SupersedesUpTo;
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Malformed line — keep scanning for an earlier valid marker.
                }
            }
        }

        if (lastCompactionIndex < 0)
        {
            return lines.Select(l => (object)JsonSerializer.Deserialize<JsonElement>(l)).ToList();
        }

        // Find the file position of the line whose entryId matches supersedesUpTo. Every line strictly
        // before that position is superseded. Lines from that position onward (including the compaction
        // marker and everything after it) are retained.
        int supersedesUpToIndex = -1;

        if (supersedesUpTo is not null)
        {
            for (int i = 0; i < lastCompactionIndex; i++)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(lines[i]);

                    if (doc.RootElement.TryGetProperty("entryId", out JsonElement prop) &&
                        prop.GetString() == supersedesUpTo)
                    {
                        supersedesUpToIndex = i;
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Malformed line — skip.
                }
            }
        }

        List<object> result = [];

        for (int i = 0; i < lines.Count; i++)
        {
            // Lines strictly before the supersedesUpTo message are superseded.
            if (supersedesUpToIndex >= 0 && i <= supersedesUpToIndex && i < lastCompactionIndex)
            {
                continue;
            }

            result.Add(JsonSerializer.Deserialize<JsonElement>(lines[i]));
        }

        return result;
    }

    private string GetRoot()
    {
        string root = _resolver.Resolve(Environment.CurrentDirectory);
        Directory.CreateDirectory(root);
        return root;
    }

    private SessionIndex GetIndex(string root) => new(root, _loggerFactory.CreateLogger<SessionIndex>());

    private static async Task WriteMetaAsync(string sessionDir, SessionMeta meta, CancellationToken cancellationToken)
    {
        string metaPath = Path.Combine(sessionDir, "meta.json");
        string tempPath = metaPath + ".tmp";

        string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        // Atomic rename.
        File.Move(tempPath, metaPath, overwrite: true);
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string subDir in Directory.EnumerateDirectories(source))
        {
            string destSubDir = Path.Combine(destination, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSubDir);
        }
    }

    /// <summary>
    /// Reads lines from <paramref name="messagesPath"/>, retains lines up to and including
    /// the one containing <paramref name="entryId"/>, writes them back, and returns the retained lines.
    /// </summary>
    private static async Task<List<string>> TruncateAtEntryIdAsync(
        string messagesPath,
        string entryId,
        CancellationToken cancellationToken)
    {
        List<string> allLines = [];

        if (File.Exists(messagesPath))
        {
            allLines.AddRange(await File.ReadAllLinesAsync(messagesPath, cancellationToken).ConfigureAwait(false));
        }

        List<string> retained = [];
        bool found = false;

        foreach (string line in allLines)
        {
            retained.Add(line);

            bool matched = false;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);

                if (doc.RootElement.TryGetProperty("entryId", out JsonElement prop) &&
                    prop.GetString() == entryId)
                {
                    matched = true;
                }
            }
            catch (JsonException)
            {
                // Malformed line — include it and keep scanning (safe default).
            }

            if (matched)
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            throw new InvalidOperationException($"Entry '{entryId}' not found in session messages at '{messagesPath}'.");
        }

        string tempPath = messagesPath + ".tmp";
        await File.WriteAllLinesAsync(tempPath, retained, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, messagesPath, overwrite: true);

        return retained;
    }

    private static void PruneUnreferencedAttachments(string sessionDir, IReadOnlyList<string> retainedLines)
    {
        string attachmentsDir = Path.Combine(sessionDir, "attachments");

        if (!Directory.Exists(attachmentsDir))
        {
            return;
        }

        // Collect referenced attachment basenames by parsing the attachmentPath property.
        HashSet<string> referenced = [];

        foreach (string line in retainedLines)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);

                if (doc.RootElement.TryGetProperty("attachmentPath", out JsonElement prop))
                {
                    string? attachmentPath = prop.GetString();

                    if (attachmentPath is not null)
                    {
                        referenced.Add(Path.GetFileName(attachmentPath));
                    }
                }
            }
            catch (JsonException)
            {
                // Cannot parse this line — skip it; do not delete any attachment it might reference.
            }
        }

        foreach (string file in Directory.EnumerateFiles(attachmentsDir))
        {
            string fileName = Path.GetFileName(file);

            if (!referenced.Contains(fileName))
            {
                File.Delete(file);
            }
        }
    }
}
