using Dmon.Core.Extensions;
using Dmon.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dmon.Core.Tests.Extensions;

/// <summary>
/// Verifies middleware discovery in <see cref="NuGetExtensionLoader"/>:
/// attribute-gated discovery, skip-without-attribute, per-middleware throw resilience,
/// tool-only assembly produces no middleware, and assembly with both tool and middleware.
///
/// Spec scenarios covered (extension-middleware spec, "Extension loader discovers
/// and instantiates middleware" requirement):
///   Scenario: "[DmonMiddleware] type is discovered and surfaced on result.Middleware"
///   Scenario: "IDmonMiddleware without [DmonMiddleware] is ignored"
///   Scenario: "Middleware constructor throws — startup continues"
///   Scenario: "Tool-only assembly — middleware list is empty"
///   Scenario: "Assembly with both a tool and a middleware — both are surfaced"
/// </summary>
public sealed class NuGetExtensionLoaderMiddlewareTests : IDisposable
{
    private static readonly IServiceProvider NullSp = new MwTestNullServiceProvider();
    private readonly string _tempDir;

    // Assembly location of Dmon.Extensions so Roslyn can resolve IDmonMiddleware
    // and DmonMiddlewareAttribute in emitted assemblies.
    private static readonly string DmonExtensionsPath =
        typeof(IDmonMiddleware).Assembly.Location;

    // Microsoft.Extensions.AI for IChatClient in the emitted Wrap implementation.
    private static readonly string MeaiPath =
        typeof(IChatClient).Assembly.Location;

    public NuGetExtensionLoaderMiddlewareTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dmon-mw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // -------------------------------------------------------------------------
    // Scenario: [DmonMiddleware]-annotated IDmonMiddleware is discovered
    // -------------------------------------------------------------------------

    /// <summary>
    /// An assembly that contains exactly one [DmonMiddleware] IDmonMiddleware
    /// implementation surfaces it in result.Middleware.
    /// </summary>
    [Fact]
    public async Task LoadAsync_AnnotatedMiddleware_IsSurfacedOnResult()
    {
        string guid = Guid.NewGuid().ToString("N");
        string asmName = $"MwAnnotated{guid}";
        string dllPath = Path.Combine(_tempDir, asmName + ".dll");

        EmitAssembly(
            assemblyName: asmName,
            source: $$"""
                using System;
                using System.Collections.Generic;
                using System.Threading;
                using System.Threading.Tasks;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                [DmonMiddleware(Priority = 10)]
                public sealed class {{asmName}}Mw : IDmonMiddleware
                {
                    public IChatClient Wrap(IChatClient inner)
                    {
                        ArgumentNullException.ThrowIfNull(inner);
                        return inner;
                    }
                }
                """,
            outputPath: dllPath,
            additionalRefs: [DmonExtensionsPath, MeaiPath]);

        NuGetExtensionLoader loader = new(NullSp);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = dllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.False(
            result.Name.StartsWith("__error__", StringComparison.Ordinal),
            $"Expected successful load but got error: {result.Description}");
        Assert.Single(result.Middleware);
        Assert.IsAssignableFrom<IDmonMiddleware>(result.Middleware[0]);
    }

    // -------------------------------------------------------------------------
    // Scenario: IDmonMiddleware without [DmonMiddleware] is ignored
    // -------------------------------------------------------------------------

    /// <summary>
    /// A type implementing IDmonMiddleware but lacking [DmonMiddleware] must not
    /// appear in result.Middleware. The assembly must still load (via its IDmonExtension).
    /// </summary>
    [Fact]
    public async Task LoadAsync_MiddlewareWithoutAttribute_IsIgnored()
    {
        string guid = Guid.NewGuid().ToString("N");
        string asmName = $"MwNoAttr{guid}";
        string dllPath = Path.Combine(_tempDir, asmName + ".dll");

        EmitAssembly(
            assemblyName: asmName,
            source: $$"""
                using System;
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                // This type implements IDmonMiddleware but has no [DmonMiddleware] — must be ignored.
                public sealed class {{asmName}}Mw : IDmonMiddleware
                {
                    public IChatClient Wrap(IChatClient inner)
                    {
                        ArgumentNullException.ThrowIfNull(inner);
                        return inner;
                    }
                }

                // Provide a real IDmonExtension so the load is not rejected as empty.
                public sealed class {{asmName}}Ext : IDmonExtension
                {
                    public string Name => "{{asmName}}";
                    public string Description => "no-attr middleware test";
                    public IEnumerable<AIFunction> Tools => [];
                }
                """,
            outputPath: dllPath,
            additionalRefs: [DmonExtensionsPath, MeaiPath]);

        NuGetExtensionLoader loader = new(NullSp);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = dllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.False(
            result.Name.StartsWith("__error__", StringComparison.Ordinal),
            $"Expected successful load but got error: {result.Description}");
        Assert.Empty(result.Middleware);
    }

