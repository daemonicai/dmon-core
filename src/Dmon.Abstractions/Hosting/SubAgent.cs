using Dmon.Abstractions.Hosting;

namespace Dmon.Hosting;

/// <summary>
/// Factory helpers for building sub-agent <see cref="IChatClientFactory"/> instances
/// from provider-agnostic <see cref="IProviderRegistration"/> configuration actions.
/// </summary>
/// <remarks>
/// <para>
/// A sub-agent tool extension captures an <see cref="IChatClientFactory"/> at construction
/// time and invokes it per tool call, keeping its client completely independent of the
/// host's provider registry (ADR-010 D3, ADR-022 D6).
/// </para>
/// <para>
/// Provider-agnostic base pattern (single package reference to <c>Dmon.Abstractions</c>):
/// </para>
/// <code language="csharp">
/// public static T AddAgentWebSearch&lt;T&gt;(this T r, Action&lt;IProviderRegistration&gt; configure)
///     where T : IToolRegistration
///     =&gt; r.AddToolExtension(new WebSearchExtension(SubAgent.BuildClient(configure)));
/// </code>
/// <para>
/// Provider-bundling convenience (optional, in a provider-specific package):
/// </para>
/// <code language="csharp">
/// public static T AddAgentWebSearch&lt;T&gt;(this T r, string model = "gemini-2.5-flash-lite")
///     where T : IToolRegistration
///     =&gt; r.AddAgentWebSearch(p =&gt; p.UseGemini(model));
/// </code>
/// <para>
/// Structural validation (exactly one provider verb + a model selected) is eager and runs
/// inside <see cref="BuildClient"/> — a malformed action throws <see cref="InvalidOperationException"/>
/// immediately, before any host startup.  Credential resolution and client construction are
/// deferred to the first <see cref="IChatClientFactory.CreateAsync"/> call.
/// </para>
/// </remarks>
public static class SubAgent
{
    /// <summary>
    /// Runs <paramref name="configure"/> against a fresh, isolated
    /// <see cref="IProviderRegistration"/> and returns the resulting
    /// <see cref="IChatClientFactory"/>.
    /// </summary>
    /// <param name="configure">
    /// An action that calls exactly one <c>Use&lt;Provider&gt;</c> verb and selects a model.
    /// </param>
    /// <returns>
    /// An <see cref="IChatClientFactory"/> that lazily resolves credentials and constructs
    /// the underlying <see cref="Microsoft.Extensions.AI.IChatClient"/> on first use.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown immediately when <paramref name="configure"/> fails structural validation —
    /// zero or multiple provider verbs, or no model selected.
    /// </exception>
    public static IChatClientFactory BuildClient(Action<IProviderRegistration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        SubAgentProviderRegistration registration = new();
        configure(registration);
        return registration.Build();
    }
}
