using Dmon.Protocol.Enums;
using Dmon.Protocol.Models;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Abstractions.Extensions;

/// <summary>
/// Defines the contract for a dmon tool extension.
/// Extensions expose AI-callable tools by implementing this interface and returning
/// <see cref="AIFunction"/> instances from <see cref="Tools"/>.
/// </summary>
/// <remarks>
/// <para>
/// Extensions are pure <see cref="Microsoft.Extensions.AI"/> code. They reference only
/// <c>Microsoft.Extensions.AI</c> and this package; they do not depend on dmon internals.
/// </para>
/// <para>
/// To create an extension:
/// <list type="number">
///   <item>Create a class library targeting .NET 10 that references <c>Dmon.Abstractions</c>.</item>
///   <item>Implement <see cref="IToolExtension"/> on one or more public types.</item>
///   <item>Use <see cref="AIFunctionFactory"/> (from <c>Microsoft.Extensions.AI</c>) or
///       <see cref="DmonAIFunctionFactory"/> (from this package) to create
///       <see cref="AIFunction"/> instances.</item>
///   <item>Return the functions from <see cref="Tools"/>.</item>
/// </list>
/// </para>
/// <para>
/// Extensions are registered at composition time via <c>builder.AddToolExtension&lt;T&gt;()</c>
/// in <c>Dmon.cs</c>. The dmon core registers their functions into the tool registry
/// before the JSONL/stdio loop starts.
/// </para>
/// </remarks>
public interface IToolExtension
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

    /// <summary>
    /// Evaluates the permission required for the given tool call.
    /// The default implementation always returns <see cref="PermissionResult.Prompt"/>.
    /// Override to grant implicit permission for safe operations or deny dangerous ones.
    /// </summary>
    PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => PermissionResult.Prompt;

    /// <summary>
    /// Creates a <see cref="ToolConfirmRequest"/> for the given tool call.
    /// The default implementation uses <see cref="RiskLevel.Low"/> as the risk level.
    /// Override to assign higher risk levels to destructive or network operations.
    /// </summary>
    ToolConfirmRequest CreateConfirmRequest(FunctionCallContent call)
        => new()
        {
            Id = call.CallId,
            Name = call.Name,
            Args = call.Arguments is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(call.Arguments),
            Risk = RiskLevel.Low
        };
}
