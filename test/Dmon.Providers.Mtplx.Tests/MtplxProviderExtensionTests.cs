using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Providers.Mtplx.Tests;

// ---------------------------------------------------------------------------
// Shared fakes
// ---------------------------------------------------------------------------

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

/// <summary>
/// Fake IChatClient whose GetResponseAsync either includes or omits a FunctionCallContent,
/// allowing us to test tool-calling probe gating.
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
        ChatResponse response = new([msg]);
        return Task.FromResult(response);
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
// 5.2 — IsApplicable (injected OS/arch/resolve seams)
// ---------------------------------------------------------------------------

public sealed class IsApplicableTests
{
    [Fact]
    public void IsApplicable_NotMacOs_ReturnsFalse_AndFiresWarning()
    {
        List<string> warnings = [];
        MtplxOptions opts = new();
        using MtplxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => false,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveServerPathOverride: () => "/usr/local/bin/mtplx",
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
        MtplxOptions opts = new();
        using MtplxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.X64,
            resolveServerPathOverride: () => "/usr/local/bin/mtplx",
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.False(result);
        Assert.Single(warnings);
        Assert.Contains("arm64", warnings[0]);
        Assert.Contains("non-arm64", warnings[0]);
    }

    [Fact]
    public void IsApplicable_MacOsArm64_BinaryUnresolved_ReturnsFalse_AndFiresInstallRemediation()
    {
        List<string> warnings = [];
        MtplxOptions opts = new();
        using MtplxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveServerPathOverride: () => null,
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.False(result);
        Assert.Single(warnings);
        Assert.Contains("brew install", warnings[0]);
        Assert.Contains("MTPLX_SERVER_PATH", warnings[0]);
    }

    [Fact]
    public void IsApplicable_MacOsArm64_BinaryResolvable_ReturnsTrue()
    {
        List<string> warnings = [];
        MtplxOptions opts = new();
        using MtplxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveServerPathOverride: () => "/usr/local/bin/mtplx",
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.True(result);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Sanity check: on non-macOS CI platforms the production (no-override) constructor's
    /// IsApplicable() returns false. On a macOS/arm64 dev host the test is skipped because
    /// the result depends on whether the binary is installed.
    /// </summary>
    [SkippableFact]
    public void IsApplicable_ProductionCtor_ReturnsFalse_OnNonMacOsPlatform()
    {
        Skip.If(OperatingSystem.IsMacOS(), "Skipped on macOS — binary presence is environment-dependent.");

        MtplxOptions opts = new();
        using MtplxProviderExtension sut = new(opts);

        bool result = sut.IsApplicable();

        Assert.False(result);
    }
}

// ---------------------------------------------------------------------------
// 5.3 — EnsureRunningAsync / attach-first lifecycle
// ---------------------------------------------------------------------------

public sealed class EnsureRunningAsyncTests
{
    [Fact]
    public async Task AttachPath_ServerAlreadyRunning_DoesNotSpawnProcess_OwnsProcessFalse()
    {
        // isRunningProbe returns true immediately → attach path.
        // ModelId set to short-circuit EnsureActiveModelSeededAsync — no real HTTP call.
        bool startDelegateCalled = false;
        MtplxRuntimeState state = new();
        MtplxOptions opts = new()
        {
            Port = 8000,
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ModelId = "Youssofal/Qwen3.5-9B",
        };

        using MtplxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            startServerDelegate: (_, _) =>
            {
                startDelegateCalled = true;
                return Task.CompletedTask;
            },
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.False(startDelegateCalled, "startServerDelegate must NOT be invoked on the attach path.");
        Assert.False(state.OwnsProcess);
        Assert.NotEmpty(state.BaseUrl);
        Assert.StartsWith("http://", state.BaseUrl);
    }

    [Fact]
    public async Task ColdStart_ServerNotRunning_StartsProcess_OwnsProcessTrue()
    {
        int probeCall = 0;
        // ModelId set to short-circuit EnsureActiveModelSeededAsync — no real HTTP call.
        int startCallCount = 0;
        MtplxRuntimeState state = new();
        MtplxOptions opts = new()
        {
            Port = 8001,
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ModelId = "Youssofal/Qwen3.5-9B",
        };

        // Probe: false on first call (cold), true on second (ready after start).
        using MtplxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(++probeCall > 1),
            startServerDelegate: (_, _) =>
            {
                startCallCount++;
                return Task.CompletedTask;
            },
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.Equal(1, startCallCount);
        Assert.True(state.OwnsProcess);
        Assert.NotEmpty(state.BaseUrl);
    }

