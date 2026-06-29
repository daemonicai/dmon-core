using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;

namespace Dmon.Providers.Mlx.Tests;

// ---------------------------------------------------------------------------
// Shared fakes
// ---------------------------------------------------------------------------

/// <summary>
/// Fake IChatClient whose GetResponseAsync either includes or omits a FunctionCallContent,
/// allowing us to test the tool-calling probe gating.
/// </summary>
file sealed class ToolCallFakeChatClient : IChatClient
{
    private readonly bool _hasToolCall;

    public ToolCallFakeChatClient(bool hasToolCall) => _hasToolCall = hasToolCall;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<AIContent> contents = _hasToolCall
            ? [new FunctionCallContent("call-1", "get_test_value", new Dictionary<string, object?>())]
            : [new TextContent("I cannot call tools.")];

        ChatMessage msg = new(ChatRole.Assistant, contents);
        return Task.FromResult(new ChatResponse([msg]));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Fake IChatClient that records every GetResponseAsync call's ChatOptions.
/// Used to verify readiness uses a completion (with no tools, small MaxOutputTokens).
/// </summary>
file sealed class CapturingChatClient : IChatClient
{
    private readonly List<ChatOptions?> _captured;
    private readonly bool _succeed;

    public CapturingChatClient(List<ChatOptions?> captured, bool succeed = true)
    {
        _captured = captured;
        _succeed = succeed;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _captured.Add(options);
        if (!_succeed)
            throw new HttpRequestException("Simulated server unavailable.");

        ChatMessage msg = new(ChatRole.Assistant, [new TextContent("ok")]);
        return Task.FromResult(new ChatResponse([msg]));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

file sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(_respond(request));
}

file sealed class ThrowingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("Connection refused");
}

// ---------------------------------------------------------------------------
// 3.2 — IsApplicable (injected OS/arch/uv-resolve seams)
// ---------------------------------------------------------------------------

public sealed class IsApplicableTests
{
    [Fact]
    public void IsApplicable_NotMacOs_ReturnsFalse_AndFiresWarning()
    {
        List<string> warnings = [];
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => false,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveUvPathOverride: () => "/usr/local/bin/uv",
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.False(result);
        Assert.Single(warnings);
        Assert.Contains("not running macOS", warnings[0]);
    }

    [Fact]
    public void IsApplicable_MacOsButNotArm64_ReturnsFalse_AndFiresDistinctWarning()
    {
        List<string> warnings = [];
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.X64,
            resolveUvPathOverride: () => "/usr/local/bin/uv",
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.False(result);
        Assert.Single(warnings);
        Assert.Contains("arm64", warnings[0]);
        Assert.Contains("non-arm64", warnings[0]);
    }

    [Fact]
    public void IsApplicable_MacOsArm64_UvUnresolved_ReturnsFalse_AndFiresUvRemediation()
    {
        List<string> warnings = [];
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveUvPathOverride: () => null,
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.False(result);
        Assert.Single(warnings);
        // Remediation must name "uv" — the missing prerequisite.
        Assert.Contains("uv", warnings[0]);
    }

    [Fact]
    public void IsApplicable_MacOsArm64_UvResolvable_ReturnsTrue_WithNoWarning()
    {
        List<string> warnings = [];
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveUvPathOverride: () => "/usr/local/bin/uv",
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.True(result);
        Assert.Empty(warnings);
    }

    [Fact]
    public void IsApplicable_InvokesResolveUvPathOverride_NotSystemPath()
    {
        // The override must be consulted — this proves no real PATH I/O occurs.
        bool overrideCalled = false;
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveUvPathOverride: () => { overrideCalled = true; return "/injected/uv"; },
            onWarning: _ => { });

        sut.IsApplicable();

        Assert.True(overrideCalled);
    }
}

// ---------------------------------------------------------------------------
// 3.1 — MlxRuntimeOptions defaults and nvfp4-firstline guard
// ---------------------------------------------------------------------------

