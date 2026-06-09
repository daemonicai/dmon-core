using Dmon.Core.Session;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Session;

public sealed class SessionForkCloneTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly ISessionStore _store;

    public SessionForkCloneTests()
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

    private async Task AppendLineAsync(string sessionDir, string json)
    {
        string path = Path.Combine(sessionDir, "messages.jsonl");
        await File.AppendAllTextAsync(path, json + "\n");
    }

    [Fact]
    public async Task ForkAsync_TruncatesAtEntryId()
    {
        SessionMeta source = await _store.CreateAsync("source");
        string sourceDir = _store.GetSessionDirectory(source.Id);

        await AppendLineAsync(sourceDir, "{\"entryId\":\"e1\",\"type\":\"user\"}");
        await AppendLineAsync(sourceDir, "{\"entryId\":\"e2\",\"type\":\"assistant\"}");
        await AppendLineAsync(sourceDir, "{\"entryId\":\"e3\",\"type\":\"user\"}");

        SessionMeta fork = await _store.ForkAsync(source.Id, "e2");

        string forkDir = _store.GetSessionDirectory(fork.Id);
        string[] lines = (await File.ReadAllTextAsync(Path.Combine(forkDir, "messages.jsonl")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Should retain e1 and e2, not e3.
        Assert.Equal(2, lines.Length);
        Assert.Contains("e1", lines[0]);
        Assert.Contains("e2", lines[1]);
    }

    [Fact]
    public async Task ForkAsync_SetsParentSessionAndForkEntryId()
    {
        SessionMeta source = await _store.CreateAsync();
        string sourceDir = _store.GetSessionDirectory(source.Id);

        await AppendLineAsync(sourceDir, "{\"entryId\":\"e1\",\"type\":\"user\"}");

        SessionMeta fork = await _store.ForkAsync(source.Id, "e1", "forked");

        Assert.Equal(source.Id, fork.ParentSession);
        Assert.Equal("e1", fork.ForkEntryId);
        Assert.Equal("forked", fork.Name);
    }

    [Fact]
    public async Task ForkAsync_SourceIsNotMutated()
    {
        SessionMeta source = await _store.CreateAsync();
        string sourceDir = _store.GetSessionDirectory(source.Id);

        await AppendLineAsync(sourceDir, "{\"entryId\":\"e1\",\"type\":\"user\"}");
        await AppendLineAsync(sourceDir, "{\"entryId\":\"e2\",\"type\":\"assistant\"}");
        await AppendLineAsync(sourceDir, "{\"entryId\":\"e3\",\"type\":\"user\"}");

        await _store.ForkAsync(source.Id, "e1");

        string[] sourceLines = (await File.ReadAllTextAsync(Path.Combine(sourceDir, "messages.jsonl")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Source retains all 3 lines.
        Assert.Equal(3, sourceLines.Length);
    }

    [Fact]
    public async Task ForkAsync_PrunesUnreferencedAttachments()
    {
        SessionMeta source = await _store.CreateAsync();
        string sourceDir = _store.GetSessionDirectory(source.Id);

        // Write an attachment.
        string attachmentsDir = Path.Combine(sourceDir, "attachments");
        Directory.CreateDirectory(attachmentsDir);
        File.WriteAllText(Path.Combine(attachmentsDir, "e1.txt"), "content1");
        File.WriteAllText(Path.Combine(attachmentsDir, "e2.txt"), "content2");

        // e1 message references attachments/e1.txt via attachmentPath; e2 message references attachments/e2.txt.
        await AppendLineAsync(sourceDir, "{\"entryId\":\"e1\",\"type\":\"tool\",\"attachmentPath\":\"attachments/e1.txt\"}");
        await AppendLineAsync(sourceDir, "{\"entryId\":\"e2\",\"type\":\"tool\",\"attachmentPath\":\"attachments/e2.txt\"}");

        // Fork at e1 — e2 message is pruned, so e2.txt should be deleted.
        SessionMeta fork = await _store.ForkAsync(source.Id, "e1");
        string forkAttachmentsDir = Path.Combine(_store.GetSessionDirectory(fork.Id), "attachments");

        Assert.True(File.Exists(Path.Combine(forkAttachmentsDir, "e1.txt")));
        Assert.False(File.Exists(Path.Combine(forkAttachmentsDir, "e2.txt")));
    }

    [Fact]
    public async Task CloneAsync_CopiesAllMessages()
    {
        SessionMeta source = await _store.CreateAsync();
        string sourceDir = _store.GetSessionDirectory(source.Id);

        await AppendLineAsync(sourceDir, "{\"entryId\":\"e1\",\"type\":\"user\"}");
        await AppendLineAsync(sourceDir, "{\"entryId\":\"e2\",\"type\":\"assistant\"}");

        SessionMeta clone = await _store.CloneAsync(source.Id, "cloned");
        string cloneDir = _store.GetSessionDirectory(clone.Id);

        string[] lines = (await File.ReadAllTextAsync(Path.Combine(cloneDir, "messages.jsonl")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal(source.Id, clone.ParentSession);
        Assert.Null(clone.ForkEntryId);
        Assert.Equal("cloned", clone.Name);
    }

    [Fact]
    public async Task CloneAsync_AssignsNewId()
    {
        SessionMeta source = await _store.CreateAsync();
        SessionMeta clone = await _store.CloneAsync(source.Id);

        Assert.NotEqual(source.Id, clone.Id);
    }


    [Fact]
    public async Task ForkAsync_ThrowsWhenEntryIdNotFound()
    {
        SessionMeta source = await _store.CreateAsync("source");
        string sourceDir = _store.GetSessionDirectory(source.Id);

        await AppendLineAsync(sourceDir, "{\"entryId\":\"e1\",\"type\":\"user\"}");
        await AppendLineAsync(sourceDir, "{\"entryId\":\"e2\",\"type\":\"assistant\"}");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.ForkAsync(source.Id, "does-not-exist"));
    }

    // ── Profile inheritance (task 3.3) ────────────────────────────────────────

    [Fact]
    public async Task ForkAsync_InheritsSourceProfile()
    {
        SessionMeta source = await _store.CreateAsync(profile: "researcher");
        string sourceDir = _store.GetSessionDirectory(source.Id);
        await AppendLineAsync(sourceDir, "{\"entryId\":\"e1\",\"type\":\"user\"}");

        SessionMeta fork = await _store.ForkAsync(source.Id, "e1");

        Assert.Equal("researcher", fork.Profile);
    }

    [Fact]
    public async Task ForkAsync_SourceWithNoProfile_ForkProfileIsNull()
    {
        SessionMeta source = await _store.CreateAsync();
        string sourceDir = _store.GetSessionDirectory(source.Id);
        await AppendLineAsync(sourceDir, "{\"entryId\":\"e1\",\"type\":\"user\"}");

        SessionMeta fork = await _store.ForkAsync(source.Id, "e1");

        Assert.Null(fork.Profile);
    }

    [Fact]
    public async Task CloneAsync_InheritsSourceProfile()
    {
        SessionMeta source = await _store.CreateAsync(profile: "researcher");

        SessionMeta clone = await _store.CloneAsync(source.Id);

        Assert.Equal("researcher", clone.Profile);
    }

    [Fact]
    public async Task CloneAsync_SourceWithNoProfile_CloneProfileIsNull()
    {
        SessionMeta source = await _store.CreateAsync();

        SessionMeta clone = await _store.CloneAsync(source.Id);

        Assert.Null(clone.Profile);
    }

    private sealed class FakeResolver : ISessionDirectoryResolver
    {
        private readonly string _root;

        public FakeResolver(string root) => _root = root;

        public string Resolve(string workingDirectory) => _root;
    }
}
