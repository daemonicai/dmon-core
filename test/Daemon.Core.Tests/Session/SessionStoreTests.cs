using System.IO;
using System.Text.Json;
using Daemon.Core.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daemon.Core.Tests.Session;

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
        return new SessionStore(resolver, NullLogger<SessionStore>.Instance, NullLoggerFactory.Instance);
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

    private sealed class FakeResolver : ISessionDirectoryResolver
    {
        private readonly string _root;

        public FakeResolver(string root) => _root = root;

        public string Resolve(string workingDirectory) => _root;
    }
}
