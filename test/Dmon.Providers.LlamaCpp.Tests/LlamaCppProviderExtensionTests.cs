using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Dmon.Abstractions.Providers;

namespace Dmon.Providers.LlamaCpp.Tests;

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

file static class Helpers
{
    public static HttpClient MakeClient(HttpMessageHandler handler) => new(handler);

    public static LlamaCppOptions DefaultOptions(string modelId = "repo/model") =>
        new() { ModelId = modelId };

    public static LlamaCppOptions OptionsWithExistingPath() =>
        new()
        {
            ModelId = "repo/model",
            // Points at an actually-existing file on any platform.
            ServerPath = Environment.ProcessPath ?? typeof(IsApplicableTests).Assembly.Location,
        };

    // IReadOnlyList<T> does not expose IndexOf; this avoids repeated casts across arg-assembly tests.
    public static int ArgIndex(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count; i++)
            if (args[i] == flag)
                return i;
        return -1;
    }
}

// ---------------------------------------------------------------------------
// 6.1 — IsApplicable
// ---------------------------------------------------------------------------

public sealed class IsApplicableTests
{
    [Fact]
    public void IsApplicable_ReturnsTrue_WhenServerPathExistsOnDisk()
    {
        LlamaCppOptions opts = Helpers.OptionsWithExistingPath();
        using LlamaCppProviderExtension sut = new(opts);

        Assert.True(sut.IsApplicable());
    }

    [Fact]
    public void IsApplicable_ReturnsFalse_WhenPathUnresolvable_AndInvokesWarning()
    {
        // Non-existent path; PATH is unlikely to have llama-server in CI.
        LlamaCppOptions opts = new()
        {
            ModelId = "repo/model",
            ServerPath = "/nonexistent/path/llama-server-does-not-exist",
        };

        List<string> warnings = [];
        // Clear PATH so FindOnPath also fails.
        string? savedPath = Environment.GetEnvironmentVariable("PATH");
        string? savedLlamaPath = Environment.GetEnvironmentVariable("LLAMA_SERVER_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", null);
            Environment.SetEnvironmentVariable("LLAMA_SERVER_PATH", null);

            using LlamaCppProviderExtension sut = new(opts, onWarning: w => warnings.Add(w));

            bool result = sut.IsApplicable();

            Assert.False(result);
            Assert.Single(warnings);
            Assert.Contains("llama-server not found", warnings[0]);
            Assert.Contains("LLAMA_SERVER_PATH", warnings[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable("LLAMA_SERVER_PATH", savedLlamaPath);
        }
    }
}

// ---------------------------------------------------------------------------
// 6.2 — EnsureRunningAsync (injected seams, no real llama-server)
// ---------------------------------------------------------------------------

public sealed class EnsureRunningAsyncTests
{
    [Fact]
    public async Task ColdStart_Ready_SetsPortAndBaseUrl()
    {
        int callCount = 0;
        // Probe returns false first (cold), then true (ready).
        Task<bool> Probe(CancellationToken _)
        {
            callCount++;
            return Task.FromResult(callCount > 1);
        }

        LlamaCppRuntimeState state = new();
        LlamaCppOptions opts = new()
        {
            ModelId = "repo/model",
            Port = 11434,
            ReadyTimeout = TimeSpan.FromSeconds(5),
        };
        // Probe-client factory returns a client whose response has a FunctionCallContent
        // so the tool-calling probe is satisfied.
        using LlamaCppProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: Probe,
            startServerDelegate: (_, _, _) => Task.CompletedTask,
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.NotEqual(0, state.Port);
        Assert.NotEmpty(state.BaseUrl);
        Assert.StartsWith("http://", state.BaseUrl);
    }

    [Fact]
    public async Task ReadinessTimeout_ThrowsTimeoutException()
    {
        // Probe always returns false → PollUntilReadyAsync will time out.
        LlamaCppRuntimeState state = new();
        LlamaCppOptions opts = new()
        {
            ModelId = "repo/model",
            // Extremely short timeout so the test doesn't hang.
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
        };
        using LlamaCppProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false),
            startServerDelegate: (_, _, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<TimeoutException>(() => sut.EnsureRunningAsync());
    }

