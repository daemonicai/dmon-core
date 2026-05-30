using Dmon.Extensions;
using Microsoft.Extensions.AI;

namespace Dmon.ExtensionSmoke;

/// <summary>
/// Minimal out-of-tree extension that compiles against the packed Dmon.Extensions package.
/// Verifies the IDmonExtension contract is usable from a package reference with no ProjectReference.
/// </summary>
public sealed class SmokeExtension : IDmonExtension
{
    public string Name => "smoke";
    public string Description => "Smoke-test extension — verifies IDmonExtension compiles from package reference.";

    public IEnumerable<AIFunction> Tools =>
    [
        AIFunctionFactory.Create(
            ([System.ComponentModel.Description("The message to echo")] string message) => message,
            "echo",
            "Echoes the provided message back.")
    ];
}