public sealed class MlxRuntimeOptionsTests
{
    [Fact]
    public void Firstline_DefaultModelId_IsE4BOptiQ()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        Assert.Equal("mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit", opts.ModelId);
    }

    [Fact]
    public void Firstline_DefaultPort_Is8800()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        Assert.Equal(8800, opts.Port);
    }

    [Fact]
    public void Escalation_DefaultModelId_Is26BNvfp4()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Escalation();

        Assert.Equal("mlx-community/gemma-4-26B-A4B-it-qat-nvfp4", opts.ModelId);
    }

    [Fact]
    public void Escalation_DefaultPort_Is8810()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Escalation();

        Assert.Equal(8810, opts.Port);
    }

    [Fact]
    public void Firstline_And_Escalation_DefaultPorts_AreDistinct()
    {
        MlxRuntimeOptions firstline = MlxRuntimeOptions.Firstline();
        MlxRuntimeOptions escalation = MlxRuntimeOptions.Escalation();

        Assert.NotEqual(firstline.Port, escalation.Port);
    }

    [Fact]
    public void Firstline_Nvfp4ModelId_ThrowsArgumentException()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => MlxRuntimeOptions.Firstline("mlx-community/gemma-4-e4b-it-qat-nvfp4"));

        Assert.Contains("nvfp4", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Firstline_Nvfp4ModelId_CaseInsensitive_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => MlxRuntimeOptions.Firstline("some-model-NVFP4-variant"));
    }

    [Fact]
    public void Firstline_NonNvfp4OverrideModelId_IsHonoured()
    {
        const string overrideModel = "mlx-community/gemma-4-e4b-it-qat-bf16";
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline(overrideModel);

        Assert.Equal(overrideModel, opts.ModelId);
    }

    [Fact]
    public void Escalation_OverrideModelId_IsHonoured()
    {
        const string overrideModel = "mlx-community/gemma-4-26B-A4B-it-qat-bf16";
        MlxRuntimeOptions opts = MlxRuntimeOptions.Escalation(overrideModel);

        Assert.Equal(overrideModel, opts.ModelId);
    }

    [Fact]
    public void Firstline_OverridePort_IsHonoured()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline(port: 9900);

        Assert.Equal(9900, opts.Port);
    }

    [Fact]
    public void Escalation_OverridePort_IsHonoured()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Escalation(port: 9901);

        Assert.Equal(9901, opts.Port);
    }

    [Fact]
    public void DefaultHost_Is_Loopback()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        Assert.Equal("127.0.0.1", opts.Host);
    }
}

// ---------------------------------------------------------------------------
// 3.3 — Version pin enforcement (uv-managed env delegate)
// ---------------------------------------------------------------------------

public sealed class VersionPinTests
{
    [Fact]
    public async Task VersionBelowPin_ThrowsInvalidOperation_BeforeAnySpawn()
    {
        bool spawnCalled = false;
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false),
            provisionEnvDelegate: _ => Task.FromResult("0.30.0"),
            startServerDelegate: (_, _) =>
            {
                spawnCalled = true;
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.EnsureRunningAsync());

        Assert.False(spawnCalled, "Server must NOT be spawned when the version pin is not satisfied.");
    }

    [Fact]
    public async Task VersionAtPin_Succeeds_SpawnProceeds()
    {
        int spawnCallCount = 0;
        int probeCall = 0;
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(++probeCall > 1),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: (_, _) =>
            {
                spawnCallCount++;
                return Task.CompletedTask;
            },
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.Equal(1, spawnCallCount);
    }

    [Fact]
    public async Task VersionAbovePin_Succeeds_AttachProceeds()
    {
        bool spawnCalled = false;
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        // Server already running → attach path (spawn must not be called).
        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            provisionEnvDelegate: _ => Task.FromResult("0.31.4"),
            startServerDelegate: (_, _) =>
            {
                spawnCalled = true;
                return Task.CompletedTask;
            },
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.False(spawnCalled);
        Assert.False(state.OwnsProcess);
    }

    [Fact]
    public async Task ErrorMessage_NamesPinVersion_And_RemeditionCommand()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            new MlxRuntimeState(),
            isRunningProbe: _ => Task.FromResult(false),
            provisionEnvDelegate: _ => Task.FromResult("0.29.5"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.EnsureRunningAsync());

        Assert.Contains("0.29.5", ex.Message);
        Assert.Contains("0.31.3", ex.Message);
        Assert.Contains("uv pip install", ex.Message);
    }
}

