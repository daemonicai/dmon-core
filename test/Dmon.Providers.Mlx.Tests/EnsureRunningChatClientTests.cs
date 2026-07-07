using Microsoft.Extensions.AI;

namespace Dmon.Providers.Mlx.Tests;

// ---------------------------------------------------------------------------
// 2.3 (mlx-escalation-resilience) — EnsureRunningChatClient self-heal wrapper
// ---------------------------------------------------------------------------

/// <summary>
/// Fake IChatClient that records "dispatch" into a shared call-order trace, allowing tests to
/// assert that a respawn happens strictly before the inner client is ever dispatched to.
/// </summary>
file sealed class TracingFakeChatClient : IChatClient
{
    private readonly List<string> _callOrder;

    public TracingFakeChatClient(List<string> callOrder) => _callOrder = callOrder;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _callOrder.Add("dispatch");
        ChatMessage msg = new(ChatRole.Assistant, [new TextContent("ok")]);
        return Task.FromResult(new ChatResponse([msg]));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _callOrder.Add("dispatch");
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Fake IChatClient whose GetResponseAsync includes a FunctionCallContent, satisfying
/// EnsureRunningAsync's tool-calling probe during the attach/cold-start path.
/// </summary>
file sealed class ProbeSucceedsChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatMessage msg = new(
            ChatRole.Assistant,
            [new FunctionCallContent("call-1", "get_test_value", new Dictionary<string, object?>())]);
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

public sealed class EnsureRunningChatClientTests
{
    private static readonly IEnumerable<ChatMessage> Messages = [new ChatMessage(ChatRole.User, "hi")];

    [Fact]
    public async Task GetResponseAsync_AfterIdleTeardown_RespawnsBeforeDispatch_NoDeadEndpointCall()
    {
        // Simulates the runtime having been torn down by idle teardown (StopAsync): the
        // liveness probe reports down until the respawn's startServerDelegate flips it live.
        bool serverUp = false;
        List<string> callOrder = [];
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline() with { ReadyTimeout = TimeSpan.FromSeconds(5) };

        using MlxProviderExtension ext = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(serverUp),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: (_, _) =>
            {
                callOrder.Add("spawn");
                serverUp = true;
                return Task.CompletedTask;
            },
            probeClientFactory: (_, _) => new ProbeSucceedsChatClient());

        EnsureRunningChatClient sut = new(new TracingFakeChatClient(callOrder), ext);

        await sut.GetResponseAsync(Messages);

        Assert.Equal(["spawn", "dispatch"], callOrder);
    }

    [Fact]
    public async Task GetResponseAsync_RuntimeAlreadyRunning_AttachesWithoutSpawning()
    {
        List<string> callOrder = [];
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        using MlxProviderExtension ext = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(true),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: (_, _) =>
            {
                callOrder.Add("spawn");
                return Task.CompletedTask;
            },
            probeClientFactory: (_, _) => new ProbeSucceedsChatClient());

        EnsureRunningChatClient sut = new(new TracingFakeChatClient(callOrder), ext);

        await sut.GetResponseAsync(Messages);

        Assert.DoesNotContain("spawn", callOrder);
        Assert.Equal(["dispatch"], callOrder);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_AfterIdleTeardown_RespawnsBeforeDispatch_NoDeadEndpointCall()
    {
        bool serverUp = false;
        List<string> callOrder = [];
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline() with { ReadyTimeout = TimeSpan.FromSeconds(5) };

        using MlxProviderExtension ext = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(serverUp),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: (_, _) =>
            {
                callOrder.Add("spawn");
                serverUp = true;
                return Task.CompletedTask;
            },
            probeClientFactory: (_, _) => new ProbeSucceedsChatClient());

        EnsureRunningChatClient sut = new(new TracingFakeChatClient(callOrder), ext);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in sut.GetStreamingResponseAsync(Messages))
            updates.Add(update);

        Assert.Equal(["spawn", "dispatch"], callOrder);
        Assert.NotEmpty(updates);
    }

    [Fact]
    public async Task GetResponseAsync_EnsureRunningAsyncFaults_PropagatesAndNeverDispatches()
    {
        List<string> callOrder = [];
        MlxRuntimeState state = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline() with { ReadyTimeout = TimeSpan.FromMilliseconds(50) };

        // isRunningProbe never reports live and there is no startServerDelegate to flip it —
        // PollUntilReadyAsync times out, so EnsureRunningAsync throws.
        using MlxProviderExtension ext = new(
            opts,
            state,
            isRunningProbe: _ => Task.FromResult(false),
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: (_, _) => Task.CompletedTask);

        EnsureRunningChatClient sut = new(new TracingFakeChatClient(callOrder), ext);

        await Assert.ThrowsAsync<TimeoutException>(() => sut.GetResponseAsync(Messages));

        Assert.DoesNotContain("dispatch", callOrder);
    }
}