    // -------------------------------------------------------------------------
    // Scenario: Middleware constructor throws — startup continues
    // -------------------------------------------------------------------------

    /// <summary>
    /// When a middleware constructor throws, that middleware is skipped (not surfaced),
    /// but the rest of the assembly (e.g. a tool) still loads without error.
    /// </summary>
    [Fact]
    public async Task LoadAsync_MiddlewareCtorThrows_SkippedAndRestLoads()
    {
        string guid = Guid.NewGuid().ToString("N");
        string asmName = $"MwCtorThrow{guid}";
        string dllPath = Path.Combine(_tempDir, asmName + ".dll");

        EmitAssembly(
            assemblyName: asmName,
            source: $$"""
                using System;
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                // This middleware always throws in its constructor.
                [DmonMiddleware]
                public sealed class {{asmName}}BadMw : IDmonMiddleware
                {
                    public {{asmName}}BadMw()
                    {
                        throw new InvalidOperationException("deliberate ctor failure");
                    }

                    public IChatClient Wrap(IChatClient inner) => inner;
                }

                // This tool extension must still be discovered despite the bad middleware.
                public sealed class {{asmName}}Ext : IDmonExtension
                {
                    public string Name => "{{asmName}}";
                    public string Description => "ctor-throw test";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => "ok", "{{asmName}}_probe")];
                }
                """,
            outputPath: dllPath,
            additionalRefs: [DmonExtensionsPath, MeaiPath]);

        NuGetExtensionLoader loader = new(NullSp);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = dllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        // The load must succeed (tool is still present).
        Assert.False(
            result.Name.StartsWith("__error__", StringComparison.Ordinal),
            $"Expected successful load but got error: {result.Description}");

        // The bad middleware must not appear in the result.
        Assert.Empty(result.Middleware);

        // The tool from the same assembly must still be surfaced.
        Assert.NotEmpty(result.Tools);
        Assert.Contains(result.Tools, f => f.Name == $"{asmName}_probe");
    }

    // -------------------------------------------------------------------------
    // Scenario: Middleware constructor throws — warning IS logged (task 6.3 logging half)
    // -------------------------------------------------------------------------

    /// <summary>
    /// When a middleware constructor throws, the loader must call
    /// <see cref="ILogger.LogWarning"/> via the <see cref="IServiceProvider"/>.
    /// Inject a capturing <see cref="ILogger{T}"/> so the call is observable.
    /// </summary>
    [Fact]
    public async Task LoadAsync_MiddlewareCtorThrows_LogsWarning()
    {
        string guid = Guid.NewGuid().ToString("N");
        string asmName = $"MwCtorThrowLog{guid}";
        string dllPath = Path.Combine(_tempDir, asmName + ".dll");

        EmitAssembly(
            assemblyName: asmName,
            source: $$"""
                using System;
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                [DmonMiddleware]
                public sealed class {{asmName}}BadMw : IDmonMiddleware
                {
                    public {{asmName}}BadMw()
                    {
                        throw new InvalidOperationException("deliberate ctor failure for log test");
                    }

                    public IChatClient Wrap(IChatClient inner) => inner;
                }

                // Provide a real IDmonExtension so the load is not rejected as empty.
                public sealed class {{asmName}}Ext : IDmonExtension
                {
                    public string Name => "{{asmName}}";
                    public string Description => "log-warning test";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => "ok", "{{asmName}}_probe")];
                }
                """,
            outputPath: dllPath,
            additionalRefs: [DmonExtensionsPath, MeaiPath]);

        CapturingLogger capturingLogger = new();
        IServiceProvider sp = new CapturingLoggerServiceProvider(capturingLogger);
        NuGetExtensionLoader loader = new(sp);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = dllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        // The middleware must be skipped.
        Assert.Empty(result.Middleware);

        // A warning must have been emitted by the logger.
        Assert.True(
            capturingLogger.HasWarning,
            "Expected a LogWarning call when the middleware constructor throws.");
    }

    // -------------------------------------------------------------------------
    // Scenario: Tool-only assembly — middleware list is empty
    // -------------------------------------------------------------------------

    /// <summary>
    /// An assembly with only IDmonExtension tools produces an empty Middleware list.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ToolOnlyAssembly_MiddlewareIsEmpty()
    {
        string guid = Guid.NewGuid().ToString("N");
        string asmName = $"MwToolOnly{guid}";
        string dllPath = Path.Combine(_tempDir, asmName + ".dll");

        EmitAssembly(
            assemblyName: asmName,
            source: $$"""
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                public sealed class {{asmName}}Ext : IDmonExtension
                {
                    public string Name => "{{asmName}}";
                    public string Description => "tool-only test";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => "hi", "{{asmName}}_hi")];
                }
                """,
            outputPath: dllPath,
            additionalRefs: [DmonExtensionsPath, MeaiPath]);

        NuGetExtensionLoader loader = new(NullSp);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = dllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.False(
            result.Name.StartsWith("__error__", StringComparison.Ordinal),
            $"Expected successful load but got error: {result.Description}");
        Assert.Empty(result.Middleware);
        Assert.NotEmpty(result.Tools);
    }

