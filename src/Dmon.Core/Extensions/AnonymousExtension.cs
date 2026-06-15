using Dmon.Abstractions.Extensions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Extensions;

/// <summary>
/// Null-object extension for .csx scripts that do not implement <see cref="IToolExtension"/>.
/// Uses the default <see cref="IToolExtension.Evaluate"/> (→ Prompt) and
/// <see cref="IToolExtension.CreateConfirmRequest"/> (→ Low risk) implementations.
/// </summary>
internal sealed class AnonymousExtension : IToolExtension
{
    private readonly string _name;
    private readonly string _description;
    private readonly IEnumerable<AIFunction> _tools;

    internal AnonymousExtension(string name, string description, IEnumerable<AIFunction> tools)
    {
        _name = name;
        _description = description;
        _tools = tools;
    }

    public string Name => _name;
    public string Description => _description;
    public IEnumerable<AIFunction> Tools => _tools;
}
