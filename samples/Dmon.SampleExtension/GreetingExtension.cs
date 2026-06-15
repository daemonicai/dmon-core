using System.ComponentModel;
using Dmon.Extensions;
using Microsoft.Extensions.AI;

namespace Dmon.SampleExtension;

/// <summary>
/// Minimal dmon extension that exposes a single greeting tool.
/// Used by <c>samples/Dmon.ComposedCore</c> to prove compile-time composition.
/// </summary>
public sealed class GreetingExtension : IDmonExtension
{
    public string Name => "greeting";
    public string Description => "Provides a greeting tool. Sample extension for composition-root smoke testing.";

    public IEnumerable<AIFunction> Tools =>
    [
        AIFunctionFactory.Create(
            ([Description("The name to greet")] string name) => $"Hello, {name}!",
            "greet",
            "Returns a greeting for the provided name.")
    ];
}
