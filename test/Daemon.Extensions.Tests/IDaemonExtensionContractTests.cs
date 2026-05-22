using Daemon.Extensions;
using Microsoft.Extensions.AI;

namespace Daemon.Extensions.Tests;

public class IDaemonExtensionContractTests
{
    [Fact]
    public void CanImplementInterface()
    {
        var extension = new SampleExtension();
        Assert.IsAssignableFrom<IDaemonExtension>(extension);
    }

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        var extension = new SampleExtension();
        Assert.Equal("SampleExtension", extension.Name);
    }

    [Fact]
    public void Description_ReturnsExpectedValue()
    {
        var extension = new SampleExtension();
        Assert.Equal("A sample extension for testing", extension.Description);
    }

    [Fact]
    public void Tools_ReturnsNonNullCollection()
    {
        var extension = new SampleExtension();
        var functions = extension.Tools;
        Assert.NotNull(functions);
    }

    [Fact]
    public void Tools_ReturnsAllRegisteredFunctions()
    {
        var extension = new SampleExtension();
        var functions = extension.Tools.ToList();

        Assert.Equal(2, functions.Count);
        Assert.Contains(functions, f => f.Name == "AddNumbers");
        Assert.Contains(functions, f => f.Name == "GreetPerson");
    }

    [Fact]
    public void Tools_FunctionsAreInvocable()
    {
        var extension = new SampleExtension();
        var functions = extension.Tools.ToList();

        Assert.All(functions, f => Assert.NotNull(f));
        Assert.All(functions, f => Assert.False(string.IsNullOrEmpty(f.Name)));
    }

    /// <summary>
    /// Minimal extension returning two simple AIFunctions.
    /// </summary>
    private sealed class SampleExtension : IDaemonExtension
    {
        public string Name => "SampleExtension";
        public string Description => "A sample extension for testing";

        public IEnumerable<AIFunction> Tools
        {
            get
            {
                yield return DaemonAIFunctionFactory.Create(
                    (int a, int b) => a + b,
                    "AddNumbers",
                    "Adds two integers and returns the sum.");

                yield return DaemonAIFunctionFactory.Create(
                    (string name, int age) => $"Hello {name}, you are {age} years old.",
                    "GreetPerson",
                    "Returns a personalised greeting for a person by name and age.");
            }
        }
    }
}