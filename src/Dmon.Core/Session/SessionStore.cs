using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dmon.Abstractions.Memory;
using Dmon.Core.Telemetry;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Serialises <paramref name="parts"/> (with attachment offloading for large tool results),
    /// mints an <c>entryId</c> and <c>timestamp</c>, appends a <c>type:message</c> record to
    /// the session log, and returns the minted <c>entryId</c>.
    /// </summary>
    Task<string> AppendMessageAsync(string sessionId, string role, IReadOnlyList<Part> parts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps each <paramref name="messages"/> via <see cref="ConversationMapper"/>, appends each
    /// as a canonical <c>type:message</c> record (minting <c>entryId</c>), and — after all writes
    /// succeed — drives the memory index (if available) for index-only ingestion.
    /// Returns the minted <c>entryId</c>s in input order.
    /// System-role messages are skipped (the system prompt is rebuilt on load, not persisted
    /// as a conversational turn).
    /// </summary>
    Task<IReadOnlyList<string>> AppendMessagesAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all log records (messages and compaction markers) deserialised via the
    /// <see cref="SessionLogLine"/> polymorphic base. When <paramref name="applyCompaction"/>
    /// is <c>true</c> (default), records superseded by the last compaction marker are excluded.
    /// </summary>
    Task<IReadOnlyList<SessionLogLine>> ReadRecordsAsync(string sessionId, bool applyCompaction = true, CancellationToken cancellationToken = default);
}

public sealed class SessionStore : ISessionStore
{
    private readonly ISessionDirectoryResolver _resolver;
    private readonly IAttachmentStore _attachmentStore;
    private readonly ILogger<SessionStore> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly int _compactionThreshold;
    // Lazy and nullable to break the DI cycle: IMemory → IShortTermMemory → ISessionStore → IMemory.
    // Canonical append always runs; indexing runs only when memory resolves non-null.
    private readonly Lazy<IMemory?> _memory;

