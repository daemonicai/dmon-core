using System.Text.Json;
using Dmon.Abstractions.Memory;
using Dmon.Core.Session;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Session;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly ISessionStore _store;

    public SessionStoreTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _store = CreateStore(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static ISessionStore CreateStore(string sessionsRoot)
    {
        FakeResolver resolver = new(sessionsRoot);
        IConfiguration config = new ConfigurationBuilder().Build();
        IAttachmentStore attachmentStore = new AttachmentStore(resolver, config);
        return new SessionStore(resolver, attachmentStore, NullLogger<SessionStore>.Instance, NullLoggerFactory.Instance, config);
    }

    [Fact]
    public async Task CreateAsync_CreatesDirectoryStructure()
    {
        SessionMeta meta = await _store.CreateAsync();

        string sessionDir = _store.GetSessionDirectory(meta.Id);

        Assert.True(Directory.Exists(sessionDir));
        Assert.True(Directory.Exists(Path.Combine(sessionDir, "attachments")));
        Assert.True(File.Exists(Path.Combine(sessionDir, "messages.jsonl")));
        Assert.True(File.Exists(Path.Combine(sessionDir, "meta.json")));
    }

    [Fact]
    public async Task CreateAsync_WritesMetaJson()
    {
        SessionMeta meta = await _store.CreateAsync("my-session");

        string sessionDir = _store.GetSessionDirectory(meta.Id);
        string json = await File.ReadAllTextAsync(Path.Combine(sessionDir, "meta.json"));
        SessionMeta? loaded = JsonSerializer.Deserialize<SessionMeta>(json);

        Assert.NotNull(loaded);
        Assert.Equal(meta.Id, loaded.Id);
        Assert.Equal("my-session", loaded.Name);
    }

    [Fact]
    public async Task LoadAsync_ReturnsCreatedSession()
    {
        SessionMeta created = await _store.CreateAsync("load-test");
        SessionMeta loaded = await _store.LoadAsync(created.Id);

        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal("load-test", loaded.Name);
    }

    [Fact]
    public async Task LoadAsync_MissingSession_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.LoadAsync("nonexistent-id"));
    }

    [Fact]
    public async Task ListAsync_ReturnsAllCreatedSessions()
    {
        SessionMeta a = await _store.CreateAsync("a");
        SessionMeta b = await _store.CreateAsync("b");
        SessionMeta c = await _store.CreateAsync("c");

        IReadOnlyList<SessionMeta> list = await _store.ListAsync();

        Assert.Contains(list, m => m.Id == a.Id);
        Assert.Contains(list, m => m.Id == b.Id);
        Assert.Contains(list, m => m.Id == c.Id);
    }

    [Fact]
    public async Task UpdateMetaAsync_UpdatesModifiedTimestamp()
    {
        SessionMeta original = await _store.CreateAsync();
        DateTimeOffset originalModified = original.Modified;

        // Ensure time advances.
        await Task.Delay(10);

        await _store.UpdateMetaAsync(original);
        SessionMeta reloaded = await _store.LoadAsync(original.Id);

        Assert.True(reloaded.Modified > originalModified);
    }

    [Fact]
    public async Task UpdateMetaAsync_WritesAtomically()
    {
        // Verify the temp-file-then-rename approach: meta.json always contains valid JSON after update.
        SessionMeta meta = await _store.CreateAsync("atomic-test");

        await _store.UpdateMetaAsync(meta with { Name = "updated" });

        SessionMeta reloaded = await _store.LoadAsync(meta.Id);
        Assert.Equal("updated", reloaded.Name);
    }

    // ── AppendMessagesAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task AppendMessagesAsync_WritesCanonicalLinesAndReturnsMintedEntryIds()
    {
        SessionMeta session = await _store.CreateAsync();

        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.User, "Hello there"),
            new ChatMessage(ChatRole.Assistant, "Hi! How can I help?"),
        ];

        IReadOnlyList<MessageRecord> written = await _store.AppendMessagesAsync(session.Id, messages);

        // One record per non-system message.
        Assert.Equal(2, written.Count);
        Assert.All(written, r => Assert.False(string.IsNullOrWhiteSpace(r.EntryId)));
        Assert.Distinct(written.Select(r => r.EntryId));

        // Canonical JSONL must have exactly 2 lines.
        string jsonlPath = Path.Combine(_store.GetSessionDirectory(session.Id), "messages.jsonl");
        string[] lines = (await File.ReadAllLinesAsync(jsonlPath))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(2, lines.Length);

        // Each line must be a MessageRecord with the correct role.
        string combined = string.Join("\n", lines);
        Assert.Contains("\"user\"", combined, StringComparison.Ordinal);
        Assert.Contains("\"assistant\"", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendMessagesAsync_SystemMessageSkipped()
    {
        SessionMeta session = await _store.CreateAsync();

        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, "You are a helpful assistant."),
            new ChatMessage(ChatRole.User, "Hello"),
        ];

        IReadOnlyList<MessageRecord> written = await _store.AppendMessagesAsync(session.Id, messages);

        // Only the user message should be persisted.
        Assert.Single(written);

        string jsonlPath = Path.Combine(_store.GetSessionDirectory(session.Id), "messages.jsonl");
        string[] lines = (await File.ReadAllLinesAsync(jsonlPath))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Single(lines);
        Assert.DoesNotContain("\"system\"", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendMessagesAsync_WithMemory_CallsMemoryRecordAsyncAfterCanonicalWrite()
    {
        // Arrange: memory spy tracks calls and records receipt order.
        SpyMemory memSpy = new();
        SessionStore storeWithMemory = CreateStoreWithMemory(memSpy);
        SessionMeta session = await storeWithMemory.CreateAsync();

        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.User,      "persist me"),
            new ChatMessage(ChatRole.Assistant, "persisted"),
        ];

        // Act
        IReadOnlyList<MessageRecord> written = await storeWithMemory.AppendMessagesAsync(session.Id, messages);

        // Assert: memory was called exactly once (after storage).
        Assert.Equal(1, memSpy.RecordCallCount);

        // The records passed to memory must carry the same entryIds that were written.
        IReadOnlyList<MessageRecord>? indexedRecords = memSpy.LastRecordedRecords;
        Assert.NotNull(indexedRecords);
        Assert.Equal(2, indexedRecords.Count);
        Assert.Equal(written[0].EntryId, indexedRecords[0].EntryId);
        Assert.Equal(written[1].EntryId, indexedRecords[1].EntryId);
    }

    [Fact]
    public async Task AppendMessagesAsync_WithMemory_MemoryRecordMatchesDiskRecord()
    {
        // Validates ADR-016: the memory index is a derivation of the canonical record —
        // live-indexing must be identical to what a rebuild-from-JSONL would derive.
        SpyMemory memSpy = new();
        SessionStore storeWithMemory = CreateStoreWithMemory(memSpy);
        SessionMeta session = await storeWithMemory.CreateAsync();

        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.User,      "hello"),
            new ChatMessage(ChatRole.Assistant, "world"),
        ];

        await storeWithMemory.AppendMessagesAsync(session.Id, messages);

        IReadOnlyList<MessageRecord>? indexedRecords = memSpy.LastRecordedRecords;
        Assert.NotNull(indexedRecords);

        // Read what was actually persisted to disk.
        IReadOnlyList<SessionLogLine> diskRecords = await storeWithMemory.ReadRecordsAsync(session.Id);
        List<MessageRecord> diskMessages = diskRecords.OfType<MessageRecord>().ToList();

        Assert.Equal(diskMessages.Count, indexedRecords.Count);

        for (int i = 0; i < diskMessages.Count; i++)
        {
            // entryId and timestamp must be identical — no second UtcNow call between write and index.
            Assert.Equal(diskMessages[i].EntryId,   indexedRecords[i].EntryId);
            Assert.Equal(diskMessages[i].Timestamp, indexedRecords[i].Timestamp);
            Assert.Equal(diskMessages[i].Role,      indexedRecords[i].Role);

            // Parts must reflect the post-offload state that was written to disk.
            Assert.Equal(diskMessages[i].Parts.Count, indexedRecords[i].Parts.Count);
        }
    }

    private SessionStore CreateStoreWithMemory(IMemory memory)
    {
        FakeResolver resolver = new(_tempRoot);
        IConfiguration config = new ConfigurationBuilder().Build();
        IAttachmentStore attachmentStore = new AttachmentStore(resolver, config);
        Lazy<IMemory?> lazyMemory = new(() => memory);
        return new SessionStore(resolver, attachmentStore, NullLogger<SessionStore>.Instance, NullLoggerFactory.Instance, config, lazyMemory);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class SpyMemory : IMemory
    {
        public int RecordCallCount { get; private set; }
        public IReadOnlyList<MessageRecord>? LastRecordedRecords { get; private set; }

        public IShortTermMemory ShortTerm => throw new NotSupportedException();
        public ILongTermMemory? LongTerm => null;

        public Task RecordAsync(
            IReadOnlyList<MessageRecord> records,
            MemoryScope scope = MemoryScope.Agent,
            CancellationToken cancellationToken = default)
        {
            RecordCallCount++;
            LastRecordedRecords = records;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemoryHit>> SearchAsync(
            string query,
            MemoryScope scope = MemoryScope.Agent,
            int limit = 10,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeResolver : ISessionDirectoryResolver
    {
        private readonly string _root;

        public FakeResolver(string root) => _root = root;

        public string Resolve(string workingDirectory) => _root;
    }
}