// ---------------------------------------------------------------------------
// 3.4 — EnsureRunningAsync / attach-first lifecycle
// ---------------------------------------------------------------------------

public sealed class EnsureRunningAsyncTests
{
    [Fact]
    public async Task AttachPath_ServerAlreadyRunning_DoesNotSpawnProcess_OwnsProcessFalse()
    {
        bool spawnCalled = false;
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: (_, _) =>
            {
                spawnCalled = true;
                return Task.CompletedTask;
            },
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.False(spawnCalled, "startServerDelegate must NOT be invoked on the attach path.");
        Assert.False(state.OwnsProcess);
        Assert.NotEmpty(state.BaseUrl);
        Assert.StartsWith("http://", state.BaseUrl);
    }

    [Fact]
    public async Task AttachPath_SetsToolCallingVerified_ViaProbe()
    {
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.True(state.ToolCallingVerified);
    }

    [Fact]
    public async Task ColdStart_ServerNotRunning_StartsProcess_OwnsProcessTrue()
    {
        int probeCall = 0;
        int spawnCallCount = 0;
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline() with
        {
            ReadyTimeout = TimeSpan.FromSeconds(5),
        };

        // Probe: false on first call (cold start check), true on second (readiness poll after spawn).
        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(++probeCall > 1),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: (_, _) =>
            {
                spawnCallCount++;
                return Task.CompletedTask;
            },
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.Equal(1, spawnCallCount);
        Assert.True(state.OwnsProcess);
        Assert.NotEmpty(state.BaseUrl);
        Assert.StartsWith("http://", state.BaseUrl);
    }

    [Fact]
    public async Task ColdStart_SetsToolCallingVerified_ViaProbe()
    {
        int probeCall = 0;
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline() with
        {
            ReadyTimeout = TimeSpan.FromSeconds(5),
        };

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(++probeCall > 1),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: (_, _) => Task.CompletedTask,
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.True(state.ToolCallingVerified);
    }

    [Fact]
    public async Task ReadinessTimeout_ThrowsTimeoutException()
    {
        // Probe always false → timeout fires.
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline() with
        {
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
        };

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<TimeoutException>(() => sut.EnsureRunningAsync());
    }

    [Fact]
    public async Task BaseUrl_ContainsHostAndPort()
    {
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline(port: 8888);

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.Contains("8888", state.BaseUrl);
        Assert.Contains("127.0.0.1", state.BaseUrl);
    }
}

// ---------------------------------------------------------------------------
// 3.6 — Readiness via completion (never /v1/models)
// ---------------------------------------------------------------------------

public sealed class ReadinessCompletionTests
{
    [Fact]
    public async Task IsRunningAsync_CallsCompletion_NotModelsEndpoint()
    {
        // With _probeClientFactory but no _isRunningProbe, IsRunningAsync goes through
        // CheckRunningViaCompletionAsync which invokes the factory (IChatClient) —
        // NOT an HttpClient.GetAsync to /v1/models.
        List<ChatOptions?> captured = [];
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        using MlxProviderExtension sut = new(
            opts,
            state,
            probeClientFactory: (_, _) => new CapturingChatClient(captured));

        bool result = await sut.IsRunningAsync();

        // Exactly one call made (for readiness).
        Assert.Single(captured);
        // The call had no tools — it is a completion, not a tool-calling probe.
        Assert.Null(captured[0]?.Tools);
        // Small token budget consistent with a liveness check, not content generation.
        Assert.NotNull(captured[0]?.MaxOutputTokens);
        Assert.True(captured[0]!.MaxOutputTokens <= 16,
            $"Expected MaxOutputTokens ≤ 16 for readiness, got {captured[0]!.MaxOutputTokens}.");
        Assert.True(result);
    }

