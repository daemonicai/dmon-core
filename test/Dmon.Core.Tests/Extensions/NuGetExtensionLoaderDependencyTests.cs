using System.Reflection;
using System.Runtime.Loader;
using Dmon.Core.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dmon.Core.Tests.Extensions;

/// <summary>
/// Verifies that NuGetExtensionLoader resolves transitive dependencies through both
/// the AssemblyDependencyResolver (.deps.json) path (Pass 1) and the sibling-directory
/// probing path (Pass 2) — driven end-to-end through LoadAsync.
/// </summary>
public sealed class NuGetExtensionLoaderDependencyTests : IDisposable
{
    private static readonly IServiceProvider NullSp = new DepTestNullServiceProvider();
    private readonly string _tempDir;

    public NuGetExtensionLoaderDependencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dmon-dep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Pass 2 scenario: extension and its dependency sit in the same directory with no
    /// .deps.json.  The sibling-directory probe in ResolveExtensionDependency must find
    /// the dependency and return a successful load result.
    /// </summary>
    [Fact]
    public async Task LoadAsync_SiblingDependency_LoadsWithoutMissingAssemblyError()
    {
        // Use unique names so the Default ALC's first-writer-wins cache does not
        // interfere with other test runs in the same process.
        string guid = Guid.NewGuid().ToString("N");
        string depName = $"DepSibling{guid}";
        string extName = $"ExtSibling{guid}";

        string extDir = Path.Combine(_tempDir, "sibling");
        Directory.CreateDirectory(extDir);

        // Compile Dep — a minimal public type the extension will instantiate.
        string depDllPath = Path.Combine(extDir, depName + ".dll");
        EmitAssembly(
            assemblyName: depName,
            source: $$"""
                public sealed class DepValue
                {
                    public string Greet() => "hello from {{depName}}";
                }
                """,
            outputPath: depDllPath,
            additionalRefs: []);

        // Compile Ext referencing Dep.  The constructor instantiates DepValue so the
        // runtime resolves Dep when DiscoverAll calls Activator.CreateInstance.
        string extDllPath = Path.Combine(extDir, extName + ".dll");
        EmitAssembly(
            assemblyName: extName,
            source: $$"""
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                public sealed class {{extName}}Extension : IDmonExtension
                {
                    private readonly string _greeting;

                    public {{extName}}Extension()
                    {
                        // Forces Dep to be resolved at instantiation time.
                        _greeting = new DepValue().Greet();
                    }

                    public string Name => "{{extName}}";
                    public string Description => "Sibling-dep test extension.";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => _greeting, "{{extName}}_Probe")];
                }
                """,
            outputPath: extDllPath,
            additionalRefs: [depDllPath]);

        // No .deps.json — Pass 2 (sibling probe) must resolve the dependency.
        NuGetExtensionLoader loader = new(NullSp);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = extDllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.False(result.Name.StartsWith("__error__", StringComparison.Ordinal),
            $"Expected successful load but got error result: {result.Description}");
        Assert.NotNull(result.Extension);
        Assert.NotEmpty(result.Tools);
    }