    [SkippableFact]
    public async Task Dispose_KillsTrackedProcess_NoOrphan()
    {
        // Uses a real long-lived dummy process (sleep on Unix, ping on Windows) so
        // we can verify Dispose() actually kills it.
        bool isUnix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                   || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        Skip.IfNot(isUnix || isWindows, "Unsupported platform for this test.");

        // Use absolute paths so the test is not sensitive to the process PATH.
        ProcessStartInfo dummyPsi = isUnix
            ? new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false }
            : new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true };

        Process dummy = new() { StartInfo = dummyPsi };
        dummy.Start();
        int pid = dummy.Id;
        Assert.False(dummy.HasExited, "Dummy process should be running before Dispose.");

        LlamaCppOptions opts = new() { ModelId = "repo/model" };
        LlamaCppRuntimeState state = new();
        using (LlamaCppProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false)))
        {
            // Transfer ownership: KillServer() will dispose dummy after killing it.
            sut.SetServerProcess(dummy);
        } // Dispose() called here — should kill the dummy process.

        // Allow a moment for the OS to update the exit status, then verify via PID.
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
}

// ---------------------------------------------------------------------------
// 6.3 — Free-port selection
// ---------------------------------------------------------------------------

public sealed class SelectPortTests
{
    [Fact]
    public void ExplicitPort_IsHonouredVerbatim()
    {
        LlamaCppOptions opts = new()
        {
            ModelId = "repo/model",
            Port = 12345,
        };
        // BuildServerArguments exposes the port choice without spawning.
        LlamaCppRuntimeState state = new();
        using LlamaCppProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false));

        IReadOnlyList<string> args = sut.BuildServerArguments(12345, "127.0.0.1");

        int portArgIdx = Helpers.ArgIndex(args, "--port");
        Assert.True(portArgIdx >= 0, "--port flag must appear in arguments");
        Assert.Equal("12345", args[portArgIdx + 1]);
    }

    [Fact]
    public async Task FreePort_IsBindable_WhenNoExplicitPort()
    {
        // Verify that EnsureRunningAsync picks a bindable port when no Port is set.
        int capturedPort = 0;
        LlamaCppRuntimeState state = new();
        LlamaCppOptions opts = new()
        {
            ModelId = "repo/model",
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
        };

        // Probe returns false first call (cold) and again (timeout path is fine here;
        // we just want to capture the port before it times out).
        int probeCall = 0;
        Task<bool> Probe(CancellationToken _)
        {
            capturedPort = state.Port;
            probeCall++;
            // Return true on 2nd call so EnsureRunningAsync proceeds past the first check.
            return Task.FromResult(probeCall > 1);
        }

        using LlamaCppProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: Probe,
            startServerDelegate: (_, _, _) => Task.CompletedTask,
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.NotEqual(0, state.Port);
        Assert.InRange(state.Port, 1, 65535);
    }
}

// ---------------------------------------------------------------------------
// 6.4 — Arg assembly (BuildServerArguments)
// ---------------------------------------------------------------------------