    public SessionStore(
        ISessionDirectoryResolver resolver,
        IAttachmentStore attachmentStore,
        ILogger<SessionStore> logger,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        Lazy<IMemory?>? memory = null)
    {
        _resolver = resolver;
        _attachmentStore = attachmentStore;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _compactionThreshold = configuration.GetValue("Dmon:Session:Compaction:Threshold", 100);
        _memory = memory ?? new Lazy<IMemory?>(() => null);
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

    public async Task<string> AppendMessageAsync(
        string sessionId,
        string role,
        IReadOnlyList<Part> parts,
        CancellationToken cancellationToken = default)
    {
        MessageRecord record = await BuildAndWriteRecordAsync(sessionId, role, parts, cancellationToken)
            .ConfigureAwait(false);
        return record.EntryId;
    }

    public async Task<IReadOnlyList<string>> AppendMessagesAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        List<MessageRecord> written = new(messages.Count);

        foreach (ChatMessage message in messages)
        {
            // System prompt is rebuilt on load — not persisted as a conversational turn.
            if (message.Role == ChatRole.System)
                continue;

            (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(message);
            MessageRecord record = await BuildAndWriteRecordAsync(sessionId, role, parts, cancellationToken)
                .ConfigureAwait(false);
            written.Add(record);
        }

        if (written.Count == 0)
            return [];

        // Storage-first: all canonical writes are durable before index ingestion.
        IMemory? memory = _memory.Value;
        if (memory is not null)
        {
            try
            {
                await memory.RecordAsync(written, scope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Memory indexing failed for session {SessionId} — canonical record is intact.", sessionId);
            }
        }

        return written.Select(r => r.EntryId).ToList();
    }

    /// <summary>
    /// Mints an <c>entryId</c> and <c>timestamp</c>, offloads large tool results to attachments,
    /// serialises the resulting <see cref="MessageRecord"/>, appends it to the session log, and
    /// returns the record. The returned record is reference-identical in content to the line
    /// written to <c>messages.jsonl</c> — callers that need to forward the record to the memory
    /// tier should use this return value rather than reconstructing it.
    /// </summary>
    private async Task<MessageRecord> BuildAndWriteRecordAsync(
        string sessionId,
        string role,
        IReadOnlyList<Part> parts,
        CancellationToken cancellationToken)
    {
        string entryId = Guid.NewGuid().ToString();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        List<Part> offloaded = new(parts.Count);

        foreach (Part part in parts)
        {
            if (part is ToolResultPart toolResult && toolResult.Result.HasValue && toolResult.AttachmentRef is null)
            {
                string fullText = toolResult.Result.Value.ValueKind == JsonValueKind.String
                    ? toolResult.Result.Value.GetString() ?? toolResult.Result.Value.GetRawText()
                    : toolResult.Result.Value.GetRawText();

                string? attachmentRef = await _attachmentStore
                    .StoreIfLargeAsync(sessionId, toolResult.CallId, fullText, "txt", cancellationToken)
                    .ConfigureAwait(false);

                if (attachmentRef is not null)
                {
                    // Preview: first 200 chars of the full text, truncated.
                    string previewText = fullText.Length > 200
                        ? fullText[..200] + "…"
                        : fullText;

                    JsonElement previewElement = JsonSerializer.SerializeToElement(previewText);

                    offloaded.Add(toolResult with
                    {
                        Result = previewElement,
                        AttachmentRef = attachmentRef,
                        Truncated = true
                    });
                    continue;
                }
            }

            offloaded.Add(part);
        }

        MessageRecord record = new()
        {
            EntryId = entryId,
            Timestamp = timestamp,
            Role = role,
            Parts = offloaded
        };

        string json = JsonSerializer.Serialize<SessionLogLine>(record);
        await AppendLineAsync(sessionId, json, cancellationToken).ConfigureAwait(false);

        return record;
    }

    public async Task<IReadOnlyList<SessionLogLine>> ReadRecordsAsync(
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

        List<SessionLogLine> parsed = [];

        foreach (string l in lines)
        {
            try
            {
                SessionLogLine? record = JsonSerializer.Deserialize<SessionLogLine>(l);
                if (record is not null)
                {
                    parsed.Add(record);
                }
            }
            catch (JsonException)
            {
                // Malformed line — skip it.
            }
        }

        if (!applyCompaction)
        {
            return parsed;
        }

        // Find the last compaction marker by file position (line index). UUIDs are not
        // lexicographically monotonic so ordering must be by position, not entryId string.
        int lastCompactionIndex = -1;
        string? supersedesUpTo = null;

        for (int i = parsed.Count - 1; i >= 0; i--)
        {
            if (parsed[i] is CompactionMessage cm)
            {
                lastCompactionIndex = i;
                supersedesUpTo = cm.SupersedesUpTo;
                break;
            }
        }

        if (lastCompactionIndex < 0)
        {
            return parsed;
        }

        // Find the position of the record whose entryId == supersedesUpTo.
        int supersedesUpToIndex = -1;

        if (supersedesUpTo is not null)
        {
            for (int i = 0; i < lastCompactionIndex; i++)
            {
                string? id = parsed[i] switch
                {
                    MessageRecord mr => mr.EntryId,
                    CompactionMessage cm2 => cm2.EntryId,
                    _ => null
                };

                if (id == supersedesUpTo)
                {
                    supersedesUpToIndex = i;
                    break;
                }
            }
        }

        List<SessionLogLine> result = [];

        for (int i = 0; i < parsed.Count; i++)
        {
            if (supersedesUpToIndex >= 0 && i <= supersedesUpToIndex && i < lastCompactionIndex)
            {
                continue;
            }

            result.Add(parsed[i]);
        }

        return result;
    }

    private async Task AppendLineAsync(string sessionId, string json, CancellationToken cancellationToken)
    {
        string root = GetRoot();
        string messagesPath = Path.Combine(root, sessionId, "messages.jsonl");

        await using FileStream stream = new(
            messagesPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        byte[] lineBytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(lineBytes, cancellationToken).ConfigureAwait(false);
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
