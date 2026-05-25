using Dmon.Abstractions;
using Dmon.Abstractions.Providers;
using Dmon.Core.Config;
using Dmon.Core.Extensions;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.SystemPrompt;
using Dmon.Core.Tests.Fakes;
using Dmon.Extensions;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.SystemPrompt;

/// <summary>
/// Tests for SystemPromptBuilder.
///
/// SystemPromptBuilder delegates config discovery to AgentConfigResolver which
/// reads Directory.GetCurrentDirectory(). All tests here are serialised via
/// [Collection("FileSystemCwd")] alongside AgentConfigResolverTests to
/// prevent parallel CWD mutation.
/// </summary>
[Collection("FileSystemCwd")]
public sealed class SystemPromptBuilderTests : IDisposable
{
    // Use the solution root as the stable restore target — guaranteed to exist.
    private static readonly string StableRestoreDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private readonly string _tempDir;

    public SystemPromptBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(StableRestoreDir);
        Directory.Delete(_tempDir, recursive: true);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static SystemPromptBuilder MakeBuilder(FakeEventEmitter? emitter = null)
    {
        StubProviderRegistry providers = new();
        EmptyToolRegistry tools = new();
        AgentConfigResolver configResolver = new();
        IEventEmitter eventEmitter = emitter ?? new FakeEventEmitter();
        return new SystemPromptBuilder(providers, tools, configResolver, eventEmitter);
    }

    private void WriteProjectFile(string filename, string content)
        => File.WriteAllText(Path.Combine(_tempDir, filename), content);

    // ── 5.1 — no config files present ───────────────────────────────────────

    [Fact]
    public async Task Build_NoConfigFiles_ReturnsSystemMessage()
    {
        SystemPromptBuilder builder = MakeBuilder();

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        Assert.Equal(ChatRole.System, msg.Role);
        Assert.NotNull(msg.Text);
        Assert.NotEmpty(msg.Text);
    }

    [Fact]
    public async Task Build_NoConfigFiles_ContainsStaticCoreIdentity()
    {
        SystemPromptBuilder builder = MakeBuilder();

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        Assert.Contains("D-mon", msg.Text);
    }

    [Fact]
    public async Task Build_NoConfigFiles_ContainsCwd()
    {
        SystemPromptBuilder builder = MakeBuilder();

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        // Dynamic context block includes the working directory.
        Assert.Contains(_tempDir, msg.Text);
    }

    [Fact]
    public async Task Build_NoConfigFiles_DoesNotContainProjectConfigSection()
    {
        SystemPromptBuilder builder = MakeBuilder();

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        Assert.DoesNotContain("## Project configuration", msg.Text);
    }

    // ── 5.3 — CLAUDE.md fallback emits system.notice ────────────────────────

    [Fact]
    public async Task Build_ClaudeMdFallback_EmitsSystemNoticeEvent()
    {
        WriteProjectFile("CLAUDE.md", "# Project config\n\nSome rules.\n");
        FakeEventEmitter emitter = new();
        SystemPromptBuilder builder = MakeBuilder(emitter);

        await builder.BuildAsync(CancellationToken.None);

        SystemNoticeEvent? notice = emitter.Emitted.OfType<SystemNoticeEvent>().FirstOrDefault();
        Assert.NotNull(notice);
        Assert.Contains("CLAUDE.md", notice.Message);
    }

    [Fact]
    public async Task Build_AgentsMdPresent_DoesNotEmitSystemNotice()
    {
        WriteProjectFile("AGENTS.md", "# Project config\n\nSome rules.\n");
        FakeEventEmitter emitter = new();
        SystemPromptBuilder builder = MakeBuilder(emitter);

        await builder.BuildAsync(CancellationToken.None);

        Assert.DoesNotContain(emitter.Emitted, e => e is SystemNoticeEvent);
    }

    [Fact]
    public async Task Build_ClaudeMdFallback_IncludesContentInPrompt()
    {
        const string content = "# My project\n\nDo not break things.\n";
        WriteProjectFile("CLAUDE.md", content);
        SystemPromptBuilder builder = MakeBuilder();

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        Assert.Contains("Do not break things", msg.Text);
    }

    [Fact]
    public async Task Build_AgentsMd_IncludesContentInPrompt()
    {
        const string content = "# My project\n\nAlways write tests.\n";
        WriteProjectFile("AGENTS.md", content);
        SystemPromptBuilder builder = MakeBuilder();

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        Assert.Contains("Always write tests", msg.Text);
    }

    // ── stubs ────────────────────────────────────────────────────────────────

    private sealed class StubProviderRegistry : IProviderRegistry
    {
        public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IChatClient>(new NullChatClient());

        public ProviderConfig GetCurrentConfig() => new()
        {
            Name = "stub",
            Adapter = "stub",
            Auth = new ProviderAuthConfig { Type = "none" }
        };

        public IReadOnlyList<ProviderConfig> GetAll() => [GetCurrentConfig()];

        public void SetProvider(string name) { }
        public void SetModel(string modelId) { }
        public void CycleProvider() { }
        public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public void AddDynamicProvider(ProviderConfig config) { }
        public ProviderSwitchResult? CommitPendingSwitch() => null;
        public bool CurrentSupportsToolCalling => false;
        public bool CurrentSupportsReasoning => false;
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public IReadOnlyList<AIFunction> GetAll() => [];
        public void Register(string extensionName, IDmonExtension extension, IEnumerable<AIFunction> tools) { }
        public IDmonExtension? FindExtension(string toolName) => null;
        public void Unregister(string extensionName) { }
        public IReadOnlyList<RegisteredExtensionSnapshot> GetSnapshot() => [];
        public void Clear() { }
    }

    private sealed class NullChatClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }
}