    [Fact]
    public async Task IsRunningAsync_ReturnsTrue_WhenCompletionSucceeds()
    {
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        using MlxProviderExtension sut = new(
            opts,
            state,
            probeClientFactory: (_, _) => new CapturingChatClient([], succeed: true));

        bool result = await sut.IsRunningAsync();

        Assert.True(result);
    }

    [Fact]
    public async Task IsRunningAsync_ReturnsFalse_WhenCompletionFails()
    {
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        using MlxProviderExtension sut = new(
            opts,
            state,
            probeClientFactory: (_, _) => new CapturingChatClient([], succeed: false));

        bool result = await sut.IsRunningAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task IsRunningAsync_ReturnsFalse_WhenBaseUrlEmpty()
    {
        // Empty BaseUrl (not yet seeded) → no network attempt; return false immediately.
        MlxRuntimeState state = new() { BaseUrl = string.Empty };
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        // No probeClientFactory — production path; but BaseUrl is empty so it returns false first.
        // Inject a recording client to confirm the factory was NOT called (no network attempt).
        List<ChatOptions?> captured = [];
        using MlxProviderExtension sut = new(
            opts,
            state,
            probeClientFactory: (_, _) => new CapturingChatClient(captured));

        bool result = await sut.IsRunningAsync();

        Assert.False(result);
        Assert.Empty(captured); // Factory must not be invoked when BaseUrl is unset.
    }
}

// ---------------------------------------------------------------------------
// 3.7 — Tool-calling probe (generous MaxOutputTokens for gemma-4)
// ---------------------------------------------------------------------------

public sealed class ToolCallingProbeTests
{
    [Fact]
    public async Task Probe_SetsToolCallingVerified_True_WhenFunctionCallReturned()
    {
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.True(state.ToolCallingVerified);
    }

    [Fact]
    public async Task Probe_SetsToolCallingVerified_False_WhenNoFunctionCallReturned()
    {
        List<string> warnings = [];
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: false),
            onWarning: w => warnings.Add(w));

        await sut.EnsureRunningAsync();

        Assert.False(state.ToolCallingVerified);
        Assert.Single(warnings);
        Assert.Contains("tool", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_UsesGenerousMaxOutputTokens_ForGemma4()
    {
        // The tool-calling probe must use a generous MaxOutputTokens so gemma-4's reasoning
        // field doesn't consume the whole budget before the tool call is emitted.
        List<ChatOptions?> captured = [];
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        // Fake returns a FunctionCallContent so the probe doesn't fire a warning.
        IChatClient factory(string _, string __)
        {
            // Use CapturingChatClient that also returns a tool call.
            return new ProbeCaptureClient(captured);
        }

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            probeClientFactory: factory);

        await sut.EnsureRunningAsync();

        // Probe call is the one with tools.
        ChatOptions? probeOpts = captured.FirstOrDefault(o => o?.Tools is { Count: > 0 });
        Assert.NotNull(probeOpts);
        Assert.True(probeOpts!.MaxOutputTokens >= 2048,
            $"Probe MaxOutputTokens must be ≥ 2048 for gemma-4, got {probeOpts.MaxOutputTokens}.");
    }
}

// ---------------------------------------------------------------------------
// 3.4 — SpawnServer argument list (no real process)
// ---------------------------------------------------------------------------

