using Dmon.Abstractions.Extensions;
using Dmon.Core.Extensions;
using Dmon.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Core.Tests.Composition;

/// <summary>
/// Verifies the AddBuiltinTools() composition verb satisfies the builtin-tools spec scenarios:
/// (a) calling .AddBuiltinTools() yields exactly the six snake_case tool functions in IToolRegistry;
/// (b) omitting .AddBuiltinTools() yields no builtin tools (locked-down composition is valid).
/// </summary>
public sealed class BuiltinToolsRegistrationTests
{
    private static readonly string[] ExpectedToolNames =
    [
        "read_file",
        "write_file",
        "edit_file",
        "glob",
        "fetch",
        "bash",
    ];

    // -------------------------------------------------------------------------
    // (a) AddBuiltinTools() registers the six snake_case tools
    // -------------------------------------------------------------------------

    [Fact]
    public void AddBuiltinTools_RegistersSixTools_WithExpectedNames()
    {
        using StringReader stdin = new(string.Empty);
        using StringWriter stdout = new();

        DmonBuiltHost host = DmonHost
            .CreateBuilder([])
            .WithoutTelemetry()
            .WithStdio(stdin, stdout)
            .AddBuiltinTools()
            .Build();

        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
        IReadOnlyList<AIFunction> tools = registry.GetAll();

        // The engine always registers extension.search and extension.readme.
        // We assert the six builtin names are all present, not an exact count.
        foreach (string name in ExpectedToolNames)
        {
            Assert.Contains(tools, t => t.Name == name);
        }
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("glob")]
    [InlineData("fetch")]
    [InlineData("bash")]
    public void AddBuiltinTools_EachToolIsDiscoverableByName(string toolName)
    {
        using StringReader stdin = new(string.Empty);
        using StringWriter stdout = new();

        DmonBuiltHost host = DmonHost
            .CreateBuilder([])
            .WithoutTelemetry()
            .WithStdio(stdin, stdout)
            .AddBuiltinTools()
            .Build();

        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();

        IToolExtension? owner = registry.FindExtension(toolName);
        Assert.NotNull(owner);
    }

    // -------------------------------------------------------------------------
    // (b) Omitting AddBuiltinTools() is valid — no builtin tools, not an error
    // -------------------------------------------------------------------------

    [Fact]
    public void WithoutAddBuiltinTools_NoBuiltinToolsInRegistry()
    {
        using StringReader stdin = new(string.Empty);
        using StringWriter stdout = new();

        DmonBuiltHost host = DmonHost
            .CreateBuilder([])
            .WithoutTelemetry()
            .WithStdio(stdin, stdout)
            .Build();

        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
        IReadOnlyList<AIFunction> tools = registry.GetAll();

        // None of the six builtin tool names must be present.
        foreach (string name in ExpectedToolNames)
        {
            Assert.DoesNotContain(tools, t => t.Name == name);
        }
    }

    [Fact]
    public void WithoutAddBuiltinTools_HostBuildsSuccessfully()
    {
        // Proves a tool-less locked-down composition is valid and not an error.
        using StringReader stdin = new(string.Empty);
        using StringWriter stdout = new();

        DmonBuiltHost host = DmonHost
            .CreateBuilder([])
            .WithoutTelemetry()
            .WithStdio(stdin, stdout)
            .Build();

        // Host builds and the registry is available — the engine is functional.
        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
        Assert.NotNull(registry);
    }
}
