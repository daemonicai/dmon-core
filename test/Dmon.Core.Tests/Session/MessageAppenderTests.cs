using System.Text.Json;
using Dmon.Core.Session;

namespace Dmon.Core.Tests.Session;

public sealed class MessageAppenderTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public MessageAppenderTests()
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

    private IMessageAppender CreateAppender()
    {
        FakeResolver resolver = new(_tempRoot);
        return new MessageAppender(resolver);
    }

    private string CreateSession(string sessionId)
    {
        string dir = Path.Combine(_tempRoot, sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "messages.jsonl"), string.Empty);
        return dir;
    }

    [Fact]
    public async Task AppendAsync_WritesLfTerminatedJsonLine()
    {
        string sessionId = Guid.NewGuid().ToString();
        CreateSession(sessionId);

        IMessageAppender appender = CreateAppender();
        object message = new { type = "user", content = "hello" };

        await appender.AppendAsync(sessionId, message);

        string messagesPath = Path.Combine(_tempRoot, sessionId, "messages.jsonl");
        string content = await File.ReadAllTextAsync(messagesPath);

        Assert.EndsWith("\n", content);
        // Should be valid JSON on a single line.
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        JsonElement parsed = JsonSerializer.Deserialize<JsonElement>(lines[0]);
        Assert.Equal("user", parsed.GetProperty("type").GetString());
    }

    [Fact]
    public async Task AppendAsync_MultipleAppends_AccumulateLines()
    {
        string sessionId = Guid.NewGuid().ToString();
        CreateSession(sessionId);

        IMessageAppender appender = CreateAppender();

        await appender.AppendAsync(sessionId, new { type = "user", content = "hello" });
        await appender.AppendAsync(sessionId, new { type = "assistant", content = "world" });
        await appender.AppendAsync(sessionId, new { type = "user", content = "again" });

        string messagesPath = Path.Combine(_tempRoot, sessionId, "messages.jsonl");
        string[] lines = (await File.ReadAllTextAsync(messagesPath))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task AppendCompactionAsync_WritesCompactionRecord()
    {
        string sessionId = Guid.NewGuid().ToString();
        CreateSession(sessionId);

        IMessageAppender appender = CreateAppender();
        CompactionMessage compaction = new()
        {
            EntryId = "c1",
            Timestamp = DateTimeOffset.UtcNow,
            Summary = "Summary text",
            SupersedesUpTo = "e10",
            Reason = "manual"
        };

        await appender.AppendCompactionAsync(sessionId, compaction);

        string messagesPath = Path.Combine(_tempRoot, sessionId, "messages.jsonl");
        string line = (await File.ReadAllTextAsync(messagesPath)).TrimEnd('\n');
        CompactionMessage? parsed = JsonSerializer.Deserialize<CompactionMessage>(line);

        Assert.NotNull(parsed);
        Assert.Equal("compaction", parsed.Type);
        Assert.Equal("c1", parsed.EntryId);
        Assert.Equal("e10", parsed.SupersedesUpTo);
    }

    private sealed class FakeResolver : ISessionDirectoryResolver
    {
        private readonly string _root;

        public FakeResolver(string root) => _root = root;

        public string Resolve(string workingDirectory) => _root;
    }
}