public sealed class BuildServerArgumentsTests
{
    [Fact]
    public void BuildServerArguments_ContainsMlxLmServer_Module()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(8800);

        Assert.Contains("-m", args);
        Assert.Contains("mlx_lm.server", args);
    }

    [Fact]
    public void BuildServerArguments_ContainsModelId()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(8800);

        int modelIdx = args.ToList().IndexOf("--model");
        Assert.True(modelIdx >= 0, "--model flag must be present.");
        Assert.Equal(opts.ModelId, args[modelIdx + 1]);
    }

    [Fact]
    public void BuildServerArguments_ContainsPort()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline(port: 8812);
        using MlxProviderExtension sut = new(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(8812);

        int portIdx = args.ToList().IndexOf("--port");
        Assert.True(portIdx >= 0, "--port flag must be present.");
        Assert.Equal("8812", args[portIdx + 1]);
    }

    [Fact]
    public void BuildServerArguments_ContainsHost()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(8800);

        int hostIdx = args.ToList().IndexOf("--host");
        Assert.True(hostIdx >= 0, "--host flag must be present.");
        Assert.Equal("127.0.0.1", args[hostIdx + 1]);
    }
}

// ---------------------------------------------------------------------------
// Probe-capture helper — records options AND returns a FunctionCallContent
// ---------------------------------------------------------------------------

file sealed class ProbeCaptureClient : IChatClient
{
    private readonly List<ChatOptions?> _captured;

    public ProbeCaptureClient(List<ChatOptions?> captured) => _captured = captured;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _captured.Add(options);

        // Return a FunctionCallContent so the probe considers tool calling verified.
        List<AIContent> contents = options?.Tools is { Count: > 0 }
            ? [new FunctionCallContent("call-1", "get_test_value", new Dictionary<string, object?>())]
            : [new TextContent("ok")];

        ChatMessage msg = new(ChatRole.Assistant, contents);
        return Task.FromResult(new ChatResponse([msg]));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

// ---------------------------------------------------------------------------
// 3.8 (partial) — StopAsync process-ownership invariant (SetServerProcess seam)
// ---------------------------------------------------------------------------

public sealed class StopAsyncTests
{
    [SkippableFact]
    public async Task StopAsync_KillsOwnedProcess_WhenOwnsProcessTrue()
    {
        bool isUnix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                   || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        Skip.IfNot(isUnix || isWindows, "Unsupported platform for this test.");

        ProcessStartInfo dummyPsi = isUnix
            ? new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false }
            : new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true };

        Process dummy = new() { StartInfo = dummyPsi };
        dummy.Start();
        int pid = dummy.Id;
        Assert.False(dummy.HasExited, "Dummy process should be running before StopAsync.");

        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        MlxRuntimeState state = new() { OwnsProcess = true };

        MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"));

        sut.SetServerProcess(dummy);
        await sut.StopAsync();
        sut.Dispose();

        await Task.Delay(300);
        bool stillRunning = false;
        try
        {
            using Process? found = Process.GetProcessById(pid);
            stillRunning = !found.HasExited;
        }
        catch (ArgumentException)
        {
            // Process not found by ID — it has exited. This is the success case.
        }

        Assert.False(stillRunning, "StopAsync() must kill an owned server process.");
    }

    [SkippableFact]
    public async Task StopAsync_DoesNotKillAttachedProcess_WhenOwnsProcessFalse()
    {
        bool isUnix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                   || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        Skip.IfNot(isUnix || isWindows, "Unsupported platform for this test.");

        ProcessStartInfo dummyPsi = isUnix
            ? new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false }
            : new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true };

        Process dummy = new() { StartInfo = dummyPsi };
        dummy.Start();
        int pid = dummy.Id;

        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        // OwnsProcess = false → attached server; StopAsync must NOT kill it.
        MlxRuntimeState state = new() { OwnsProcess = false };

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"));

        sut.SetServerProcess(dummy);
        await sut.StopAsync();

        await Task.Delay(300);

        bool stillRunning = false;
        try
        {
            using Process? found = Process.GetProcessById(pid);
            stillRunning = !found.HasExited;
        }
        catch (ArgumentException)
        {
            // Exited — unexpected for an attached process.
        }

        // Clean up the dummy process ourselves.
        try { dummy.Kill(entireProcessTree: true); } catch { /* best-effort */ }

        Assert.True(stillRunning, "StopAsync() must NOT kill an attached (not-owned) server process.");
    }

    [SkippableFact]
    public async Task StopAsync_IsIdempotent()
    {
        bool isUnix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                   || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        Skip.IfNot(isUnix || isWindows, "Unsupported platform for this test.");

        ProcessStartInfo dummyPsi = isUnix
            ? new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false }
            : new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true };

        Process dummy = new() { StartInfo = dummyPsi };
        dummy.Start();

        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        MlxRuntimeState state = new() { OwnsProcess = true };

        using MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"));

        sut.SetServerProcess(dummy);

        // First call kills the process.
        await sut.StopAsync();

        // Second call must not throw — KillServer is idempotent (Interlocked.Exchange guards).
        Exception? ex = await Record.ExceptionAsync(() => sut.StopAsync());
        Assert.Null(ex);
    }
}

