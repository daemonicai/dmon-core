using Dmon.Abstractions.Extensions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Extensions;

/// <summary>
/// Resolves <see cref="AITool"/> instances from registered <see cref="IAbilityProvider"/>
/// instances, filtered by scope.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton in <c>AddDmonCore()</c> and resolves <see cref="IAbilityProvider"/>
/// via DI enumeration — no reflection pass and no manual post-build loop.
/// </para>
/// <para>
/// Tool lists are built per-call; results are never cached across calls.
/// Providers registered via <c>AddAbilities&lt;T&gt;()</c> are orthogonal to
/// <c>IToolExtension</c> instances in the global pipeline: they never enter
/// the global tool list, and global extensions never appear in <see cref="ForScope"/>.
/// </para>
/// </remarks>
public sealed class AbilityRegistry
{
    private readonly IEnumerable<IAbilityProvider> _providers;

    /// <summary>
    /// Initialises the registry with all registered <see cref="IAbilityProvider"/> instances.
    /// An empty enumerable is valid; all scopes return empty lists in that case.
    /// </summary>
    public AbilityRegistry(IEnumerable<IAbilityProvider> providers)
    {
        _providers = providers;
    }

    /// <summary>
    /// Returns all <see cref="AITool"/> instances from providers whose
    /// <see cref="IAbilityProvider.Scope"/> matches <paramref name="scope"/>
    /// using <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// Tools from non-matching scopes are never included.
    /// </summary>
    /// <param name="scope">The opaque scope label to filter by.</param>
    /// <returns>
    /// A list of matching tools, or an empty list when no provider matches
    /// or when no providers are registered.
    /// </returns>
    public IList<AITool> ForScope(string scope)
    {
        List<AITool> result = [];
        foreach (IAbilityProvider provider in _providers)
        {
            if (string.Equals(provider.Scope, scope, StringComparison.OrdinalIgnoreCase))
            {
                result.AddRange(provider.Tools);
            }
        }
        return result;
    }
}