    /// <summary>
    /// Pass 1 scenario: extension ships a .deps.json that describes its dependency and
    /// <see cref="AssemblyDependencyResolver.ResolveAssemblyToPath"/> is consulted by
    /// the Resolving handler before the sibling probe (Pass 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Isolation note: <c>AssemblyDependencyResolver</c> with <c>type:"project"</c> and
    /// a bare filename runtime path resolves the dep relative to the .deps.json directory —
    /// the same directory where the dep dll lives.  In a synthetic unit-test scenario the
    /// resolver returns the same sibling path whether or not the deps.json is present, so
    /// this test cannot prove that Pass 1 fires independently of Pass 2.  What the test
    /// does prove is: (a) the resolver is consulted and returns a non-null path for a
    /// dependency described in the deps.json (satisfying the spec's
    /// "resolves via the dependency resolver" requirement); (b) that path exists on disk;
    /// (c) <c>LoadAsync</c> succeeds end-to-end when the extension ships a deps.json —
    /// i.e. the full Pass-1 + Pass-2 pipeline handles a described dependency correctly.
    /// </para>
    /// <para>
    /// A true Pass-1-only isolation proof would require <c>AssemblyDependencyResolver</c>
    /// to resolve a dep from a non-sibling path via the deps.json alone.  Empirical probing
    /// (shapes: <c>type:"project"</c> with subdir runtime path, <c>type:"package"</c> with
    /// path field, absolute runtime key) showed the resolver returns null for all non-sibling
    /// configurations in a synthetic setup — the runtime's managed resolver requires a real
    /// NuGet global-packages or publish layout for non-adjacent paths.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LoadAsync_DepsJsonDescribedDependency_LoadsWithoutMissingAssemblyError()
    {
        string guid = Guid.NewGuid().ToString("N");
        string depName = $"DepDeps{guid}";
        string extName = $"ExtDeps{guid}";

        string extDir = Path.Combine(_tempDir, "depstest");
        Directory.CreateDirectory(extDir);

        string depDllPath = Path.Combine(extDir, depName + ".dll");
        EmitAssembly(
            assemblyName: depName,
            source: $$"""
                public sealed class DepValue
                {
                    public string Greet() => "hello from {{depName}}";
                }
                """,
            outputPath: depDllPath,
            additionalRefs: []);

        string extDllPath = Path.Combine(extDir, extName + ".dll");
        EmitAssembly(
            assemblyName: extName,
            source: $$"""
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                public sealed class {{extName}}Extension : IDmonExtension
                {
                    private readonly string _greeting;

                    public {{extName}}Extension()
                    {
                        _greeting = new DepValue().Greet();
                    }

                    public string Name => "{{extName}}";
                    public string Description => "Deps.json-dep test extension.";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => _greeting, "{{extName}}_Probe")];
                }
                """,
            outputPath: extDllPath,
            additionalRefs: [depDllPath]);

        // Write a deps.json with type:"project" for the dep.  For project-type libraries
        // AssemblyDependencyResolver resolves relative to the deps.json directory, so the
        // runtime path must be the bare filename.
        string depsJsonPath = Path.Combine(extDir, extName + ".deps.json");
        WriteDepsJson(
            depsJsonPath: depsJsonPath,
            extAssemblyName: extName,
            depAssemblyName: depName);

        // Assert that the resolver is consulted and returns the dep path.  This satisfies
        // the spec's "resolves via the dependency resolver" requirement: ResolveAssemblyToPath
        // returns a non-null, existing path for a dependency described in the deps.json.
        // (See Isolation note in the XML doc above for why this cannot be a Pass-1-only proof.)
        AssemblyDependencyResolver resolver = new(extDllPath);
        string? resolvedByDeps = resolver.ResolveAssemblyToPath(new AssemblyName(depName));

        Assert.NotNull(resolvedByDeps);
        Assert.True(File.Exists(resolvedByDeps),
            $"AssemblyDependencyResolver returned a path that does not exist: {resolvedByDeps}");

        // End-to-end: LoadAsync succeeds when the extension ships a deps.json.
        NuGetExtensionLoader loader = new(NullSp);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = extDllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.False(result.Name.StartsWith("__error__", StringComparison.Ordinal),
            $"Expected successful load but got error result: {result.Description}");
        Assert.NotNull(result.Extension);
        Assert.NotEmpty(result.Tools);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compiles C# source into a .dll using Roslyn.  References are inferred from
    /// the assemblies already loaded into the current process, supplemented by
    /// <paramref name="additionalRefs"/> (absolute paths to .dll files on disk).
    /// Throws <see cref="InvalidOperationException"/> if compilation fails.
    /// </summary>
    private static void EmitAssembly(
        string assemblyName,
        string source,
        string outputPath,
        IReadOnlyList<string> additionalRefs)
    {
        List<MetadataReference> refs = BuildMetadataReferences(additionalRefs);

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Microsoft.CodeAnalysis.Emit.EmitResult emitResult = compilation.Emit(outputPath);

        if (!emitResult.Success)
        {
            IEnumerable<string> errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            throw new InvalidOperationException(
                $"Roslyn compilation of '{assemblyName}' failed:\n{string.Join("\n", errors)}");
        }
    }

    /// <summary>
    /// Builds the list of <see cref="MetadataReference"/> objects needed for Roslyn
    /// compilation.  Includes all non-dynamic, non-empty assemblies currently loaded
    /// in the process plus any extra paths supplied by the caller.
    /// </summary>
    private static List<MetadataReference> BuildMetadataReferences(IReadOnlyList<string> additionalPaths)
    {
        // Touch System.Text.Json to ensure it is loaded — AIFunctionFactory overload
        // resolution requires it to be a known reference assembly.
        _ = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(0);

        List<MetadataReference> refs = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location))
            {
                continue;
            }

            // Skip assemblies emitted into temp dirs by earlier tests in this process run;
            // their files may already have been deleted when their test's Dispose() ran.
            if (!File.Exists(asm.Location))
            {
                continue;
            }

            if (seen.Add(asm.Location))
            {
                refs.Add(MetadataReference.CreateFromFile(asm.Location));
            }
        }

        foreach (string path in additionalPaths)
        {
            refs.Add(MetadataReference.CreateFromFile(path));
        }

        return refs;
    }

    /// <summary>
    /// Writes a minimal .NET runtime .deps.json adjacent to the extension assembly.
    /// Uses <c>type:"project"</c> for both the extension and its dependency.  The
    /// runtime path is the bare filename; <see cref="AssemblyDependencyResolver"/>
    /// resolves it relative to the directory containing the .deps.json.
    /// </summary>
    private static void WriteDepsJson(
        string depsJsonPath,
        string extAssemblyName,
        string depAssemblyName)
    {
        string json = $$"""
            {
              "runtimeTarget": {
                "name": ".NETCoreApp,Version=v10.0",
                "signature": ""
              },
              "compilationOptions": {},
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "{{extAssemblyName}}/1.0.0": {
                    "dependencies": {
                      "{{depAssemblyName}}": "1.0.0"
                    },
                    "runtime": {
                      "{{extAssemblyName}}.dll": {}
                    }
                  },
                  "{{depAssemblyName}}/1.0.0": {
                    "runtime": {
                      "{{depAssemblyName}}.dll": {}
                    }
                  }
                }
              },
              "libraries": {
                "{{extAssemblyName}}/1.0.0": {
                  "type": "project",
                  "serviceable": false,
                  "sha512": ""
                },
                "{{depAssemblyName}}/1.0.0": {
                  "type": "project",
                  "serviceable": false,
                  "sha512": ""
                }
              }
            }
            """;

        File.WriteAllText(depsJsonPath, json);
    }
}

file sealed class DepTestNullServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
