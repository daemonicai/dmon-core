using Microsoft.Extensions.AI;

namespace Daemon.Extensions;

/// <summary>
/// Defines the contract for a daemon extension.
/// Extensions expose AI-callable tools by implementing this interface and returning
/// <see cref="AIFunction"/> instances from <see cref="Tools"/>.
/// </summary>
/// <remarks>
/// <para>
/// Extensions are pure <see cref="Microsoft.Extensions.AI"/> code. They reference only
/// <c>Microsoft.Extensions.AI</c> and this package; they do not depend on daemon internals.
/// </para>
/// <para>
/// To create an extension:
/// <list type="number">
///   <item>Create a class library targeting .NET 10 that references <c>Daemon.Extensions</c>.</item>
///   <item>Implement <see cref="IDaemonExtension"/> on one or more public types.</item>
///   <item>Use <see cref="AIFunctionFactory"/> (from <c>Microsoft.Extensions.AI</c>) or
///       <see cref="DaemonAIFunctionFactory"/> (from this package) to create
///       <see cref="AIFunction"/> instances.</item>
///   <item>Return the functions from <see cref="Tools"/>.</item>
/// </list>
/// </para>
/// <para>
/// Extensions are loaded via the <c>extension.load</c> RPC command. The daemon core
/// discovers <see cref="IDaemonExtension"/> implementations, instantiates them, and
/// registers their functions into the per-session tool registry.
/// </para>
/// </remarks>
public interface IDaemonExtension
{
    /// <summary>
    /// Gets the human-readable name of this extension.
    /// Displayed in the host UI when the extension is loaded or listed.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a description of what this extension provides.
    /// Shown in host UI and extension listings. Keep it concise — one or two sentences.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the <see cref="AIFunction"/> instances provided by this extension.
    /// Called once when the extension is loaded. The returned functions are registered
    /// into the session tool registry and made available to the LLM on subsequent turns.
    /// </summary>
    IEnumerable<AIFunction> Tools { get; }
}