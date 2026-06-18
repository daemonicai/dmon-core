using Microsoft.Extensions.AI;

namespace Dmon.Abstractions.Extensions;

/// <summary>
/// Defines the contract for a named set of AI-callable tools grouped under a scope label.
/// </summary>
/// <remarks>
/// <para>
/// An ability provider exposes a cohesive collection of <see cref="AITool"/> instances
/// under an opaque <see cref="Scope"/> string. The scope vocabulary is a convention
/// agreed between ability authors and the consuming agent; <c>Dmon.Abstractions</c>
/// defines no scope constants.
/// </para>
/// <para>
/// Ability providers are registered at composition time in <c>Dmon.cs</c> and collected
/// by <c>AbilityRegistry</c> at host build time.
/// </para>
/// </remarks>
public interface IAbilityProvider
{
    /// <summary>
    /// Gets the opaque scope label for this ability set.
    /// Scope is a convention between ability authors and the consuming agent.
    /// </summary>
    string Scope { get; }

    /// <summary>
    /// Gets the <see cref="AITool"/> instances provided by this ability set.
    /// </summary>
    IEnumerable<AITool> Tools { get; }
}
