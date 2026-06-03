using System.Text.Json;
using Dmon.Core.Session;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Session;

public sealed class MessagePersistenceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public MessagePersistenceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private ISessionStore CreateStores(int? thresholdBytes = null)
    {
        FakeResolver resolver = new(_tempRoot);
        IConfigurationBuilder builder = new ConfigurationBuilder();

        if (thresholdBytes.HasValue)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dmon:Session:AttachmentThresholdBytes"] = thresholdBytes.Value.ToString()
            });
        }

        IConfiguration config = builder.Build();
        IAttachmentStore attachmentStore = new AttachmentStore(resolver, config);
        return new SessionStore(resolver, attachmentStore, NullLogger<SessionStore>.Instance, NullLoggerFactory.Instance, config);
    }

    // ── 3.1: append mints entryId, writes type:message record ──────────────────

    [Fact]
    public async Task AppendMessageAsync_ReturnsNonEmptyEntryId()
    {
        ISessionStore store = CreateStores();
        SessionMeta session = await store.CreateAsync();

        string entryId = await store.AppendMessageAsync(session.Id, "user", [new TextPart { Text = "hello" }]);

        Assert.False(string.IsNullOrWhiteSpace(entryId));
    }

    [Fact]
    public async Task AppendMessageAsync_WritesTypeMessageDiscriminator()
    {
        ISessionStore store = CreateStores();
        SessionMeta session = await store.CreateAsync();

        await store.AppendMessageAsync(session.Id, "user", [new TextPart { Text = "hi" }]);

        string dir = store.GetSessionDirectory(session.Id);
        string[] lines = await File.ReadAllLinesAsync(Path.Combine(dir, "messages.jsonl"));
        string messageLine = lines.Single(l => !string.IsNullOrWhiteSpace(l));

        JsonElement raw = JsonSerializer.Deserialize<JsonElement>(messageLine);
        Assert.Equal("message", raw.GetProperty("type").GetString());
    }

    [Fact]
    public async Task AppendMessageAsync_RecordRoundTripsViaPolymorphicBase()
    {
        ISessionStore store = CreateStores();
        SessionMeta session = await store.CreateAsync();

        TextPart textPart = new() { Text = "round-trip me" };
        string entryId = await store.AppendMessageAsync(session.Id, "assistant", [textPart]);

        string dir = store.GetSessionDirectory(session.Id);
        string line = (await File.ReadAllLinesAsync(Path.Combine(dir, "messages.jsonl")))
            .Single(l => !string.IsNullOrWhiteSpace(l));

        SessionLogLine? parsed = JsonSerializer.Deserialize<SessionLogLine>(line);
        MessageRecord record = Assert.IsType<MessageRecord>(parsed);

        Assert.Equal(entryId, record.EntryId);
        Assert.Equal("assistant", record.Role);
        TextPart roundTripped = Assert.IsType<TextPart>(Assert.Single(record.Parts));
        Assert.Equal("round-trip me", roundTripped.Text);
    }

    // ── 3.2: offloading threshold ───────────────────────────────────────────────

    [Fact]
    public async Task AppendMessageAsync_SmallToolResult_StaysInline()
    {
        ISessionStore store = CreateStores(thresholdBytes: 1024);
        SessionMeta session = await store.CreateAsync();

        string smallResult = "short";
        JsonElement resultElement = JsonSerializer.SerializeToElement(smallResult);
        ToolResultPart part = new() { CallId = "call-1", Result = resultElement, IsError = false };

        await store.AppendMessageAsync(session.Id, "tool", [part]);

        IReadOnlyList<SessionLogLine> records = await store.ReadRecordsAsync(session.Id);
        MessageRecord record = Assert.IsType<MessageRecord>(Assert.Single(records));
        ToolResultPart stored = Assert.IsType<ToolResultPart>(Assert.Single(record.Parts));

        Assert.Null(stored.AttachmentRef);
        Assert.False(stored.Truncated);
        Assert.True(stored.Result.HasValue);
    }

    [Fact]
    public async Task AppendMessageAsync_LargeToolResult_IsOffloadedToAttachment()
    {
        ISessionStore store = CreateStores(thresholdBytes: 10);
        SessionMeta session = await store.CreateAsync();

        string largeResult = new string('x', 500);
        JsonElement resultElement = JsonSerializer.SerializeToElement(largeResult);
        ToolResultPart part = new() { CallId = "call-big", Result = resultElement, IsError = false };

        await store.AppendMessageAsync(session.Id, "tool", [part]);

        // Attachment file must exist and hold the full content.
        string attachmentPath = Path.Combine(store.GetSessionDirectory(session.Id), "attachments", "call-big.txt");
        Assert.True(File.Exists(attachmentPath));
        Assert.Equal(largeResult, await File.ReadAllTextAsync(attachmentPath));

        // Stored part must reference the attachment, be truncated, and carry a preview.
        IReadOnlyList<SessionLogLine> records = await store.ReadRecordsAsync(session.Id);
        MessageRecord record = Assert.IsType<MessageRecord>(Assert.Single(records));
        ToolResultPart stored = Assert.IsType<ToolResultPart>(Assert.Single(record.Parts));

        Assert.Equal("attachments/call-big.txt", stored.AttachmentRef);
        Assert.True(stored.Truncated);
        Assert.True(stored.Result.HasValue);
        // Preview must be shorter than the full content.
        string? preview = stored.Result!.Value.GetString();
        Assert.NotNull(preview);
        Assert.True(preview!.Length < largeResult.Length);
    }

    [Fact]
    public async Task AppendMessageAsync_LargeToolResult_PreservesIsError()
    {
        ISessionStore store = CreateStores(thresholdBytes: 10);
        SessionMeta session = await store.CreateAsync();

        string content = new string('e', 200);
        JsonElement resultElement = JsonSerializer.SerializeToElement(content);
        ToolResultPart part = new() { CallId = "call-err", Result = resultElement, IsError = true };

        await store.AppendMessageAsync(session.Id, "tool", [part]);

        IReadOnlyList<SessionLogLine> records = await store.ReadRecordsAsync(session.Id);
        MessageRecord record = Assert.IsType<MessageRecord>(Assert.Single(records));
        ToolResultPart stored = Assert.IsType<ToolResultPart>(Assert.Single(record.Parts));

        Assert.True(stored.IsError);
        Assert.NotNull(stored.AttachmentRef);
    }

    [Fact]
    public async Task AppendMessageAsync_NonToolResultParts_LeftUntouched()
    {
        ISessionStore store = CreateStores(thresholdBytes: 1);
        SessionMeta session = await store.CreateAsync();

        TextPart text = new() { Text = "unchanged text part" };

        await store.AppendMessageAsync(session.Id, "user", [text]);

        IReadOnlyList<SessionLogLine> records = await store.ReadRecordsAsync(session.Id);
        MessageRecord record = Assert.IsType<MessageRecord>(Assert.Single(records));
        TextPart stored = Assert.IsType<TextPart>(Assert.Single(record.Parts));
        Assert.Equal("unchanged text part", stored.Text);
    }

    // ── 3.3: typed read-back + compaction ──────────────────────────────────────

    [Fact]
    public async Task ReadRecordsAsync_ReturnsMostRecentPostCompactionRecords()
    {
        ISessionStore store = CreateStores();
        SessionMeta session = await store.CreateAsync();
        string dir = store.GetSessionDirectory(session.Id);

        await AppendLineAsync(dir, "{\"type\":\"message\",\"entryId\":\"e1\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"role\":\"user\",\"parts\":[]}");
        await AppendLineAsync(dir, "{\"type\":\"message\",\"entryId\":\"e2\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"role\":\"assistant\",\"parts\":[]}");
        await AppendLineAsync(dir, "{\"type\":\"compaction\",\"entryId\":\"c1\",\"supersedesUpTo\":\"e1\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"summary\":\"s\",\"reason\":\"manual\",\"tokensBefore\":0}");
        await AppendLineAsync(dir, "{\"type\":\"message\",\"entryId\":\"e3\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"role\":\"user\",\"parts\":[]}");

        IReadOnlyList<SessionLogLine> records = await store.ReadRecordsAsync(session.Id, applyCompaction: true);

        // e1 superseded; e2, c1, e3 retained.
        Assert.DoesNotContain(records, r => r is MessageRecord mr && mr.EntryId == "e1");
        Assert.Contains(records, r => r is MessageRecord mr && mr.EntryId == "e2");
        Assert.Contains(records, r => r is CompactionMessage cm && cm.EntryId == "c1");
        Assert.Contains(records, r => r is MessageRecord mr && mr.EntryId == "e3");
    }

    [Fact]
    public async Task ReadRecordsAsync_WithoutCompaction_ReturnsAllRecords()
    {
        ISessionStore store = CreateStores();
        SessionMeta session = await store.CreateAsync();
        string dir = store.GetSessionDirectory(session.Id);

        await AppendLineAsync(dir, "{\"type\":\"message\",\"entryId\":\"e1\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"role\":\"user\",\"parts\":[]}");
        await AppendLineAsync(dir, "{\"type\":\"compaction\",\"entryId\":\"c1\",\"supersedesUpTo\":\"e1\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"summary\":\"s\",\"reason\":\"manual\",\"tokensBefore\":0}");
        await AppendLineAsync(dir, "{\"type\":\"message\",\"entryId\":\"e2\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"role\":\"assistant\",\"parts\":[]}");

        IReadOnlyList<SessionLogLine> records = await store.ReadRecordsAsync(session.Id, applyCompaction: false);

        Assert.Equal(3, records.Count);
    }

    [Fact]
    public async Task ReadRecordsAsync_CompactionLineDeserializesToCompactionMessage()
    {
        ISessionStore store = CreateStores();
        SessionMeta session = await store.CreateAsync();
        string dir = store.GetSessionDirectory(session.Id);

        await AppendLineAsync(dir, "{\"type\":\"compaction\",\"entryId\":\"c1\",\"supersedesUpTo\":\"e1\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"summary\":\"my summary\",\"reason\":\"threshold\",\"tokensBefore\":5000}");

        IReadOnlyList<SessionLogLine> records = await store.ReadRecordsAsync(session.Id, applyCompaction: false);

        CompactionMessage cm = Assert.IsType<CompactionMessage>(Assert.Single(records));
        Assert.Equal("c1", cm.EntryId);
        Assert.Equal("e1", cm.SupersedesUpTo);
        Assert.Equal("my summary", cm.Summary);
        Assert.Equal(5000, cm.TokensBefore);
    }

    [Fact]
    public async Task ReadRecordsAsync_NoCompaction_ReturnsAllRecords()
    {
        ISessionStore store = CreateStores();
        SessionMeta session = await store.CreateAsync();

        await store.AppendMessageAsync(session.Id, "user", [new TextPart { Text = "a" }]);
        await store.AppendMessageAsync(session.Id, "assistant", [new TextPart { Text = "b" }]);

        IReadOnlyList<SessionLogLine> records = await store.ReadRecordsAsync(session.Id, applyCompaction: true);

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.IsType<MessageRecord>(r));
    }

    private static Task AppendLineAsync(string sessionDir, string json) =>
        File.AppendAllTextAsync(Path.Combine(sessionDir, "messages.jsonl"), json + "\n");

    private sealed class FakeResolver : ISessionDirectoryResolver
    {
        private readonly string _root;

        public FakeResolver(string root) => _root = root;

        public string Resolve(string workingDirectory) => _root;
    }
}
