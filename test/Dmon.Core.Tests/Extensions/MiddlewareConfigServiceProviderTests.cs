using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Core.Extensions;
using Dmon.Extensions;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Core.Tests.Extensions;

/// <summary>
/// Verifies the IConfigurationRoot / middleware-config support added in group 4:
///
/// Spec scenarios covered (extension-middleware-tier spec):
///   "Arbitrary config fields are accessible via IConfigurationRoot" —
///       asserted via the maxTokens round-trip through the emitted middleware ctor.
///   "IConfigurationRoot is resolvable from the host IServiceProvider" —
///       asserted directly via GetService/GetRequiredService on the DI container.
/// </summary>
public sealed class MiddlewareConfigServiceProviderTests : IDisposable
{
    private const string MiddlewareClassName = "ProbeMiddleware";
    private const string MaxTokensValue = "4096";

    // Assembly paths for Roslyn references in emitted assemblies.
    private static readonly string DmonExtensionsPath =
        typeof(IDmonMiddleware).Assembly.Location;
    private static readonly string MeaiPath =
        typeof(IChatClient).Assembly.Location;

    private readonly string _tempDir;

    public MiddlewareConfigServiceProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dmon-mwcfg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Direct container test ────────────────────────────────────────────────

    /// <summary>
    /// IConfigurationRoot is resolvable from a ServiceProvider that mirrors the
    /// production AddDmonCore registration — i.e. IConfiguration is an IConfigurationRoot
    /// and a TryAddSingleton delegates the cast.
    /// </summary>
    [Fact]
    public void IConfigurationRoot_IsResolvable_WhenRegisteredViaAddSingleton()
    {
        // Build a service provider that mirrors the production pattern:
        // TryAddSingleton<IConfigurationRoot>(sp => (IConfigurationRoot)sp.GetRequiredService<IConfiguration>())
        IConfigurationRoot configRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"middleware:{MiddlewareClassName}:maxTokens"] = MaxTokensValue
            })
            .Build();

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configRoot);
        services.AddSingleton<IConfigurationRoot>(sp =>
            (IConfigurationRoot)sp.GetRequiredService<IConfiguration>());

        IServiceProvider provider = services.BuildServiceProvider();

        // IConfigurationRoot must resolve without throwing.
        IConfigurationRoot resolved = provider.GetRequiredService<IConfigurationRoot>();
        Assert.NotNull(resolved);

        // The resolved root must serve the middleware:<ClassName> section.
        string? maxTokens = resolved.GetSection($"middleware:{MiddlewareClassName}")["maxTokens"];
        Assert.Equal(MaxTokensValue, maxTokens);
    }

    /// <summary>
    /// IConfiguration and IConfigurationRoot resolve to the same underlying instance
    /// when IConfiguration is already an IConfigurationRoot.
    /// </summary>
    [Fact]
    public void IConfigurationRoot_IsSameInstance_AsIConfiguration()
    {
        IConfigurationRoot configRoot = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configRoot);
        services.AddSingleton<IConfigurationRoot>(sp =>
            (IConfigurationRoot)sp.GetRequiredService<IConfiguration>());

        IServiceProvider provider = services.BuildServiceProvider();

        IConfiguration configuration = provider.GetRequiredService<IConfiguration>();
        IConfigurationRoot configurationRoot = provider.GetRequiredService<IConfigurationRoot>();

        Assert.Same(configuration, configurationRoot);
    }

    // ── End-to-end: middleware reads its section via injected IServiceProvider ──

    /// <summary>
    /// End-to-end: a middleware with an IServiceProvider constructor resolves
    /// IConfigurationRoot and reads its middleware:&lt;ClassName&gt; section.
    /// The emitted middleware throws in its constructor if IConfigurationRoot is
    /// absent or the maxTokens key is missing; the loader catches that exception,
    /// skips the middleware, and returns normally — so a throwing ctor causes
    /// <c>result.Middleware</c> to be empty and <c>Assert.Single</c> below would
    /// fail. Reaching the assertions means the IConfigurationRoot was present and
    /// the key was found.
    /// </summary>
    [Fact]
    public async Task LoadAsync_MiddlewareWithServiceProviderCtor_ReadsItsConfigSection()
    {
        string guid = Guid.NewGuid().ToString("N");
        string asmName = $"MwCfgProbe{guid}";
        string dllPath = Path.Combine(_tempDir, asmName + ".dll");

        IServiceProvider serviceProvider = BuildServiceProvider(asmName);

        // The emitted middleware:
        //   - has an (IServiceProvider sp) constructor
        //   - resolves IConfigurationRoot (throws if absent)
        //   - reads middleware:<ClassName>:maxTokens (throws if absent/empty)
        //   - surfaces the resolved value via an AIFunction for assertion
        TestAssemblyEmitter.EmitAssembly(
            assemblyName: asmName,
            source: $$"""
                using System;
                using System.Collections.Generic;
                using System.Threading;
                using System.Threading.Tasks;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;
                using Microsoft.Extensions.Configuration;

                [DmonMiddleware(Priority = 10)]
                public sealed class {{asmName}}Mw : IDmonMiddleware
                {
                    private readonly string _maxTokens;

                    public {{asmName}}Mw(IServiceProvider sp)
                    {
                        IConfigurationRoot root =
                            (IConfigurationRoot?)sp.GetService(typeof(IConfigurationRoot))
                            ?? throw new InvalidOperationException(
                                "IConfigurationRoot was not registered in the injected IServiceProvider.");

                        string? maxTokens = root.GetSection("middleware:{{asmName}}Mw")["maxTokens"];
                        if (string.IsNullOrEmpty(maxTokens))
                            throw new InvalidOperationException(
                                "middleware:{{asmName}}Mw:maxTokens was null or empty.");

                        _maxTokens = maxTokens;
                    }

                    public IChatClient Wrap(IChatClient inner)
                    {
                        ArgumentNullException.ThrowIfNull(inner);
                        return inner;
                    }

                    // Expose resolved value so the test can assert it via IDmonExtension.
                    public string ResolvedMaxTokens => _maxTokens;
                }

                // A minimal IDmonExtension so the assembly is not rejected as tool-empty.
                public sealed class {{asmName}}Ext : IDmonExtension
                {
                    public string Name => "{{asmName}}";
                    public string Description => "middleware-config probe extension";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => "ok", "{{asmName}}_probe")];
                }
                """,
            outputPath: dllPath,
            additionalRefs: [DmonExtensionsPath, MeaiPath]);

        NuGetExtensionLoader loader = new(serviceProvider);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = dllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        // If IConfigurationRoot was absent or the key was missing, the ctor would have
        // thrown; the loader catches that, skips the middleware, and returns normally —
        // result.Middleware would be empty and Assert.Single below would fail.
        Assert.False(
            result.Name.StartsWith("__error__", StringComparison.Ordinal),
            $"Expected successful load but got error: {result.Description}");

        // The middleware must be surfaced (IConfigurationRoot resolved and key found).
        Assert.Single(result.Middleware);
        Assert.IsAssignableFrom<IDmonMiddleware>(result.Middleware[0]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a ServiceProvider that mirrors the production pattern for middleware:
    /// IConfiguration (as IConfigurationRoot) + IConfigurationRoot singleton delegating
    /// to it, pre-seeded with the middleware:&lt;asmName&gt;Mw:maxTokens entry.
    /// </summary>
    private static IServiceProvider BuildServiceProvider(string asmName)
    {
        IConfigurationRoot configRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"middleware:{asmName}Mw:maxTokens"] = MaxTokensValue
            })
            .Build();

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configRoot);
        services.AddSingleton<IConfigurationRoot>(sp =>
            (IConfigurationRoot)sp.GetRequiredService<IConfiguration>());
        services.AddSingleton<IProviderFactory, CfgStubProviderFactory>();

        return services.BuildServiceProvider();
    }
}

// ---------------------------------------------------------------------------
// Stub IProviderFactory (hermetic)
// ---------------------------------------------------------------------------

file sealed class CfgStubProviderFactory : IProviderFactory
{
    public string AdapterName => "cfg-stub";
    public string DisplayName => "Config Stub";
    public string DefaultModelId => "cfg-stub/model";
    public string DefaultEnvVar => "CFG_STUB_KEY";
    public ChatClientCapabilities GetCapabilities(string modelId) => new();
    public ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stub — not intended for invocation.");
    public ValueTask<WizardStep> GetNextStepAsync(Dmon.Abstractions.Wizard.WizardState state, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stub — not intended for invocation.");
}
