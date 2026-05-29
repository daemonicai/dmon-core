using Dmon.Abstractions.Memory;
using Dmon.Core.Session;
using Dmon.Memory.Index;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Memory.Tests.Stubs;

/// <summary>
/// Per-test fixture: isolated temp directory, real <see cref="MessageAppender"/> +
/// <see cref="SessionStore"/>, and a <see cref="StubEmbeddingGenerator"/>.
///
/// Call <see cref="BuildMemoryAsync"/> to get an initialized <see cref="ShortTermMemory"/>
/// pointed at a fresh session directory.  Call <see cref="DisposeAsync"/> (or wrap in
/// <c>await using</c>) to delete the temp directory.
/// </summary>
internal sealed class TempSessionFixture : IAsyncDisposable
{
    // Root temp directory for this fixture instance.
    private readonly string _root;

    public StubEmbeddingGenerator EmbeddingGenerator { get; } = new();

    private TempSessionFixture(string root)
    {
        _root = root;
    }

    /// <summary>Creates a new fixture backed by a freshly-created temp directory.</summary>
    public static TempSessionFixture Create()
    {
        string root = Path.Combine(Path.GetTempPath(), "dmon-mem-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TempSessionFixture(root);
    }

    /// <summary>
    /// Creates a session directory under the fixture root and returns an initialized
    /// <see cref="ShortTermMemory"/> bound to it.
    ///
    /// The returned <see cref="ShortTermMemory"/> shares <see cref="EmbeddingGenerator"/>
    /// so the caller can configure vectors before recording turns.
    /// </summary>
    public async Task<(ShortTermMemory Memory, string SessionId)> BuildMemoryAsync(
        IEmbeddingGenerator<string, Embedding<float>>? generator = null,
        CancellationToken cancellationToken = default)
    {
        string sessionId = Guid.NewGuid().ToString("N");
        string sessionDir = Path.Combine(_root, sessionId);
        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "attachments"));
        File.Create(Path.Combine(sessionDir, "messages.jsonl")).Dispose();

        return await OpenMemoryAsync(sessionId, generator, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens an existing session directory (created by a previous <see cref="BuildMemoryAsync"/>
    /// call) and returns an initialized <see cref="ShortTermMemory"/> bound to it.
    /// Does NOT recreate <c>messages.jsonl</c> — the existing file is reused.
    /// Used for rebuild tests where the session directory already exists.
    /// </summary>
    public async Task<(ShortTermMemory Memory, string SessionId)> BuildMemoryAsync(
        string sessionId,
        IEmbeddingGenerator<string, Embedding<float>>? generator = null,
        CancellationToken cancellationToken = default)
    {
        return await OpenMemoryAsync(sessionId, generator, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(ShortTermMemory Memory, string SessionId)> OpenMemoryAsync(
        string sessionId,
        IEmbeddingGenerator<string, Embedding<float>>? generator,
        CancellationToken cancellationToken)
    {
        IEmbeddingGenerator<string, Embedding<float>> gen = generator ?? EmbeddingGenerator;
        ISessionDirectoryResolver resolver = new FixedRootResolver(_root);
        IConfiguration config = new ConfigurationBuilder().Build();
        IMessageAppender appender = new MessageAppender(resolver);
        SessionStore store = new(
            resolver,
            NullLogger<SessionStore>.Instance,
            new NullLoggerFactory(),
            config);
        MemoryContext context = new("test-dp", "dmon", sessionId);

        ShortTermMemory memory = new(gen, appender, store, resolver, context);
        await memory.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return (memory, sessionId);
    }

    /// <summary>Returns the absolute path to <c>index.db</c> for a session.</summary>
    public string IndexDbPath(string sessionId) =>
        Path.Combine(_root, sessionId, "index.db");

    /// <summary>Returns the absolute path to <c>messages.jsonl</c> for a session.</summary>
    public string MessagesJsonlPath(string sessionId) =>
        Path.Combine(_root, sessionId, "messages.jsonl");

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    // ── FixedRootResolver ────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ISessionDirectoryResolver"/> that always returns the same root,
    /// regardless of working directory. Lets tests own the session location without
    /// needing a .dmon/config.yaml on disk.
    /// </summary>
    private sealed class FixedRootResolver(string root) : ISessionDirectoryResolver
    {
        public string Resolve(string workingDirectory) => root;
    }
}
