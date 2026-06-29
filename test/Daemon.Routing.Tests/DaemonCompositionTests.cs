using Daemon.Routing;
using Dmon.Abstractions.Hosting;
using Dmon.Hosting;
using Dmon.Providers.Mlx;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Daemon.Routing.Tests;

// ── Hermetic stub (file-scoped) ───────────────────────────────────────────────

/// <summary>
/// Minimal no-op <see cref="IChatClient"/> for DI-graph construction tests.
/// Never called — the test verifies constructability, not turn execution.
/// </summary>
file sealed class NoOpChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("noop", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DI-graph test stub — never invoked.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DI-graph test stub — never invoked.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Verifies that the daemon's MLX composition graph (AddMlxFirstline/AddMlxEscalation
/// + triage verbs + escalation warming) resolves without DI errors.
///
/// NO spawn guarantee: resolving keyed <see cref="MlxProviderExtension"/> only runs
/// <c>new MlxProviderExtension(opts)</c> — pure field assignment, zero process/network I/O.
/// All spawn is inside <c>EnsureRunningAsync</c>, which is NOT called here.
/// The triage and escalation factories are lazy <see cref="Func{T,TResult}"/>s NOT
/// invoked by <c>Build()</c> or by resolving the warming service.
/// </summary>
public sealed class DaemonCompositionTests
{
    [Fact]
    public void DaemonComposition_MlxGraph_ResolvesWithoutErrors()
    {
        // Arrange — mirror the daemon's backend wiring (hermetic: no Dcal/Dmail/memory,
        // stub egress so no Gemini key or network is needed).
        DmonHostBuilder builder = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();

        builder.AddMlxFirstline();
        builder.AddMlxEscalation();
        builder.UseTriage    (sp => sp.MlxClient(MlxRuntimeKeys.Firstline));
        builder.AddEscalation(sp => sp.MlxClient(MlxRuntimeKeys.Escalation));
        builder.AddEgress    (new NoOpChatClient());
        builder.AddEscalationWarming(
            sp => sp.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation));

        DmonBuiltHost host = builder.Build();

        // Act + Assert — explicit resolves are the only automated guard against launch-time
        // DI crashes (gateway-packaging lesson: DI-resolved types need public ctors).

        // Keyed MLX runtimes register as singletons.
        MlxProviderExtension firstline =
            host.Services.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Firstline);
        Assert.NotNull(firstline);

        MlxProviderExtension escalation =
            host.Services.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation);
        Assert.NotNull(escalation);

        // Keyed singletons return the same instance on repeated resolution.
        MlxProviderExtension escalationAgain =
            host.Services.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation);
        Assert.Same(escalation, escalationAgain);

        // UseTriage registers ITerminalClientFactory → TriageRouterFactory.
        ITerminalClientFactory terminalClientFactory =
            host.Services.GetRequiredService<ITerminalClientFactory>();
        Assert.NotNull(terminalClientFactory);

        // AddEscalationWarming registers EscalationWarmingService as a singleton.
        EscalationWarmingService warmingService =
            host.Services.GetRequiredService<EscalationWarmingService>();
        Assert.NotNull(warmingService);

        // EscalationWarmingService is also registered as ISessionActivityListener.
        IEnumerable<ISessionActivityListener> listeners =
            host.Services.GetServices<ISessionActivityListener>();
        Assert.Contains(listeners, l => l is EscalationWarmingService);
    }
}
