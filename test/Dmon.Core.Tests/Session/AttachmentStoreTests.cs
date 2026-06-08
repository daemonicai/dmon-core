using Dmon.Core.Session;
using Microsoft.Extensions.Configuration;

namespace Dmon.Core.Tests.Session;

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
                ["Dmon:Session:AttachmentThresholdBytes"] = thresholdBytes.Value.ToString()
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

    // ---- Path-traversal guard tests (task 1.1–1.3) ----

    [Fact]
    public async Task StoreIfLargeAsync_SafeCallId_WritesUnderCallIdName()
    {
        string sessionId = Guid.NewGuid().ToString();
        CreateSession(sessionId);

        IAttachmentStore store = CreateStore(thresholdBytes: 1);
        string content = new string('z', 50);

        string? result = await store.StoreIfLargeAsync(sessionId, "safe-call_01", content, "txt");

        Assert.Equal("attachments/safe-call_01.txt", result);
        Assert.True(File.Exists(Path.Combine(_tempRoot, sessionId, "attachments", "safe-call_01.txt")));
    }

    [Fact]
    public async Task StoreIfLargeAsync_TraversalCallId_WritesInsideAttachmentsDir()
    {
        string sessionId = Guid.NewGuid().ToString();
        CreateSession(sessionId);

        IAttachmentStore store = CreateStore(thresholdBytes: 1);
        string content = new string('e', 50);

        string? result = await store.StoreIfLargeAsync(sessionId, "../../etc/evil", content, "txt");

        Assert.NotNull(result);

        // The attachmentRef must be resolvable inside the session attachments dir.
        string attachmentsDir = Path.Combine(_tempRoot, sessionId, "attachments");
        string resolvedRef = Path.GetFullPath(Path.Combine(attachmentsDir, result!["attachments/".Length..]));
        string resolvedAttachmentsDir = Path.GetFullPath(attachmentsDir) + Path.DirectorySeparatorChar;
        Assert.StartsWith(resolvedAttachmentsDir, resolvedRef, StringComparison.Ordinal);

        // No file must have been written at the traversal target.
        string evilTarget = Path.GetFullPath(Path.Combine(_tempRoot, sessionId, "../../etc/evil.txt"));
        Assert.False(File.Exists(evilTarget));

        // The written file must exist and contain the expected content.
        string writtenPath = Path.GetFullPath(Path.Combine(attachmentsDir, result!["attachments/".Length..]));
        Assert.True(File.Exists(writtenPath));
        Assert.Equal(content, await File.ReadAllTextAsync(writtenPath));
    }

    [Fact]
    public async Task StoreIfLargeAsync_TwoDistinctUnsafeCallIds_ProduceDistinctFiles()
    {
        string sessionId = Guid.NewGuid().ToString();
        CreateSession(sessionId);

        IAttachmentStore store = CreateStore(thresholdBytes: 1);
        string content1 = new string('a', 50);
        string content2 = new string('b', 50);

        string? ref1 = await store.StoreIfLargeAsync(sessionId, "../../etc/evil", content1, "txt");
        string? ref2 = await store.StoreIfLargeAsync(sessionId, "../other/path", content2, "txt");

        Assert.NotNull(ref1);
        Assert.NotNull(ref2);
        Assert.NotEqual(ref1, ref2);

        string attachmentsDir = Path.Combine(_tempRoot, sessionId, "attachments");
        string file1 = Path.Combine(attachmentsDir, ref1!["attachments/".Length..]);
        string file2 = Path.Combine(attachmentsDir, ref2!["attachments/".Length..]);

        Assert.True(File.Exists(file1));
        Assert.True(File.Exists(file2));
        Assert.Equal(content1, await File.ReadAllTextAsync(file1));
        Assert.Equal(content2, await File.ReadAllTextAsync(file2));
    }

    private sealed class FakeResolver : ISessionDirectoryResolver
    {
        private readonly string _root;

        public FakeResolver(string root) => _root = root;

        public string Resolve(string workingDirectory) => _root;
    }
}
