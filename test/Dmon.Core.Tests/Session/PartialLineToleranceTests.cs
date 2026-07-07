using System.Text.Json;
using Dmon.Core.Session;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Session;

public sealed class PartialLineToleranceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly ISessionStore _store;

    public PartialLineToleranceTests()
    {
        Directory.CreateDirectory(_tempRoot);

        FakeResolver resolver = new(_tempRoot);
        IConfiguration config = new ConfigurationBuilder().Build();

        IAttachmentStore attachmentStore = new AttachmentStore(resolver, config);
        _store = new SessionStore(resolver, attachmentStore, NullLogger<SessionStore>.Instance, NullLoggerFactory.Instance, config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private async Task AppendLineAsync(string sessionDir, string json)
    {
        await File.AppendAllTextAsync(Path.Combine(sessionDir, "messages.jsonl"), json + "\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadMessagesAsync_WithTruncatedTrailingLine_ReturnsOnlyWellFormedRecords(bool applyCompaction)
    {
        SessionMeta session = await _store.CreateAsync();
        string dir = _store.GetSessionDirectory(session.Id);

        await AppendLineAsync(dir, "{\"entryId\":\"e1\",\"type\":\"user\"}");
        await AppendLineAsync(dir, "{\"entryId\":\"e2\",\"type\":\"assistant\"}");

        // Simulate a partial/truncated write (e.g. process killed mid-append) — not valid JSON.
        await AppendLineAsync(dir, "{\"entryId\":\"e3\",\"typ");

        IReadOnlyList<object> messages = await _store.ReadMessagesAsync(session.Id, applyCompaction: applyCompaction);
        List<JsonElement> elements = messages.Cast<JsonElement>().ToList();

        Assert.Equal(2, elements.Count);
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e1");
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e2");
        Assert.DoesNotContain(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e3");
    }

    [Fact]
    public async Task ReadMessagesAsync_WithMalformedInteriorLine_SkipsItAndAppliesCompactionCorrectly()
    {
        SessionMeta session = await _store.CreateAsync();
        string dir = _store.GetSessionDirectory(session.Id);

        await AppendLineAsync(dir, "{\"entryId\":\"e1\",\"type\":\"user\"}");
        await AppendLineAsync(dir, "{\"entryId\":\"e2\",\"type\":\"assistant\"}");

        // Malformed interior line — must be skipped without shifting compaction-scan positions.
        await AppendLineAsync(dir, "{not json");

        await AppendLineAsync(dir, "{\"entryId\":\"e3\",\"type\":\"user\"}");
        await AppendLineAsync(dir, "{\"type\":\"compaction\",\"entryId\":\"c1\",\"supersedesUpTo\":\"e2\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"summary\":\"s\",\"reason\":\"manual\",\"tokensBefore\":0}");
        await AppendLineAsync(dir, "{\"entryId\":\"e4\",\"type\":\"user\"}");

        IReadOnlyList<object> messages = await _store.ReadMessagesAsync(session.Id, applyCompaction: true);
        List<JsonElement> elements = messages.Cast<JsonElement>().ToList();

        // e1 and e2 are superseded by the compaction marker; the malformed line contributes nothing.
        Assert.DoesNotContain(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e1");
        Assert.DoesNotContain(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e2");

        // The compaction marker, e3, and e4 remain — proving the malformed interior line did not
        // shift the file-position arithmetic used to find the supersedesUpTo boundary.
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "c1");
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e3");
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e4");
        Assert.Equal(3, elements.Count);
    }

    [Fact]
    public async Task ReadMessagesAsync_AndReadRecordsAsync_AgreeOnWellFormedSet_WhenLineIsMalformed()
    {
        SessionMeta session = await _store.CreateAsync();
        string dir = _store.GetSessionDirectory(session.Id);

        // Full, valid MessageRecord JSON — required so ReadRecordsAsync's typed deserialization
        // (not just raw ReadMessagesAsync JsonElement parsing) succeeds for the well-formed lines.
        await AppendLineAsync(dir, "{\"type\":\"message\",\"entryId\":\"e1\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"role\":\"user\",\"parts\":[]}");
        await AppendLineAsync(dir, "{not valid json at all");
        await AppendLineAsync(dir, "{\"type\":\"message\",\"entryId\":\"e2\",\"timestamp\":\"2024-01-01T00:01:00Z\",\"role\":\"assistant\",\"parts\":[]}");

        IReadOnlyList<object> messages = await _store.ReadMessagesAsync(session.Id, applyCompaction: false);
        IReadOnlyList<SessionLogLine> records = await _store.ReadRecordsAsync(session.Id, applyCompaction: false);

        List<JsonElement> elements = messages.Cast<JsonElement>().ToList();
        HashSet<string> messageIds = elements
            .Select(e => e.TryGetProperty("entryId", out JsonElement p) ? p.GetString() : null)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToHashSet();

        HashSet<string> recordIds = records
            .OfType<MessageRecord>()
            .Select(r => r.EntryId)
            .ToHashSet();

        Assert.Equal(new HashSet<string> { "e1", "e2" }, messageIds);
        Assert.Equal(new HashSet<string> { "e1", "e2" }, recordIds);
    }

    private sealed class FakeResolver : ISessionDirectoryResolver
    {
        private readonly string _root;

        public FakeResolver(string root) => _root = root;

        public string Resolve(string workingDirectory) => _root;
    }
}
