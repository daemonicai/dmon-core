using Daemon.Routing;
using Dmon.Abstractions.Hosting;
using Dmon.Core.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

// Wrapper records disambiguate the three bare IChatClient backends in DI so
// TriageRouterFactory.Create can resolve the right one without keyed registration.
internal sealed record E2bRawClient(IChatClient Client);
internal sealed record ReasonerClient(IChatClient Client);
internal sealed record EgressClient(IChatClient Client);

/// <summary>
/// Composition verbs that wire the triage router into the dmon builder.
/// </summary>
public static class TriageRegistrationExtensions
{
    /// <summary>
    /// Registers the triage router factory and the e2b backend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="e2bRaw"/> is used as both the classifier (no tools, structured output)
    /// and — after wrapping with <c>FunctionInvokingChatClient</c> — as the e2b-with-tools
    /// backend (R2). Both roles are built inside <see cref="TriageRouterFactory.Create"/>.
    /// </para>
    /// <para>
    /// Call <see cref="AddReasoner"/> and <see cref="AddEgress"/> on the same builder
    /// to supply the remaining backends. All three are resolved by
    /// <see cref="TriageRouterFactory"/> when the host is built.
    /// </para>
    /// </remarks>
    /// <param name="b">The host builder.</param>
    /// <param name="e2bRaw">
    /// The pre-built e2b <see cref="IChatClient"/> (provider SDK client, no pipeline wrapping).
    /// </param>
    /// <param name="options">Optional triage options; defaults to <see cref="TriageOptions"/> defaults.</param>
    /// <returns><paramref name="b"/>, for fluent chaining.</returns>
    public static IDmonHostBuilder UseTriage(
        this IDmonHostBuilder b,
        IChatClient e2bRaw,
        TriageOptions? options = null)
    {
        b.Services.AddSingleton(new E2bRawClient(e2bRaw));
        b.Services.AddSingleton(options ?? new TriageOptions());
        b.Services.AddSingleton<ITerminalClientFactory, TriageRouterFactory>();
        return b;
    }

    /// <summary>
    /// Registers the reasoner backend for the triage router.
    /// </summary>
    /// <param name="b">The host builder.</param>
    /// <param name="reasoner">The pre-built reasoner <see cref="IChatClient"/>.</param>
    /// <returns><paramref name="b"/>, for fluent chaining.</returns>
    public static IDmonHostBuilder AddReasoner(
        this IDmonHostBuilder b,
        IChatClient reasoner)
    {
        b.Services.AddSingleton(new ReasonerClient(reasoner));
        return b;
    }

    /// <summary>
    /// Registers the egress backend for the triage router.
    /// </summary>
    /// <remarks>
    /// Egress is provider-agnostic: pass any <see cref="IChatClient"/> — the router
    /// never references a provider package or performs a name-based lookup (R6).
    /// </remarks>
    /// <param name="b">The host builder.</param>
    /// <param name="egress">The pre-built egress <see cref="IChatClient"/>.</param>
    /// <returns><paramref name="b"/>, for fluent chaining.</returns>
    public static IDmonHostBuilder AddEgress(
        this IDmonHostBuilder b,
        IChatClient egress)
    {
        b.Services.AddSingleton(new EgressClient(egress));
        return b;
    }
}

/// <summary>
/// Resolves the three backends and constructs a <see cref="TriageRouter"/> from DI.
/// Registered as <see cref="ITerminalClientFactory"/> by <see cref="TriageRegistrationExtensions.UseTriage"/>.
/// </summary>
public sealed class TriageRouterFactory : ITerminalClientFactory
{
    /// <inheritdoc />
    public IChatClient Create(IServiceProvider services)
    {
        E2bRawClient e2bWrapper = services.GetRequiredService<E2bRawClient>();
        ReasonerClient reasonerWrapper = services.GetRequiredService<ReasonerClient>();
        EgressClient egressWrapper = services.GetRequiredService<EgressClient>();
        AbilityRegistry abilities = services.GetRequiredService<AbilityRegistry>();
        TriageOptions options = services.GetRequiredService<TriageOptions>();

        // R2: classifier = e2bRaw (no tools, structured output).
        IChatClient classifier = e2bWrapper.Client;

        // R2: e2bWithTools = e2bRaw wrapped with function invocation.
        IChatClient e2bWithTools = e2bWrapper.Client
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        return new TriageRouter(
            classifier,
            e2bWithTools,
            reasonerWrapper.Client,
            egressWrapper.Client,
            abilities,
            options);
    }
}
