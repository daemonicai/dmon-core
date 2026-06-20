using Daemon.Routing;
using Dmon.Abstractions.Hosting;
using Dmon.Core.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

// Wrapper records disambiguate the three backend delegates in DI so
// TriageRouterFactory.Create can resolve the right one without keyed registration.
internal sealed record FirstLineRawClientFactory(Func<IServiceProvider, ValueTask<IChatClient>> Factory);
internal sealed record EscalationClientFactory(Func<IServiceProvider, ValueTask<IChatClient>> Factory);
internal sealed record EgressClientFactory(Func<IServiceProvider, ValueTask<IChatClient>> Factory);

/// <summary>
/// Composition verbs that wire the triage router into the dmon builder.
/// </summary>
public static class TriageRegistrationExtensions
{
    /// <summary>
    /// Registers the triage router factory and the first-line backend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delegate is used as both the classifier (no tools, structured output)
    /// and — after wrapping with <c>FunctionInvokingChatClient</c> — as the first-line
    /// backend. Both roles are resolved lazily on first use inside <see cref="TriageRouterFactory.Create"/>.
    /// </para>
    /// <para>
    /// Call <see cref="AddEscalation"/> and <see cref="AddEgress"/> on the same builder
    /// to supply the remaining backends. All three are resolved lazily when the router first uses them.
    /// </para>
    /// </remarks>
    /// <param name="b">The host builder.</param>
    /// <param name="firstLineFactory">
    /// A delegate that resolves the first-line (classifier + tool-handling) <see cref="IChatClient"/>.
    /// Invoked at most once per router instance (lazy, thread-safe).
    /// </param>
    /// <param name="options">Optional triage options; defaults to <see cref="TriageOptions"/> defaults.</param>
    /// <returns><paramref name="b"/>, for fluent chaining.</returns>
    public static IDmonHostBuilder UseTriage(
        this IDmonHostBuilder b,
        Func<IServiceProvider, ValueTask<IChatClient>> firstLineFactory,
        TriageOptions? options = null)
    {
        b.Services.AddSingleton(new FirstLineRawClientFactory(firstLineFactory));
        b.Services.AddSingleton(options ?? new TriageOptions());
        b.Services.AddSingleton<ITerminalClientFactory, TriageRouterFactory>();
        return b;
    }

    /// <summary>
    /// Registers the triage router factory and the first-line backend (eager overload).
    /// </summary>
    /// <param name="b">The host builder.</param>
    /// <param name="firstLineRaw">
    /// The pre-built first-line <see cref="IChatClient"/> (provider SDK client, no pipeline wrapping).
    /// Wrapped as a <c>ValueTask.FromResult</c> delegate internally.
    /// </param>
    /// <param name="options">Optional triage options; defaults to <see cref="TriageOptions"/> defaults.</param>
    /// <returns><paramref name="b"/>, for fluent chaining.</returns>
    public static IDmonHostBuilder UseTriage(
        this IDmonHostBuilder b,
        IChatClient firstLineRaw,
        TriageOptions? options = null)
        => b.UseTriage(_ => ValueTask.FromResult(firstLineRaw), options);

    /// <summary>
    /// Registers the escalation backend for the triage router.
    /// </summary>
    /// <param name="b">The host builder.</param>
    /// <param name="escalationFactory">
    /// A delegate that resolves the escalation <see cref="IChatClient"/>.
    /// Invoked at most once per router instance (lazy, thread-safe).
    /// </param>
    /// <returns><paramref name="b"/>, for fluent chaining.</returns>
    public static IDmonHostBuilder AddEscalation(
        this IDmonHostBuilder b,
        Func<IServiceProvider, ValueTask<IChatClient>> escalationFactory)
    {
        b.Services.AddSingleton(new EscalationClientFactory(escalationFactory));
        return b;
    }

    /// <summary>
    /// Registers the escalation backend for the triage router (eager overload).
    /// </summary>
    /// <param name="b">The host builder.</param>
    /// <param name="escalation">The pre-built escalation <see cref="IChatClient"/>.</param>
    /// <returns><paramref name="b"/>, for fluent chaining.</returns>
    public static IDmonHostBuilder AddEscalation(
        this IDmonHostBuilder b,
        IChatClient escalation)
        => b.AddEscalation(_ => ValueTask.FromResult(escalation));

    /// <summary>
    /// Registers the egress backend for the triage router.
    /// </summary>
    /// <remarks>
    /// Egress is provider-agnostic: pass any <see cref="IChatClient"/> — the router
    /// never references a provider package or performs a name-based lookup.
    /// </remarks>
    /// <param name="b">The host builder.</param>
    /// <param name="egressFactory">
    /// A delegate that resolves the egress <see cref="IChatClient"/>.
    /// Invoked at most once per router instance (lazy, thread-safe).
    /// </param>
    /// <returns><paramref name="b"/>, for fluent chaining.</returns>
    public static IDmonHostBuilder AddEgress(
        this IDmonHostBuilder b,
        Func<IServiceProvider, ValueTask<IChatClient>> egressFactory)
    {
        b.Services.AddSingleton(new EgressClientFactory(egressFactory));
        return b;
    }

    /// <summary>
    /// Registers the egress backend for the triage router (eager overload).
    /// </summary>
    /// <param name="b">The host builder.</param>
    /// <param name="egress">The pre-built egress <see cref="IChatClient"/>.</param>
    /// <returns><paramref name="b"/>, for fluent chaining.</returns>
    public static IDmonHostBuilder AddEgress(
        this IDmonHostBuilder b,
        IChatClient egress)
        => b.AddEgress(_ => ValueTask.FromResult(egress));
}

/// <summary>
/// Captures the three backend delegates and constructs a <see cref="TriageRouter"/> from DI.
/// Registered as <see cref="ITerminalClientFactory"/> by <see cref="TriageRegistrationExtensions.UseTriage"/>.
/// </summary>
/// <remarks>
/// <see cref="Create"/> performs NO I/O — it only captures delegates and constructs the router.
/// Backends are resolved lazily on first use inside the router (ADR-027 D1 / ADR-032 D3).
/// </remarks>
public sealed class TriageRouterFactory : ITerminalClientFactory
{
    /// <inheritdoc />
    public IChatClient Create(IServiceProvider services)
    {
        FirstLineRawClientFactory firstLineWrapper = services.GetRequiredService<FirstLineRawClientFactory>();
        EscalationClientFactory escalationWrapper = services.GetRequiredService<EscalationClientFactory>();
        EgressClientFactory egressWrapper = services.GetRequiredService<EgressClientFactory>();
        AbilityRegistry abilities = services.GetRequiredService<AbilityRegistry>();
        TriageOptions options = services.GetRequiredService<TriageOptions>();

        return new TriageRouter(
            firstLineWrapper.Factory,
            escalationWrapper.Factory,
            egressWrapper.Factory,
            services,
            abilities,
            options);
    }
}
