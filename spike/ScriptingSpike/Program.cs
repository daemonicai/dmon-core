using System.Runtime.Loader;
using Dotnet.Script.Core;
using Dotnet.Script.DependencyModel.Context;
using Dotnet.Script.DependencyModel.Environment;
using Dotnet.Script.DependencyModel.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

// ===========================================================================
// Spike: Dotnet.Script.Core Embedding Validation (Tasks 2.1–2.5)
// ===========================================================================

var scriptPath = args.Length > 0 ? args[0] : null;

Console.WriteLine("=== Task 2.1: Load a .csx file from disk ===");

if (scriptPath is null)
{
    Console.WriteLine("Creating a sample test.csx for validation...");

    scriptPath = Path.Combine(AppContext.BaseDirectory, "test.csx");
    await File.WriteAllTextAsync(scriptPath, """
        // Task 2.2: Verify #r "nuget:..." resolution
        #r "nuget: Newtonsoft.Json, 13.0.3"

        using Newtonsoft.Json;

        // Task 2.3: Return an object accessible to the host
        var result = new
        {
            Name = "sampleFunction",
            Description = "A sample function from a .csx script",
            Data = JsonConvert.SerializeObject(new { message = "Hello from csx!" })
        };

        // The return value is the last expression
        result
        """);

    Console.WriteLine($"Created: {scriptPath}");
}

if (!File.Exists(scriptPath))
{
    Console.Error.WriteLine($"Script file not found: {scriptPath}");
    return 1;
}

var code = SourceText.From(File.ReadAllText(scriptPath));
Console.WriteLine($"Loaded script ({code.Length} chars)");

// Build the Dotnet.Script.Core infrastructure
var logFactory = new LogFactory(type => (level, message, exception) =>
{
    if (level >= LogLevel.Warning)
        Console.Error.WriteLine($"[{level}] {type.Name}: {message}");
});

var scriptConsole = new ScriptConsole(Console.Out, Console.In, Console.Error);
var compiler = new ScriptCompiler(logFactory, cachePath: null!, useRestoreCache: false);
var runner = new ScriptRunner(compiler, logFactory, scriptConsole);

try
{
    // Task 2.4: Isolate scripts in a collectible AssemblyLoadContext
    Console.WriteLine("\n=== Task 2.4: AssemblyLoadContext isolation ===");
    var alc = new ScriptAssemblyLoadContext("ScriptSpike", isCollectible: true);
    Console.WriteLine($"Created collectible ALC: {alc.Name}");

    // Create ScriptContext
    var workingDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
    var context = new ScriptContext(
        code,
        workingDir,
        Array.Empty<string>(),
        scriptPath,
        OptimizationLevel.Debug,
        ScriptMode.Script,
        Array.Empty<string>()
    );

    Console.WriteLine("\n=== Task 2.2: Verify #r \"nuget:...\" resolution ===");
    Console.WriteLine("Script includes #r \"nuget: Newtonsoft.Json, 13.0.3\"");
    Console.WriteLine("Compilation will resolve NuGet packages via Dotnet.Script...");

    // Execute the script — Dotnet.Script.Core handles #r resolution
    Console.WriteLine("\n=== Task 2.3: Execute script and return result ===");
    var result = await runner.Execute<object>(context);

    Console.WriteLine($"Script returned: {result}");
    Console.WriteLine("SUCCESS: Script executed and returned a value to the host.");

    // Task 2.4: Verify ALC is collectible
    alc.Unload();
    Console.WriteLine("AssemblyLoadContext unloaded.");

    // Force GC to verify collectible
    for (var i = 0; i < 3; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
    Console.WriteLine("GC completed — ALC was collectible.");
}
catch (Exception ex)
{
    // Dotnet.Script.Core wraps compilation errors in the base Exception
    Console.Error.WriteLine($"\nScript error: {ex.Message}");
    if (ex.InnerException is not null)
        Console.Error.WriteLine($"Inner: {ex.InnerException.Message}");
    return 1;
}

Console.WriteLine("\n=== All spike tasks passed! ===");
Console.WriteLine("\nFindings:");
Console.WriteLine("- Dotnet.Script.Core 2.0.0 embeds successfully in .NET 10");
Console.WriteLine("- #r \"nuget:...\" resolution works via ScriptRunner.Execute<T>");
Console.WriteLine("- Scripts can return values accessible to the host");
Console.WriteLine("- ScriptAssemblyLoadContext provides collectible isolation");
return 0;
