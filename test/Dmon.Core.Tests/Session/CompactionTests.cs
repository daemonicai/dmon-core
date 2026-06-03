using System.Text.Json;
using Dmon.Core.Session;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Session;

public sealed class CompactionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly ISessionStore _store;
    private readonly IMessageAppender _appender;

    public CompactionTests()
    {
        Directory.CreateDirectory(_tempRoot);

        FakeResolver resolver = new(_tempRoot);
        IConfiguration config = new ConfigurationBuilder().Build();

        _store = new SessionStore(resolver, NullLogger<SessionStore>.Instance, NullLoggerFactory.Instance, config);
        _appender = new MessageAppender(resolver);
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

    [Fact]
    public async Task AppendCompactionAsync_WritesCompactionToFile()
    {
        SessionMeta session = await _store.CreateAsync();

        CompactionMessage compaction = new()
        {
            EntryId = "c1",
            Timestamp = DateTimeOffset.UtcNow,
            Summary = "A summary",
            SupersedesUpTo = "e5",
            Reason = "manual",
            TokensBefore = 10000
        };

        await _appender.AppendCompactionAsync(session.Id, compaction);

        string dir = _store.GetSessionDirectory(session.Id);
        string content = await File.ReadAllTextAsync(Path.Combine(dir, "messages.jsonl"));
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);

        CompactionMessage? parsed = JsonSerializer.Deserialize<CompactionMessage>(lines[0]);
        Assert.NotNull(parsed);
        Assert.Equal("compaction", parsed.Type);
        Assert.Equal("c1", parsed.EntryId);
        Assert.Equal("e5", parsed.SupersedesUpTo);
        Assert.Equal(10000, parsed.TokensBefore);
    }

    [Fact]
    public async Task ReadMessagesAsync_WithApplyCompaction_SkipsSupersededMessages()
    {
        SessionMeta session = await _store.CreateAsync();
        string dir = _store.GetSessionDirectory(session.Id);

        await AppendLineAsync(dir, "{\"entryId\":\"e1\",\"type\":\"user\"}");
        await AppendLineAsync(dir, "{\"entryId\":\"e2\",\"type\":\"assistant\"}");
        await AppendLineAsync(dir, "{\"entryId\":\"e3\",\"type\":\"user\"}");
        await AppendLineAsync(dir, "{\"type\":\"compaction\",\"entryId\":\"c1\",\"supersedesUpTo\":\"e2\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"summary\":\"s\",\"reason\":\"manual\",\"tokensBefore\":0}");
        await AppendLineAsync(dir, "{\"entryId\":\"e4\",\"type\":\"user\"}");

        IReadOnlyList<object> messages = await _store.ReadMessagesAsync(session.Id, applyCompaction: true);

        // e1 and e2 are superseded; compaction marker itself is retained; e3 and e4 are retained.
        List<JsonElement> elements = messages.Cast<JsonElement>().ToList();

        // Should NOT contain e1 or e2.
        Assert.DoesNotContain(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e1");
        Assert.DoesNotContain(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e2");

        // Should contain compaction, e3, e4.
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "c1");
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e3");
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e4");
    }

    [Fact]
    public async Task ReadMessagesAsync_WithoutApplyCompaction_ReturnsAllLines()
    {
        SessionMeta session = await _store.CreateAsync();
        string dir = _store.GetSessionDirectory(session.Id);

        await AppendLineAsync(dir, "{\"entryId\":\"e1\",\"type\":\"user\"}");
        await AppendLineAsync(dir, "{\"type\":\"compaction\",\"entryId\":\"c1\",\"supersedesUpTo\":\"e1\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"summary\":\"s\",\"reason\":\"manual\",\"tokensBefore\":0}");

        IReadOnlyList<object> messages = await _store.ReadMessagesAsync(session.Id, applyCompaction: false);

        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task ReadMessagesAsync_LastCompactionWins()
    {
        SessionMeta session = await _store.CreateAsync();
        string dir = _store.GetSessionDirectory(session.Id);

        await AppendLineAsync(dir, "{\"entryId\":\"e1\",\"type\":\"user\"}");
        await AppendLineAsync(dir, "{\"entryId\":\"e2\",\"type\":\"assistant\"}");
        await AppendLineAsync(dir, "{\"type\":\"compaction\",\"entryId\":\"c1\",\"supersedesUpTo\":\"e1\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"summary\":\"s\",\"reason\":\"manual\",\"tokensBefore\":0}");
        await AppendLineAsync(dir, "{\"entryId\":\"e3\",\"type\":\"user\"}");
        await AppendLineAsync(dir, "{\"type\":\"compaction\",\"entryId\":\"c2\",\"supersedesUpTo\":\"e3\",\"timestamp\":\"2024-01-02T00:00:00Z\",\"summary\":\"s2\",\"reason\":\"threshold\",\"tokensBefore\":0}");
        await AppendLineAsync(dir, "{\"entryId\":\"e4\",\"type\":\"user\"}");

        IReadOnlyList<object> messages = await _store.ReadMessagesAsync(session.Id, applyCompaction: true);

        List<JsonElement> elements = messages.Cast<JsonElement>().ToList();

        // Last compaction (c2) supersedes up to e3, so e1, e2, e3 should all be gone.
        Assert.DoesNotContain(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e1");
        Assert.DoesNotContain(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e2");
        Assert.DoesNotContain(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e3");

        // c2 and e4 should remain.
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "c2");
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == "e4");
    }

    [Fact]
    public async Task ReadMessagesAsync_WithUuidEntryIds_UsesPositionNotLexicographicOrder()
    {
        // Regression: UUID entryIds are not lexicographically monotonic. Compaction filtering
        // must use file position, not string comparison, to determine what is superseded.
        SessionMeta session = await _store.CreateAsync();
        string dir = _store.GetSessionDirectory(session.Id);

        // Use deterministic IDs that are deliberately in reverse lexicographic order by file position.
        // Any algorithm that sorts by entryId string value would wrongly treat aaa-msg-3 as coming
        // before zzz-msg-1, causing it to be incorrectly filtered out by the compaction marker.
        string id1 = "zzz-msg-1";  // lex-greatest, first in file
        string id2 = "mmm-msg-2";  // middle lex, second in file
        string id3 = "aaa-msg-3";  // lex-smallest, third in file — must be retained
        string compactionId = "ccc-compaction";
        string id4 = "ddd-msg-4";

        await AppendLineAsync(dir, $"{{\"entryId\":\"{id1}\",\"type\":\"user\"}}");
        await AppendLineAsync(dir, $"{{\"entryId\":\"{id2}\",\"type\":\"assistant\"}}");
        await AppendLineAsync(dir, $"{{\"entryId\":\"{id3}\",\"type\":\"user\"}}");
        await AppendLineAsync(dir, $"{{\"type\":\"compaction\",\"entryId\":\"{compactionId}\",\"supersedesUpTo\":\"{id2}\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"summary\":\"s\",\"reason\":\"manual\",\"tokensBefore\":0}}");
        await AppendLineAsync(dir, $"{{\"entryId\":\"{id4}\",\"type\":\"user\"}}");

        IReadOnlyList<object> messages = await _store.ReadMessagesAsync(session.Id, applyCompaction: true);

        List<JsonElement> elements = messages.Cast<JsonElement>().ToList();

        // id1 and id2 are superseded (at or before supersedesUpTo position in file).
        Assert.DoesNotContain(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == id1);
        Assert.DoesNotContain(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == id2);

        // id3 is after id2 by file position but lexicographically smallest — a broken algorithm that
        // uses string comparison would wrongly drop it; the position-based algorithm must retain it.
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == id3);

        // The compaction marker and id4 must be present.
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == compactionId);
        Assert.Contains(elements, e => e.TryGetProperty("entryId", out JsonElement p) && p.GetString() == id4);
    }

    private sealed class FakeResolver : ISessionDirectoryResolver
    {
        private readonly string _root;

        public FakeResolver(string root) => _root = root;

        public string Resolve(string workingDirectory) => _root;
    }
}
