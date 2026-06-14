using Dmon.Protocol;

namespace Dmon.Terminal;

/// <summary>
/// Implements <c>dmon init</c>: scaffolds an editable <c>Dmon.cs</c> composition root
/// in the current working directory.
/// </summary>
public static class InitCommand
{
    private const string FileName = "Dmon.cs";

    /// <summary>
    /// Writes a <c>Dmon.cs</c> scaffold to <paramref name="targetDirectory"/>.
    /// </summary>
    /// <param name="targetDirectory">Directory in which to write <c>Dmon.cs</c>.</param>
    /// <param name="stdout">Writer for progress messages.</param>
    /// <param name="stderr">Writer for error messages.</param>
    /// <returns>0 on success, 1 if <c>Dmon.cs</c> already exists.</returns>
    public static int Run(string targetDirectory, TextWriter stdout, TextWriter stderr)
    {
        string outputPath = Path.Combine(targetDirectory, FileName);
        if (File.Exists(outputPath))
        {
            stderr.WriteLine($"error: {FileName} already exists at {outputPath}");
            stderr.WriteLine("Remove it manually if you want to regenerate the scaffold.");
            return 1;
        }

        string content = BuildScaffold();
        File.WriteAllText(outputPath, content);
        stdout.WriteLine($"Created {outputPath}");
        stdout.WriteLine();
        stdout.WriteLine("Next steps:");
        stdout.WriteLine($"  dotnet build {FileName}   # compile the composition root");
        stdout.WriteLine($"  dotnet run {FileName}     # run the core");
        return 0;
    }

    public static string BuildScaffold()
    {
        string pin = ProtocolVersion.Current;
        return $"""
// dmon composition root — edit this file to customise your agent.
// Add extensions by declaring packages and wiring them via .AddExtension<T>().
//
// Example (add an extension):
//   #:package Acme.DmonExt@1.0.*
//   ...
//   DmonHost.CreateBuilder(args)
//       .AddExtension<Acme.DmonExt.AcmeExtension>()
//       .Build()
//       .RunAsync();
//
// Build:  dotnet build {FileName}
// Run:    dotnet run {FileName}
#:package dmoncore@{pin}.*

using Dmon.Hosting;

await DmonHost.CreateBuilder(args).Build().RunAsync();
""";
    }
}