public sealed class BuildServerArgumentsTests
{
    private static LlamaCppProviderExtension MakeSut(LlamaCppOptions opts)
    {
        LlamaCppRuntimeState state = new();
        return new LlamaCppProviderExtension(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false));
    }

    [Fact]
    public void RepoWithExplicitQuant_ProducesHfArgWithColonNotation()
    {
        LlamaCppOptions opts = new() { ModelId = "myorg/myrepo:Q4_K_M" };
        using LlamaCppProviderExtension sut = MakeSut(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(8080, "127.0.0.1");

        int hfIdx = Helpers.ArgIndex(args, "-hf");
        Assert.True(hfIdx >= 0, "-hf flag must appear");
        Assert.Equal("myorg/myrepo:Q4_K_M", args[hfIdx + 1]);
    }

    [Fact]
    public void BareRepo_AppendsDefaultQuant()
    {
        LlamaCppOptions opts = new() { ModelId = "myorg/myrepo", Quant = "Q4_K_M" };
        using LlamaCppProviderExtension sut = MakeSut(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(8080, "127.0.0.1");

        int hfIdx = Helpers.ArgIndex(args, "-hf");
        Assert.True(hfIdx >= 0, "-hf flag must appear");
        Assert.Equal("myorg/myrepo:Q4_K_M", args[hfIdx + 1]);
    }

    [Fact]
    public void JinjaFlag_AlwaysPresent()
    {
        LlamaCppOptions opts = new() { ModelId = "repo/model" };
        using LlamaCppProviderExtension sut = MakeSut(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(8080, "127.0.0.1");

        Assert.Contains("--jinja", args);
    }

    [Fact]
    public void PortAndHost_PresentInArgs()
    {
        LlamaCppOptions opts = new() { ModelId = "repo/model" };
        using LlamaCppProviderExtension sut = MakeSut(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(9999, "0.0.0.0");

        int portIdx = Helpers.ArgIndex(args, "--port");
        int hostIdx = Helpers.ArgIndex(args, "--host");
        Assert.True(portIdx >= 0);
        Assert.Equal("9999", args[portIdx + 1]);
        Assert.True(hostIdx >= 0);
        Assert.Equal("0.0.0.0", args[hostIdx + 1]);
    }

    [Fact]
    public void ContextSize_ProducesMinusCFlag()
    {
        LlamaCppOptions opts = new() { ModelId = "repo/model", ContextSize = 4096 };
        using LlamaCppProviderExtension sut = MakeSut(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(8080, "127.0.0.1");

        int idx = Helpers.ArgIndex(args, "-c");
        Assert.True(idx >= 0, "-c flag must appear when ContextSize is set");
        Assert.Equal("4096", args[idx + 1]);
    }

    [Fact]
    public void GpuLayers_ProducesNglFlag()
    {
        LlamaCppOptions opts = new() { ModelId = "repo/model", GpuLayers = 35 };
        using LlamaCppProviderExtension sut = MakeSut(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(8080, "127.0.0.1");

        int idx = Helpers.ArgIndex(args, "-ngl");
        Assert.True(idx >= 0, "-ngl flag must appear when GpuLayers is set");
        Assert.Equal("35", args[idx + 1]);
    }

    [Fact]
    public void NoContextOrGpu_MinusCAndNgl_Absent()
    {
        LlamaCppOptions opts = new() { ModelId = "repo/model" };
        using LlamaCppProviderExtension sut = MakeSut(opts);

        IReadOnlyList<string> args = sut.BuildServerArguments(8080, "127.0.0.1");

        Assert.DoesNotContain("-c", args);
        Assert.DoesNotContain("-ngl", args);
    }
}

// ---------------------------------------------------------------------------
// 6.5 — Probe gating (probeClientFactory seam)
// ---------------------------------------------------------------------------

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

public sealed class ProbeGatingTests
{
    [Fact]
    public async Task ProbeWithFunctionCall_SetsToolCallingVerifiedTrue()
    {
        LlamaCppRuntimeState state = new();
        LlamaCppOptions opts = new()
        {
            ModelId = "repo/model",
            Port = 11434,
            ReadyTimeout = TimeSpan.FromSeconds(5),
        };

        // Probe: first call = false (cold), second call = true (ready).
        int call = 0;
        using LlamaCppProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(++call > 1),
            startServerDelegate: (_, _, _) => Task.CompletedTask,
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: true));

        await sut.EnsureRunningAsync();

        Assert.True(state.ToolCallingVerified);
    }

    [Fact]
    public async Task ProbeWithNoFunctionCall_SetsToolCallingVerifiedFalse_AndFiresWarning()
    {
        LlamaCppRuntimeState state = new();
        LlamaCppOptions opts = new()
        {
            ModelId = "repo/model",
            Port = 11434,
            ReadyTimeout = TimeSpan.FromSeconds(5),
        };

        List<string> warnings = [];
        int call = 0;
        using LlamaCppProviderExtension sut = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(++call > 1),
            startServerDelegate: (_, _, _) => Task.CompletedTask,
            probeClientFactory: (_, _) => new ToolCallFakeChatClient(hasToolCall: false),
            onWarning: w => warnings.Add(w));

        await sut.EnsureRunningAsync();

        Assert.False(state.ToolCallingVerified);
        Assert.Single(warnings);
        Assert.Contains("tool call", warnings[0]);
    }
}

// ---------------------------------------------------------------------------
// 6.6 — CreateAsync via LlamaCppProviderFactory
// ---------------------------------------------------------------------------

public sealed class CreateAsyncTests
{
    private static LlamaCppProviderFactory MakeFactory(
        LlamaCppRuntimeState state,
        LlamaCppOptions? opts = null)
    {
        opts ??= new LlamaCppOptions { ModelId = "repo/model" };
        return new LlamaCppProviderFactory(opts, state);
    }

    private static ProviderConfig DefaultConfig(string baseUrl) => new()
    {
        Name = "llamacpp",
        Adapter = "llamacpp",
        BaseUrl = baseUrl,
        Auth = new ProviderAuthConfig { Type = "none" },
    };

    [Fact]
    public async Task CreateAsync_ReturnsCapabilitiesDecoratorWrappedClient()
    {
        LlamaCppRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:11434/v1",
            ToolCallingVerified = true,
        };

        LlamaCppProviderFactory factory = MakeFactory(state);
        IChatClient client = await factory.CreateAsync(
            DefaultConfig(state.BaseUrl), apiKey: null);

        // Must be CapabilitiesDecorator — GetService returns ChatClientCapabilities.
        object? caps = client.GetService(typeof(ChatClientCapabilities));
        Assert.NotNull(caps);
        Assert.IsType<ChatClientCapabilities>(caps);
    }

    [Fact]
    public async Task CreateAsync_Capabilities_ReflectToolCallingVerified_False()
    {
        LlamaCppRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:11434/v1",
            ToolCallingVerified = false,
        };

        LlamaCppProviderFactory factory = MakeFactory(state);
        IChatClient client = await factory.CreateAsync(
            DefaultConfig(state.BaseUrl), apiKey: null);

        ChatClientCapabilities caps = (ChatClientCapabilities)client.GetService(typeof(ChatClientCapabilities))!;
        Assert.False(caps.SupportsToolCalling);
    }

    [Fact]
    public async Task CreateAsync_Capabilities_ReflectToolCallingVerified_True()
    {
        LlamaCppRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:11434/v1",
            ToolCallingVerified = true,
        };

        LlamaCppProviderFactory factory = MakeFactory(state);
        IChatClient client = await factory.CreateAsync(
            DefaultConfig(state.BaseUrl), apiKey: null);

        ChatClientCapabilities caps = (ChatClientCapabilities)client.GetService(typeof(ChatClientCapabilities))!;
        Assert.True(caps.SupportsToolCalling);
    }

    [Fact]
    public async Task CreateAsync_Capabilities_ToolCalling_False_WhenUnprobed()
    {
        // ToolCallingVerified == null (unprobed) → SupportsToolCalling must be false.
        LlamaCppRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:11434/v1",
            ToolCallingVerified = null,
        };

        LlamaCppProviderFactory factory = MakeFactory(state);
        IChatClient client = await factory.CreateAsync(
            DefaultConfig(state.BaseUrl), apiKey: null);

        ChatClientCapabilities caps = (ChatClientCapabilities)client.GetService(typeof(ChatClientCapabilities))!;
        Assert.False(caps.SupportsToolCalling);
    }

    [Fact]
    public async Task CreateAsync_DoesNotThrow_WithValidBaseUrl()
    {
        LlamaCppRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:11434/v1",
        };

        LlamaCppProviderFactory factory = MakeFactory(state);

        // Assert.NoExceptionAsync not in xunit 2.x — use Record.ExceptionAsync.
        Exception? ex = await Record.ExceptionAsync(() =>
            factory.CreateAsync(DefaultConfig(state.BaseUrl), apiKey: null).AsTask());
        Assert.Null(ex);
    }
}