    // -------------------------------------------------------------------------
    // Scenario: Assembly with both a tool and a middleware — both are surfaced
    // -------------------------------------------------------------------------

    /// <summary>
    /// An assembly containing one IDmonExtension and one [DmonMiddleware] IDmonMiddleware
    /// surfaces both in the load result.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ToolAndMiddlewareAssembly_BothAreSurfaced()
    {
        string guid = Guid.NewGuid().ToString("N");
        string asmName = $"MwAndTool{guid}";
        string dllPath = Path.Combine(_tempDir, asmName + ".dll");

        EmitAssembly(
            assemblyName: asmName,
            source: $$"""
                using System;
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                [DmonMiddleware(Priority = 5)]
                public sealed class {{asmName}}Mw : IDmonMiddleware
                {
                    public IChatClient Wrap(IChatClient inner)
                    {
                        ArgumentNullException.ThrowIfNull(inner);
                        return inner;
                    }
                }

                public sealed class {{asmName}}Ext : IDmonExtension
                {
                    public string Name => "{{asmName}}";
                    public string Description => "tool+middleware test";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => "combined", "{{asmName}}_combined")];
                }
                """,
            outputPath: dllPath,
            additionalRefs: [DmonExtensionsPath, MeaiPath]);

        NuGetExtensionLoader loader = new(NullSp);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = dllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.False(
            result.Name.StartsWith("__error__", StringComparison.Ordinal),
            $"Expected successful load but got error: {result.Description}");

        // Tool surfaced.
        Assert.NotEmpty(result.Tools);
        Assert.Contains(result.Tools, f => f.Name == $"{asmName}_combined");

        // Middleware surfaced.
        Assert.Single(result.Middleware);
        Assert.IsAssignableFrom<IDmonMiddleware>(result.Middleware[0]);
    }

    // -------------------------------------------------------------------------
    // Scenario: Tool constructor throws — assembly load fails (original behavior)
    // -------------------------------------------------------------------------

    /// <summary>
    /// When a tool (IDmonExtension) constructor throws, the exception must propagate
    /// out of LoadAsync so ExtensionService surfaces it as an error rather than silently
    /// dropping the tool. This asserts the original tool-loader semantics are unchanged.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ToolCtorThrows_PropagatesAndFailsLoad()
    {
        string guid = Guid.NewGuid().ToString("N");
        string asmName = $"ToolCtorThrow{guid}";
        string dllPath = Path.Combine(_tempDir, asmName + ".dll");

        EmitAssembly(
            assemblyName: asmName,
            source: $$"""
                using System;
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                // A tool whose constructor always throws.
                public sealed class {{asmName}}BadExt : IDmonExtension
                {
                    public {{asmName}}BadExt()
                    {
                        throw new InvalidOperationException("deliberate tool ctor failure");
                    }

                    public string Name => "{{asmName}}";
                    public string Description => "bad tool";
                    public IEnumerable<AIFunction> Tools => [];
                }
                """,
            outputPath: dllPath,
            additionalRefs: [DmonExtensionsPath, MeaiPath]);

        NuGetExtensionLoader loader = new(NullSp);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = dllPath };

        // The exception must propagate — tool ctor failures are not silently swallowed.
        await Assert.ThrowsAnyAsync<Exception>(() => loader.LoadAsync(source));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void EmitAssembly(
        string assemblyName,
        string source,
        string outputPath,
        IReadOnlyList<string> additionalRefs) =>
        TestAssemblyEmitter.EmitAssembly(assemblyName, source, outputPath, additionalRefs);
}

file sealed class MwTestNullServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

/// <summary>
/// Records whether any LogWarning call was made, regardless of message or exception.
/// Used to verify the middleware-ctor-throws warning path (task 6.3).
/// </summary>
file sealed class CapturingLogger : ILogger<NuGetExtensionLoader>
{
    public bool HasWarning { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            HasWarning = true;
        }
    }
}

/// <summary>
/// Returns a <see cref="CapturingLogger"/> for <c>ILogger&lt;NuGetExtensionLoader&gt;</c>
/// and null for every other service type.
/// </summary>
file sealed class CapturingLoggerServiceProvider(CapturingLogger logger) : IServiceProvider
{
    private static readonly Type LoggerType = typeof(ILogger<NuGetExtensionLoader>);

    public object? GetService(Type serviceType) =>
        serviceType == LoggerType ? logger : null;
}
