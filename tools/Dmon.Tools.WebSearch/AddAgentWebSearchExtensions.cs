using Dmon.Abstractions.Hosting;
using Dmon.Tools.WebSearch;

namespace Dmon.Hosting;

/// <summary>
/// Composition verb that wires the <c>web_search</c> tool into the dmon builder.
/// </summary>
public static class AddAgentWebSearchExtensions
{
    /// <summary>
    /// Registers <see cref="WebSearchExtension"/> as an <see cref="IToolExtension"/>, backed by an
    /// isolated sub-agent <see cref="IChatClientFactory"/> built from <paramref name="configure"/>.
    /// </summary>
    /// <typeparam name="T">The builder or facet type being configured.</typeparam>
    /// <param name="registration">The tool registration surface.</param>
    /// <param name="configure">
    /// An action that calls exactly one <c>Use&lt;Provider&gt;</c> verb and selects a model.
    /// Structural validation (zero or multiple providers, no model) runs eagerly at build time
    /// inside <see cref="SubAgent.BuildClient"/>; the first call to this method will throw
    /// <see cref="InvalidOperationException"/> if the action is malformed.
    /// </param>
    /// <returns><paramref name="registration"/>, for fluent chaining.</returns>
    /// <remarks>
    /// <para>Canonical usage:</para>
    /// <code language="csharp">
    /// builder.AddAgentWebSearch(p => p.UseGemini("gemini-2.5-flash"));
    /// </code>
    /// <para>
    /// The <c>web_search</c> tool always prompts for permission on first use because the query
    /// leaves the device via the hosted provider (network egress, <see cref="Dmon.Protocol.Permissions.PermissionResult.Prompt"/>).
    /// The provider API key is resolved lazily on the first tool invocation — a missing key
    /// does not block startup.
    /// </para>
    /// </remarks>
    public static T AddAgentWebSearch<T>(this T registration, Action<IProviderRegistration> configure)
        where T : IToolRegistration
        => registration.AddToolExtension(new WebSearchExtension(SubAgent.BuildClient(configure)));
}
