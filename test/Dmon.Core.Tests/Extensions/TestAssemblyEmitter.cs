using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dmon.Core.Tests.Extensions;

/// <summary>
/// Roslyn-based helper that compiles C# source into a .dll on disk.
/// Used by extension loader tests that need freshly-emitted assemblies with
/// unique names so the Default AssemblyLoadContext's first-writer-wins cache
/// does not cause cross-test interference.
/// </summary>
internal static class TestAssemblyEmitter
{
    /// <summary>
    /// Compiles <paramref name="source"/> into a .dll at <paramref name="outputPath"/>.
    /// References are inferred from all non-dynamic assemblies currently loaded in the
    /// process, supplemented by <paramref name="additionalRefs"/> (absolute .dll paths).
    /// Throws <see cref="InvalidOperationException"/> if compilation fails.
    /// </summary>
    internal static void EmitAssembly(
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
    /// Builds the <see cref="MetadataReference"/> list for Roslyn compilation.
    /// Includes all non-dynamic, non-empty assemblies currently loaded in the process
    /// plus any extra paths in <paramref name="additionalPaths"/>.
    /// </summary>
    internal static List<MetadataReference> BuildMetadataReferences(IReadOnlyList<string> additionalPaths)
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

            // Skip assemblies emitted into temp dirs by earlier tests in this run;
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
}
