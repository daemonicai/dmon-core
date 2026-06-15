using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Core.Extensions;
using Dmon.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dmon.Core.Tests.Hosting;

/// <summary>
/// Tests for the Group-3 verb grammar and DI-discovery surface (tasks 3.1–3.5).
/// Covers flat chaining on <see cref="DmonHostBuilder"/>, faceted reuse on a bare
/// <see cref="IToolRegistration"/>, and DI-constructed tool instantiation.
/// </summary>
public sealed class DmonHostBuilderVerbTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static DmonBuiltHost BuildHost(Action<DmonHostBuilder> configure)
    {
        DmonHostBuilder builder = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();
        configure(builder);
        return builder.Build();
    }

    // ── 3.1 — parameterless CreateBuilder overload ────────────────────────────

    /// <summary>
    /// <see cref="DmonHost.CreateBuilder()"/> (no args) produces a usable builder.
    /// </summary>
    [Fact]
    public void CreateBuilder_Parameterless_BuildsSuccessfully()
    {
        DmonBuiltHost host = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry()
            .Build();

        // DI container is available and tool registry is resolvable.
        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
        Assert.NotNull(registry);
    }

    // ── 3.1 — IDmonHostBuilder interface ─────────────────────────────────────

    /// <summary>
    /// <see cref="DmonHostBuilder"/> implements <see cref="IDmonHostBuilder"/>:
    /// both <c>Services</c> and <c>Configuration</c> are accessible.
    /// </summary>
    [Fact]
    public void DmonHostBuilder_ImplementsIDmonHostBuilder_ServiceCollectionAccessible()
    {
        DmonHostBuilder builder = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();

        IDmonHostBuilder iBuilder = builder;

        Assert.NotNull(iBuilder.Services);
        Assert.NotNull(iBuilder.Configuration);
    }

    // ── 3.2 / 3.3 — flat chaining on the builder ─────────────────────────────

    /// <summary>
    /// Flat chaining: <c>AddToolExtension</c> called directly on <see cref="DmonHostBuilder"/>
    /// returns <see cref="DmonHostBuilder"/> (not the interface) so the chain continues
    /// with full concrete-type API.
    /// </summary>
    [Fact]
    public void AddToolExtension_FlatChaining_ReturnsDmonHostBuilder()
    {
        // The compile-time assertion is implicit: this must compile as a chain.
        // The runtime assertion checks the tool was registered.
        DmonBuiltHost host = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry()
            .AddToolExtension<SimpleTestExtension>()
            .Build();

        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
        Assert.Contains(registry.GetAll(), t => t.Name == SimpleTestExtension.ExpectedToolName);
    }

    /// <summary>
    /// Instance overload of <c>AddToolExtension</c> registers the provided instance.
    /// </summary>
    [Fact]
    public void AddToolExtension_InstanceOverload_RegistersTool()
    {
        SimpleTestExtension instance = new();

        DmonBuiltHost host = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry()
            .AddToolExtension(instance)
            .Build();

        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
        Assert.Contains(registry.GetAll(), t => t.Name == SimpleTestExtension.ExpectedToolName);
    }

    // ── 3.3 — DI-constructed tool instantiation ───────────────────────────────

    /// <summary>
    /// <c>AddToolExtension&lt;T&gt;()</c> drops the <c>new()</c> constraint: a tool whose
    /// constructor takes a registered DI service is resolved correctly at build time.
    /// </summary>
    [Fact]
    public void AddToolExtension_DiConstructedTool_InjectsRegisteredService()
    {
        DmonBuiltHost host = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry()
            .AddToolExtension<DiDependentExtension>()
            .Build();

        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();

        // The tool must be present — proving DI-construction succeeded.
        Assert.Contains(registry.GetAll(), t => t.Name == DiDependentExtension.ExpectedToolName);

        // Verify the extension instance in the registry was actually DI-constructed
        // (it holds a non-null ILoggerFactory injected by DI).
        IToolExtension? owner = registry.FindExtension(DiDependentExtension.ExpectedToolName);
        Assert.NotNull(owner);
        DiDependentExtension typed = Assert.IsType<DiDependentExtension>(owner);
        Assert.True(typed.WasInjected, "ILoggerFactory should have been injected by DI.");
    }

    // ── 3.2 — faceted reuse on a bare IToolRegistration ─────────────────────

    /// <summary>
    /// Verifies that the Dmon.Abstractions extension methods compile and work when called
    /// on a bare <see cref="IToolRegistration"/> (the faceted-reuse path used by sub-agent
    /// tool packages). The result type is <see cref="IToolRegistration"/> (not the builder).
    /// </summary>
    [Fact]
    public void AddToolExtension_OnBareToolRegistration_RegistersViaDi()
    {
        // Arrange: a minimal IServiceCollection-backed IToolRegistration.
        ServiceCollection services = new();
        services.AddLogging();
        BareToolRegistration facet = new(services);

        // Act: call the Dmon.Hosting extension method on the bare facet.
        // This is the sub-agent reuse path — the verb returns the facet type.
        IToolRegistration result = facet.AddToolExtension(new SimpleTestExtension());

        // Assert: same object returned (chaining), and the service was registered.
        Assert.Same(facet, result);
        ServiceProvider sp = services.BuildServiceProvider();
        IEnumerable<IToolExtension> registered = sp.GetServices<IToolExtension>();
        Assert.Contains(registered, e => e.Name == "simple-test");
    }

    // ── 3.2 — UseModel verb ───────────────────────────────────────────────────

    /// <summary>
    /// <c>UseModel</c> sets the <c>activeModel</c> configuration key; the value
    /// is readable from <see cref="IActiveModelStore"/> after <c>Build()</c>.
    /// </summary>
    [Fact]
    public void UseModel_SetsActiveModelConfiguration()
    {
        DmonBuiltHost host = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry()
            .UseModel("anthropic", "claude-opus-4")
            .Build();

        Dmon.Core.Providers.IActiveModelStore store =
            host.Services.GetRequiredService<Dmon.Core.Providers.IActiveModelStore>();
        Dmon.Abstractions.Providers.ModelRef? loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("anthropic", loaded.Provider);
        Assert.Equal("claude-opus-4", loaded.Model);
    }

    // ── 3.4 — builtin tools registered via AddBuiltinTools() ────────────────

    /// <summary>
    /// Builtin tools are opt-in via <c>.AddBuiltinTools()</c> in the composition root.
    /// This test verifies that calling <c>.AddBuiltinTools()</c> makes "read_file"
    /// available in the registry after <c>Build()</c>, using the DI-discovery path.
    /// </summary>
    [Fact]
    public void BuiltinTools_RegisteredViaAddBuiltinTools_ReadFilePresent()
    {
        using StringReader stdin = new(string.Empty);
        using StringWriter stdout = new();

        DmonBuiltHost host = DmonHost.CreateBuilder()
            .WithStdio(stdin, stdout)
            .WithoutTelemetry()
            .AddBuiltinTools()
            .Build();

        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
        IReadOnlyList<AIFunction> tools = registry.GetAll();

        Assert.Contains(tools, t => t.Name == "read_file");
    }
}

