using System.Runtime.CompilerServices;
using Dmon.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Providers.Mlx.Tests;

// ---------------------------------------------------------------------------
// 3.4 — MlxClient(key) single-sources its self-heal from the factory.
//
// design.md D4: the keyed router-backend helper no longer calls EnsureRunningAsync
// up front nor wraps the factory output in its own EnsureRunningChatClient. It resolves
// the keyed extension, builds the ProviderConfig, and returns the factory client verbatim.
// The regression this guards: the pre-D4 helper produced a DOUBLE EnsureRunningChatClient
// wrapper and invoked EnsureRunningAsync TWICE at construction.
// ---------------------------------------------------------------------------

public sealed class MlxClientExtensionsTests
{
    private static MlxProviderExtension MakeExtension(
        MlxRuntimeOptions opts,
        MlxRuntimeState state,
        StrongBox<int> ensureRunningCalls) =>
        new(
            opts,
            state,
            isRunningProbe: _ =>
            {
                Interlocked.Increment(ref ensureRunningCalls.Value);
                return Task.FromResult(true);
            },
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: null,
            probeClientFactory: (_, _) => new ProbeFakeChatClient());

    [Fact]
    public async Task MlxClient_ReturnsFactorySelfHealingClient_SingleSourced()
    {
        StrongBox<int> ensureRunningCalls = new(0);
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        MlxProviderExtension ext = MakeExtension(opts, state, ensureRunningCalls);

        ServiceCollection services = new();
        services.AddKeyedSingleton<MlxProviderExtension>(MlxRuntimeKeys.Firstline, ext);
        ServiceProvider sp = services.BuildServiceProvider();

        IChatClient client = await sp.MlxClient(MlxRuntimeKeys.Firstline);

        // Single top-level wrapper — not the pre-D4 EnsureRunningChatClient(EnsureRunningChatClient(...)).
        Assert.IsType<EnsureRunningChatClient>(client);

        // Single source: EnsureRunningAsync ran exactly once at construction (the old helper
        // double-probed → this would be 2). This is the regression that guards D4.
        Assert.Equal(1, ensureRunningCalls.Value);
    }

    // Fake probe client for the factory's in-CreateAsync tool-calling probe: returns a
    // FunctionCallContent so the capability snapshot advertises tool calling. Wired via
    // probeClientFactory, so it never dials a port.
    private sealed class ProbeFakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<AIContent> contents =
                [new FunctionCallContent("call-1", "get_test_value", new Dictionary<string, object?>())];
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