    [Fact]
    public async Task ReadinessTimeout_ThrowsTimeoutException()
    {
        // Probe always false → timeout.
        MtplxRuntimeState state = new();
        MtplxOptions opts = new()
        {
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
        };

        using MtplxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false),
            startServerDelegate: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<TimeoutException>(() => sut.EnsureRunningAsync());
    }

    [SkippableFact]
    public async Task Dispose_KillsTrackedProcess_NoOrphan()
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

        MtplxOptions opts = new();
        MtplxRuntimeState state = new() { OwnsProcess = true };

        using (MtplxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false)))
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
    public async Task Dispose_DoesNotKillAttachedProcess()
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

        MtplxOptions opts = new();
        // OwnsProcess = false → attached server; Dispose must NOT kill it.
        MtplxRuntimeState state = new() { OwnsProcess = false };

        using (MtplxProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true)))
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

// ---------------------------------------------------------------------------
// 5.4 — ListModelsAsync (injected HttpClient + FakeHttpHandler)
// ---------------------------------------------------------------------------

public sealed class ListModelsAsyncTests
{
    private const string ModelsJson =
        """{"data":[{"id":"Youssofal/Qwen3.5-9B"},{"id":"other"}]}""";

    private static HttpClient MakeFakeClient(string? responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpClient(new FakeHttpHandler(_ =>
        {
            if (responseBody is null)
                return new HttpResponseMessage(statusCode);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            };
        }));
    }

    [Fact]
    public async Task ListModelsAsync_ReturnsModelIds_FromFakeResponse()
    {
        MtplxOptions opts = new();
        MtplxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8000/v1" };

        using HttpClient client = MakeFakeClient(ModelsJson);
        using MtplxProviderExtension sut = new(opts, state, client);

        IReadOnlyList<ModelInfo> models = await sut.ListModelsAsync();

        Assert.Equal(2, models.Count);
        Assert.Equal("Youssofal/Qwen3.5-9B", models[0].Id);
        Assert.Equal("other", models[1].Id);
    }

    [Fact]
    public async Task ListModelsAsync_SetsActiveModelId_ToFirstModel_WhenModelIdUnset()
    {
        MtplxOptions opts = new() { ModelId = null };
        MtplxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8000/v1" };

        using HttpClient client = MakeFakeClient(ModelsJson);
        using MtplxProviderExtension sut = new(opts, state, client);

        await sut.ListModelsAsync();

        Assert.Equal("Youssofal/Qwen3.5-9B", state.ActiveModelId);
    }

    [Fact]
    public async Task ListModelsAsync_DoesNotOverrideActiveModelId_WhenModelIdExplicitlySet()
    {
        MtplxOptions opts = new() { ModelId = "explicit-model" };
        MtplxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8000/v1" };

        using HttpClient client = MakeFakeClient(ModelsJson);
        using MtplxProviderExtension sut = new(opts, state, client);

        await sut.ListModelsAsync();

        // ModelId is set → ActiveModelId must not be touched.
        Assert.Null(state.ActiveModelId);
    }

    [Fact]
    public async Task ListModelsAsync_NonSuccessResponse_ReturnsEmpty()
    {
        MtplxOptions opts = new();
        MtplxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8000/v1" };

        using HttpClient client = MakeFakeClient(responseBody: null, statusCode: HttpStatusCode.InternalServerError);
        using MtplxProviderExtension sut = new(opts, state, client);

        IReadOnlyList<ModelInfo> models = await sut.ListModelsAsync();

        Assert.Empty(models);
    }

    [Fact]
    public async Task ListModelsAsync_MalformedJson_ReturnsEmpty()
    {
        MtplxOptions opts = new();
        MtplxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8000/v1" };

        using HttpClient client = MakeFakeClient("not-json-at-all{{{{");
        using MtplxProviderExtension sut = new(opts, state, client);

        IReadOnlyList<ModelInfo> models = await sut.ListModelsAsync();

        Assert.Empty(models);
    }

    [Fact]
    public async Task ListModelsAsync_NetworkError_ReturnsEmpty()
    {
        MtplxOptions opts = new();
        MtplxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8000/v1" };

        using HttpClient client = new(new ThrowingHttpHandler());
        using MtplxProviderExtension sut = new(opts, state, client);

        IReadOnlyList<ModelInfo> models = await sut.ListModelsAsync();

        Assert.Empty(models);
    }
}