// ── test-local extension types ─────────────────────────────────────────────────

/// <summary>
/// Minimal extension with a public parameterless constructor. Used for flat-chaining
/// and instance-registration tests.
/// </summary>
file sealed class SimpleTestExtension : IToolExtension
{
    internal const string ExpectedToolName = "simple_test_op";

    public string Name => "simple-test";
    public string Description => "Minimal extension for verb-grammar tests.";

    public IEnumerable<AIFunction> Tools =>
    [
        AIFunctionFactory.Create(
            () => "ok",
            ExpectedToolName,
            "Returns ok.")
    ];
}

/// <summary>
/// Extension whose constructor requires an <see cref="ILoggerFactory"/> from DI.
/// Verifies that <c>AddToolExtension&lt;T&gt;()</c> constructs via DI (task 3.3).
/// </summary>
file sealed class DiDependentExtension : IToolExtension
{
    internal const string ExpectedToolName = "di_dependent_op";

    private readonly ILoggerFactory _loggerFactory;

    public DiDependentExtension(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>True when <see cref="ILoggerFactory"/> was provided by DI.</summary>
    public bool WasInjected => _loggerFactory is not null;

    public string Name => "di-dependent";
    public string Description => "Extension that requires DI injection of ILoggerFactory.";

    public IEnumerable<AIFunction> Tools =>
    [
        AIFunctionFactory.Create(
            () => "injected",
            ExpectedToolName,
            "Returns injected.")
    ];
}

/// <summary>
/// Minimal <see cref="IToolRegistration"/> backed by a plain <see cref="IServiceCollection"/>.
/// Used to test the bare-facet path (sub-agent reuse scenario).
/// </summary>
file sealed class BareToolRegistration(IServiceCollection services) : IToolRegistration
{
    public IServiceCollection Services { get; } = services;
}