// ---------------------------------------------------------------------------
// 3.8 (partial) — Dispose process-ownership invariant (SetServerProcess seam)
// ---------------------------------------------------------------------------

public sealed class DisposeTests
{
    [SkippableFact]
    public async Task Dispose_KillsTrackedProcess_WhenOwnsProcessTrue()
    {
        bool isUnix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                   || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        Skip.IfNot(isUnix || isWindows, "Unsupported platform for this test.");

        ProcessStartInfo dummyPsi = isUnix
            ? new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false }
            : new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true };

        Process dummy = new() { StartInfo = dummyPsi };
        dummy.Start();
        int pid = dummy.Id;
        Assert.False(dummy.HasExited, "Dummy process should be running before Dispose.");

        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        MlxRuntimeState state = new() { OwnsProcess = true };

        using (MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3")))
        {
            sut.SetServerProcess(dummy);
        } // Dispose() called here — should kill the dummy process.

        await Task.Delay(300);
        bool stillRunning = false;
        try
        {
            using Process? found = Process.GetProcessById(pid);
            stillRunning = !found.HasExited;
        }
        catch (ArgumentException)
        {
            // Process not found by ID — it has exited. This is the success case.
        }

        Assert.False(stillRunning, "Dispose() must kill the server process — no orphan.");
    }

    [SkippableFact]
    public async Task Dispose_DoesNotKillAttachedProcess_WhenOwnsProcessFalse()
    {
        bool isUnix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                   || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        Skip.IfNot(isUnix || isWindows, "Unsupported platform for this test.");

        ProcessStartInfo dummyPsi = isUnix
            ? new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false }
            : new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true };

        Process dummy = new() { StartInfo = dummyPsi };
        dummy.Start();
        int pid = dummy.Id;

        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        // OwnsProcess = false → attached server; Dispose must NOT kill it.
        MlxRuntimeState state = new() { OwnsProcess = false };

        using (MlxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3")))
        {
            sut.SetServerProcess(dummy);
        } // Dispose() called here — must NOT kill the process.

        await Task.Delay(300);

        bool stillRunning = false;
        try
        {
            using Process? found = Process.GetProcessById(pid);
            stillRunning = !found.HasExited;
        }
        catch (ArgumentException)
        {
            // Exited — unexpected for an attached process.
        }

        // Clean up the dummy process ourselves.
        try { dummy.Kill(entireProcessTree: true); } catch { /* best-effort */ }

        Assert.True(stillRunning, "Dispose() must NOT kill an attached (not-owned) server process.");
    }
}
