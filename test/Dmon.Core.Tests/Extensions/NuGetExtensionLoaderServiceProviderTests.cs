using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Core.Extensions;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Core.Tests.Extensions;

/// <summary>
/// Verifies that the <see cref="IServiceProvider"/> passed to extension constructors by
/// <see cref="NuGetExtensionLoader"/> resolves <see cref="IConfiguration"/> and
/// <see cref="IEnumerable{T}"/> of <see cref="IProviderFactory"/>.
///
/// Spec scenarios covered:
///   "Factory resolution from the injected provider" — asserted via factory-count in the
///       load result tool return value and the non-empty IEnumerable assertion.
///   "IConfiguration is resolvable" — asserted via the model-string round-trip assertion.
///   "Extension reads its model setting" — asserted via the commands:probe-ext:model value.
/// </summary>
public sealed class NuGetExtensionLoaderServiceProviderTests : IDisposable
{
    private const string ExtensionName = "probe-ext";
    private const string ModelValue = "gemini/gemini-2.5-flash";
    private const int StubFactoryCount = 2;

    private readonly string _tempDir;

    public NuGetExtensionLoaderServiceProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dmon-sp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// End-to-end test: an extension with an (IServiceProvider) constructor resolves
    /// IConfiguration and IEnumerable&lt;IProviderFactory&gt; from the injected provider,
    /// and the load succeeds (the constructor throws if either service is missing or the
    /// model key is absent, and that exception propagates out of LoadAsync as-is).
    /// </summary>
    [Fact]
    public async Task LoadAsync_ExtensionWithServiceProviderCtor_ResolvesConfigurationAndFactories()
    {
        // Build a real IServiceProvider that mirrors the production registrations
        // relevant to sub-agent extensions.
        IServiceProvider serviceProvider = BuildServiceProvider();

        // Use unique assembly name so the Default ALC's first-writer-wins cache does
        // not interfere with other runs in the same process.
        string guid = Guid.NewGuid().ToString("N");
        string extAssemblyName = $"SpProbeExt{guid}";

        string extDir = Path.Combine(_tempDir, "sp-probe");
        Directory.CreateDirectory(extDir);

        string extDllPath = Path.Combine(extDir, extAssemblyName + ".dll");

        // The emitted extension:
        //   - has an (IServiceProvider sp) constructor
        //   - resolves IConfiguration — reads commands:probe-ext:model, throws if null
        //   - resolves IEnumerable<IProviderFactory> — throws if empty
        //   - encodes both resolved values into observable state so the test can assert:
        //       Name  = "probe-ext:<factoryCount>"
        //       Tools = one AIFunction ("probe_model") returning the resolved model string
        // If IConfiguration is absent or the model key is missing, the ctor throws and
        // LoadAsync propagates that exception — there is no "__error__" sentinel result.
        EmitAssembly(
            assemblyName: extAssemblyName,
            source: $$"""
                using System;
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Dmon.Abstractions.Providers;
                using Microsoft.Extensions.AI;
                using Microsoft.Extensions.Configuration;

                public sealed class {{extAssemblyName}}Extension : IDmonExtension
                {
                    private readonly string _resolvedModel;
                    private readonly int _factoryCount;

                    public {{extAssemblyName}}Extension(IServiceProvider sp)
                    {
                        IConfiguration config = (IConfiguration)sp.GetService(typeof(IConfiguration))
                            ?? throw new InvalidOperationException("IConfiguration was not registered in the injected IServiceProvider.");

                        string? model = config["commands:{{ExtensionName}}:model"];
                        if (string.IsNullOrEmpty(model))
                            throw new InvalidOperationException("commands:{{ExtensionName}}:model was null or empty in IConfiguration.");

                        IEnumerable<IProviderFactory> factories = (IEnumerable<IProviderFactory>)
                            (sp.GetService(typeof(IEnumerable<IProviderFactory>))
                             ?? throw new InvalidOperationException("IEnumerable<IProviderFactory> was not registered in the injected IServiceProvider."));

                        int count = 0;
                        foreach (IProviderFactory _ in factories) count++;
                        if (count == 0)
                            throw new InvalidOperationException("IEnumerable<IProviderFactory> resolved but was empty.");

                        _resolvedModel = model;
                        _factoryCount = count;
                    }

                    // Name encodes factory count so the test can assert it positively.
                    public string Name => $"{{ExtensionName}}:{_factoryCount}";
                    public string Description => "Service-provider guarantee test extension.";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => _resolvedModel, "probe_model")];
                }
                """,
            outputPath: extDllPath,
            additionalRefs: []);

        NuGetExtensionLoader loader = new(serviceProvider);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = extDllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        // "IConfiguration is resolvable" + "Extension reads its model setting":
        // If IConfiguration was absent or commands:probe-ext:model was missing, the
        // constructor would have thrown and LoadAsync would have propagated the exception.
        // Reaching this line means both resolved successfully.
        Assert.False(
            result.Name.StartsWith("__error__", StringComparison.Ordinal),
            $"Expected successful load but got error result: {result.Description}");

        // "Factory resolution from the injected provider":
        // Name is "probe-ext:<factoryCount>"; assert the count is at least the number
        // of stub factories registered (StubFactoryCount = 2).
        string[] nameParts = result.Name.Split(':');
        Assert.Equal(2, nameParts.Length);
        Assert.Equal(ExtensionName, nameParts[0]);
        bool factoryCountParsed = int.TryParse(nameParts[1], out int resolvedFactoryCount);
        Assert.True(factoryCountParsed, $"Expected Name to end with a factory count integer, got: {result.Name}");
        Assert.True(
            resolvedFactoryCount >= StubFactoryCount,
            $"Expected at least {StubFactoryCount} provider factories to be resolved, got {resolvedFactoryCount}.");

        // Positive assertion: load produced a live extension with tools.
        Assert.NotNull(result.Extension);
        Assert.NotEmpty(result.Tools);

        // "Extension reads its model setting" — end-to-end round-trip:
        // invoke the probe_model AIFunction and assert the resolved model string is exact.
        AIFunction? probeModel = result.Tools.FirstOrDefault(f => f.Name == "probe_model");
        Assert.NotNull(probeModel);
        object? invokeResult = await probeModel!.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
        Assert.Equal(ModelValue, invokeResult?.ToString());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();

        // IConfiguration with commands:<name>:model entry — mirrors the D3 convention.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"commands:{ExtensionName}:model"] = ModelValue
            })
            .Build();

        services.AddSingleton<IConfiguration>(configuration);

        // Two stub IProviderFactory registrations so IEnumerable<IProviderFactory>
        // resolves non-empty.  Using stubs keeps the test hermetic — the real factories
        // drag in credential and provider-config dependencies.
        services.AddSingleton<IProviderFactory, StubProviderFactoryA>();
        services.AddSingleton<IProviderFactory, StubProviderFactoryB>();

        return services.BuildServiceProvider();
    }

    private static void EmitAssembly(
        string assemblyName,
        string source,
        string outputPath,
        IReadOnlyList<string> additionalRefs) =>
        TestAssemblyEmitter.EmitAssembly(assemblyName, source, outputPath, additionalRefs);
}

// ---------------------------------------------------------------------------
// Stub IProviderFactory implementations (hermetic — no real provider deps)
// ---------------------------------------------------------------------------

file sealed class StubProviderFactoryA : IProviderFactory
{
    public string AdapterName => "stub-a";
    public string DisplayName => "Stub A";
    public string DefaultModelId => "stub-a/model";
    public string DefaultEnvVar => "STUB_A_KEY";
    public ChatClientCapabilities GetCapabilities(string modelId) => new();
    public ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stub — not intended for invocation.");
    public ValueTask<WizardStep> GetNextStepAsync(Dmon.Abstractions.Wizard.WizardState state, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stub — not intended for invocation.");
}

file sealed class StubProviderFactoryB : IProviderFactory
{
    public string AdapterName => "stub-b";
    public string DisplayName => "Stub B";
    public string DefaultModelId => "stub-b/model";
    public string DefaultEnvVar => "STUB_B_KEY";
    public ChatClientCapabilities GetCapabilities(string modelId) => new();
    public ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stub — not intended for invocation.");
    public ValueTask<WizardStep> GetNextStepAsync(Dmon.Abstractions.Wizard.WizardState state, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Stub — not intended for invocation.");
}
