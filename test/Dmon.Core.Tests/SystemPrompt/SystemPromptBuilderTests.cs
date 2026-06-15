using Dmon.Abstractions;
using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Core.Config;
using Dmon.Core.Extensions;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Core.SystemPrompt;
using Dmon.Hosting;
using Dmon.Protocol.Sessions;
using Dmon.Core.Tests.Fakes;
using Dmon.Abstractions.Extensions;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

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

    private static SystemPromptBuilder MakeBuilder(
        FakeEventEmitter? emitter = null,
        ISessionHandler? sessionHandler = null,
        IConfiguration? configuration = null,
        IEnumerable<SystemPromptAppend>? appends = null,
        AssetsOptions? assetsOptions = null)
    {
        StubProviderRegistry providers = new();
        EmptyToolRegistry tools = new();
        AgentConfigResolver configResolver = new();
        IEventEmitter eventEmitter = emitter ?? new FakeEventEmitter();
        ISessionHandler session = sessionHandler ?? new StubSessionHandler();
        IConfiguration config = configuration ?? new ConfigurationBuilder().Build();
        IEnumerable<SystemPromptAppend> promptAppends = appends ?? [];
        return new SystemPromptBuilder(providers, tools, configResolver, eventEmitter, session, config, promptAppends, assetsOptions);
    }

    private void WriteProjectFile(string filename, string content)
        => File.WriteAllText(Path.Combine(_tempDir, filename), content);

    private static IConfiguration ConfigWith(string key, string value)
        => new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)])
            .Build();

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

        // Built-in default identifies the agent as D-mon.
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

    // ── 6.1 — base precedence: built-in default when nothing is set ─────────

    [Fact]
    public async Task Build_NothingSet_UsesBuiltInDefault()
    {
        SystemPromptBuilder builder = MakeBuilder();

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        // Built-in default contains "D-mon" identity.
        Assert.Contains("D-mon", msg.Text);
    }

    // ── 6.1 — base precedence: config systemPrompt overrides built-in ────────

    [Fact]
    public async Task Build_ConfigSystemPrompt_OverridesBuiltInDefault()
    {
        IConfiguration config = ConfigWith(ConfigurationKeys.SystemPrompt, "You are a test agent.");
        SystemPromptBuilder builder = MakeBuilder(configuration: config);

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        Assert.Contains("You are a test agent.", msg.Text);
        Assert.DoesNotContain("D-mon", msg.Text);
    }

    // ── 6.1 — base precedence: UseSystemPrompt outranks config ───────────────
    // UseSystemPrompt writes via AddInMemoryCollection (last-wins), so a higher-priority
    // in-memory entry beats the YAML/env layer read via the same config key.

    [Fact]
    public async Task Build_UseSystemPromptEntry_OutranksConfigLayer()
    {
        // Simulate UseSystemPrompt writing at higher precedence than the base config value.
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(ConfigurationKeys.SystemPrompt, "base-config")])
            .AddInMemoryCollection([new KeyValuePair<string, string?>(ConfigurationKeys.SystemPrompt, "verb-override")])
            .Build();
        SystemPromptBuilder builder = MakeBuilder(configuration: config);

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        Assert.Contains("verb-override", msg.Text);
        Assert.DoesNotContain("base-config", msg.Text);
    }

    // ── 6.1 — AppendToSystemPrompt: single append added after base ───────────

    [Fact]
    public async Task Build_SingleAppend_ComposesAfterBase()
    {
        const string appendText = "\n\n## Extra rules\n\nDo not panic.\n";
        SystemPromptAppend[] appends = [new SystemPromptAppend(appendText)];
        SystemPromptBuilder builder = MakeBuilder(appends: appends);

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        // Both base (built-in default) and append must appear.
        Assert.Contains("D-mon", msg.Text);
        Assert.Contains("Do not panic.", msg.Text);
    }

    // ── 6.1 — AppendToSystemPrompt: multiple appends in call order ───────────

    [Fact]
    public async Task Build_MultipleAppends_ComposesInRegistrationOrder()
    {
        SystemPromptAppend[] appends =
        [
            new SystemPromptAppend("FIRST"),
            new SystemPromptAppend("SECOND"),
        ];
        SystemPromptBuilder builder = MakeBuilder(appends: appends);

        ChatMessage msg = await builder.BuildAsync(CancellationToken.None);

        int indexFirst = msg.Text!.IndexOf("FIRST", StringComparison.Ordinal);
        int indexSecond = msg.Text.IndexOf("SECOND", StringComparison.Ordinal);
        Assert.True(indexFirst >= 0, "FIRST not found in prompt");
        Assert.True(indexSecond >= 0, "SECOND not found in prompt");
        Assert.True(indexFirst < indexSecond, "FIRST must appear before SECOND");
    }

    // ── 6.2 — escape-hatch override ─────────────────────────────────────────

    [Fact]
    public async Task Build_EscapeHatch_CustomBuilderOverridesDefault()
    {
        // The TryAddSingleton pattern means a builder-registered ISystemPromptBuilder wins.
        // Verify the contract: when a custom ISystemPromptBuilder is provided it is used instead.
        FixedSystemPromptBuilder custom = new("CUSTOM_BASE");

        ChatMessage msg = await custom.BuildAsync(CancellationToken.None);

        Assert.Equal(ChatRole.System, msg.Role);
        Assert.Equal("CUSTOM_BASE", msg.Text);
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
        public string? GetCurrentModelId() => null;
        public ProviderSwitchResult? CommitPendingSwitch() => null;
        public bool CurrentSupportsToolCalling => false;
        public bool CurrentSupportsReasoning => false;
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public IReadOnlyList<AIFunction> GetAll() => [];
        public void Register(string extensionName, IToolExtension extension, IEnumerable<AIFunction> tools) { }
        public IToolExtension? FindExtension(string toolName) => null;
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

    private sealed class StubSessionHandler : ISessionHandler
    {
        public SessionMeta? CurrentSession => null;

        public Task CreateAsync(SessionCreateCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloneAsync(SessionCloneCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LoadAsync(SessionLoadCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ListAsync(SessionListCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetNameAsync(SessionSetNameCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetStatsAsync(SessionGetStatsCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetMessagesAsync(SessionGetMessagesCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedSystemPromptBuilder(string text) : ISystemPromptBuilder
    {
        public Task<ChatMessage> BuildAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ChatMessage(ChatRole.System, text));
    }

    // Staged for Group 7 asset-directory tests (asset surfacing via session path).
    private sealed class SessionWithIdHandler : ISessionHandler
    {
        private readonly string _sessionId;

        public SessionWithIdHandler(string sessionId) => _sessionId = sessionId;

        public SessionMeta? CurrentSession => new() { Id = _sessionId };

        public Task CreateAsync(SessionCreateCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloneAsync(SessionCloneCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LoadAsync(SessionLoadCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ListAsync(SessionListCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetNameAsync(SessionSetNameCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetStatsAsync(SessionGetStatsCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetMessagesAsync(SessionGetMessagesCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
