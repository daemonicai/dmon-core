using Dmon.Abstractions.Extensions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Extensions;

/// <summary>
/// Per-session registry of tools available to the LLM.
/// <see cref="ChatOptions.Tools"/> is built from this registry on each call,
/// so extensions loaded or unloaded mid-session are reflected immediately.
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// Registers a named extension's tools into the registry.
    /// If an extension with the same name is already registered, it is replaced.
    /// </summary>
    void Register(string extensionName, IToolExtension extension, IEnumerable<AIFunction> tools);

    /// <summary>
    /// Returns the <see cref="IToolExtension"/> that registered the tool with the given name,
    /// or <see langword="null"/> if no extension owns that tool.
    /// </summary>
    IToolExtension? FindExtension(string toolName);

    /// <summary>
    /// Removes all tools registered under the given extension name.
    /// Does not throw if the extension is not registered.
    /// </summary>
    void Unregister(string extensionName);

    /// <summary>
    /// Returns all currently registered tools as a flat list suitable for
    /// assignment to <see cref="ChatOptions.Tools"/>.
    /// </summary>
    IReadOnlyList<AIFunction> GetAll();

    /// <summary>
    /// Returns a snapshot of the registered extension names and their tool counts.
    /// </summary>
    IReadOnlyList<RegisteredExtensionSnapshot> GetSnapshot();

    /// <summary>
    /// Clears all registered tools. Used when a new session is loaded or
    /// the registry is reset.
    /// </summary>
    void Clear();
}

public readonly record struct RegisteredExtensionSnapshot(string Name, int ToolCount);
