using Daemon.Extensions;
using Microsoft.Extensions.AI;

namespace Daemon.Core.Tests.Extensions;

/// <summary>
/// Simple test extension for unit-testing the NuGet extension loader.
/// </summary>
public sealed class TestExtension : IDaemonExtension
{
    public string Name => "TestExtension";

    public string Description => "A test extension for unit-testing the loader.";

    public IEnumerable<AIFunction> Tools => [
        AIFunctionFactory.Create(() => "hello", "TestHello", "Returns a greeting."),
        AIFunctionFactory.Create((int x, int y) => x + y, "TestAdd", "Adds two integers.")
    ];
}

/// <summary>
/// Second test extension for verifying multi-extension assembly discovery.
/// </summary>
public sealed class SecondTestExtension : IDaemonExtension
{
    public string Name => "SecondTestExtension";

    public string Description => "Another test extension.";

    public IEnumerable<AIFunction> Tools => [
        AIFunctionFactory.Create(() => 42, "TestMeaning", "Returns the meaning of life.")
    ];
}
