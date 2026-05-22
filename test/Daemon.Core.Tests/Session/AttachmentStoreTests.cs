using System.IO;
using Daemon.Core.Session;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Daemon.Core.Tests.Session;

public sealed class AttachmentStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public AttachmentStoreTests()
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

    private IAttachmentStore CreateStore(int? thresholdBytes = null)
    {
        FakeResolver resolver = new(_tempRoot);
        IConfigurationBuilder builder = new ConfigurationBuilder();

        if (thresholdBytes.HasValue)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Daemon:Session:AttachmentThresholdBytes"] = thresholdBytes.Value.ToString()
            });
        }

        return new AttachmentStore(resolver, builder.Build());
    }

    private string CreateSession(string sessionId)
    {
        string dir = Path.Combine(_tempRoot, sessionId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task StoreIfLargeAsync_BelowThreshold_ReturnsNull()
    {
        string sessionId = Guid.NewGuid().ToString();
        CreateSession(sessionId);

        IAttachmentStore store = CreateStore(thresholdBytes: 1024);

        string? result = await store.StoreIfLargeAsync(sessionId, "call1", "short content");

        Assert.Null(result);
    }

    [Fact]
    public async Task StoreIfLargeAsync_AboveThreshold_WritesFileAndReturnsPath()
    {
        string sessionId = Guid.NewGuid().ToString();
        CreateSession(sessionId);

        IAttachmentStore store = CreateStore(thresholdBytes: 10);
        string content = new string('x', 100);

        string? result = await store.StoreIfLargeAsync(sessionId, "call1", content, "txt");

        Assert.NotNull(result);
        Assert.Equal("attachments/call1.txt", result);

        string filePath = Path.Combine(_tempRoot, sessionId, "attachments", "call1.txt");
        Assert.True(File.Exists(filePath));
        Assert.Equal(content, await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task StoreIfLargeAsync_DefaultThresholdIs1024()
    {
        string sessionId = Guid.NewGuid().ToString();
        CreateSession(sessionId);

        IAttachmentStore store = CreateStore(); // no threshold configured — use default 1024
        string below = new string('a', 500);
        string above = new string('b', 1025);

        string? belowResult = await store.StoreIfLargeAsync(sessionId, "c1", below);
        string? aboveResult = await store.StoreIfLargeAsync(sessionId, "c2", above);

        Assert.Null(belowResult);
        Assert.NotNull(aboveResult);
    }

    [Fact]
    public async Task StoreIfLargeAsync_UsesExtensionInFileName()
    {
        string sessionId = Guid.NewGuid().ToString();
        CreateSession(sessionId);

        IAttachmentStore store = CreateStore(thresholdBytes: 1);
        string content = "some content";

        string? result = await store.StoreIfLargeAsync(sessionId, "call99", content, "json");

        Assert.Equal("attachments/call99.json", result);
        Assert.True(File.Exists(Path.Combine(_tempRoot, sessionId, "attachments", "call99.json")));
    }

    private sealed class FakeResolver : ISessionDirectoryResolver
    {
        private readonly string _root;

        public FakeResolver(string root) => _root = root;

        public string Resolve(string workingDirectory) => _root;
    }
}
