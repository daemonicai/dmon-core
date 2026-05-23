# Spike: Dotnet.Script.Core Embedding

**Date:** 2026-05-22
**Status:** Passed

## Tasks

- [x] 2.1 Load `.csx` file from disk via hosted API
- [x] 2.2 Verify `#r "nuget:..."` resolution works
- [x] 2.3 Verify script returns value accessible to host
- [x] 2.4 Verify collectible `AssemblyLoadContext` isolation
- [x] 2.5 Document findings

## Key Findings

### Embedding API

`Dotnet.Script.Core` 2.0.0 embeds successfully in .NET 10. The public API is:

```csharp
var logFactory = new LogFactory(type => (level, message, exception) => { /* ... */ });
var console = new ScriptConsole(Console.Out, Console.In, Console.Error);
var compiler = new ScriptCompiler(logFactory, cachePath: null!, useRestoreCache: false);
var runner = new ScriptRunner(compiler, logFactory, console);

var code = SourceText.From(File.ReadAllText("script.csx"));
var context = new ScriptContext(code, workingDir, Array.Empty<string>(),
    scriptPath, OptimizationLevel.Debug, ScriptMode.Script, Array.Empty<string>());

var result = await runner.Execute<object>(context);
```

### NuGet Resolution

`#r "nuget: PackageName, Version"` directives resolve automatically. Dotnet.Script.Core creates a temporary `.csproj`, restores packages via `dotnet restore`, and feeds the resolved assemblies into Roslyn's `ScriptOptions`.

### Collectible ALC

`ScriptAssemblyLoadContext` (derives from `AssemblyLoadContext`) supports collectible mode for unloading extensions. Multiple independent scripts can run in separate ALCs.

### Binary vs Source Difference

The NuGet binary for 2.0.0 differs from the GitHub tag v2.0.0 source:
- Binary API uses `ScriptCompiler(LogFactory, string, bool)` (not `ScriptLogger`/`ScriptLoader`)
- Several types (`ScriptLogger`, `ScriptLoader`) are not public in the binary
- Use the binary API as the authoritative reference

## Conclusion

No update to ADR-002 required. Dotnet.Script.Core is production-ready for dmon's `.csx` extension loading.
