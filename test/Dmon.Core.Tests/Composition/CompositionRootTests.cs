using System.Diagnostics;
using System.Reflection;
using Dmon.Core.Extensions;
using Dmon.Extensions;
using Dmon.Hosting;
using Dmon.Protocol;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Core.Tests.Composition;

/// <summary>
/// Integration test (b-wire) for the composition-root hosting model (ADR-019).
/// Requires packing dmoncore + Dmon.SampleExtension; <see cref="ComposedCoreFeedFixture"/>
/// does this into a unique temp directory in setup and deletes it in teardown.
/// The composed Dmon.cs is copied into a fresh temp directory with its own nuget.config
/// so the source-controlled samples/Dmon.ComposedCore/nuget.config is not touched.
/// </summary>
[Collection("ComposedCoreBuild")]
public sealed class ComposedCoreWireTests(ComposedCoreFeedFixture feed)
{
    [Fact]
    public async Task ComposedCore_ExtensionToolsAvailableAtStartup_NoRuntimeLoadEvent()
    {
        string repoRoot = LocateRepoRoot();
        string composedCsSource = Path.Combine(repoRoot, "samples", "Dmon.ComposedCore", "Dmon.cs");

        if (!File.Exists(composedCsSource))
            throw new InvalidOperationException($"Composed core not found at {composedCsSource}.");

        // Copy Dmon.cs to a temp dir with a fresh nuget.config so the source-controlled
        // samples/Dmon.ComposedCore/nuget.config is never overwritten.
        string tempDir = Path.Combine(Path.GetTempPath(), $"dmon-composed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string dmonCsPath = Path.Combine(tempDir, "Dmon.cs");
            File.Copy(composedCsSource, dmonCsPath);
            WriteNugetConfig(tempDir, feed.FeedPath);

            // Build the composed core against the temp feed.
            await RunDotnetAsync("build", dmonCsPath, tempDir, timeoutSeconds: 120);

            // Spawn the composed core and collect stdout until agentReady or timeout.
            List<string> linesBeforeReady = [];
            string? agentReadyLine = null;

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
            ProcessStartInfo psi = new()
            {
                FileName = "dotnet",
                // --no-build: keeps build output off the JSONL stdout stream.
                Arguments = $"run --no-build \"{dmonCsPath}\"",
                WorkingDirectory = tempDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process proc = new() { StartInfo = psi };
            proc.Start();

            try
            {
                using StreamReader reader = proc.StandardOutput;

                for (int i = 0; i < 50 && !cts.IsCancellationRequested; i++)
                {
                    string? line;
                    try { line = await reader.ReadLineAsync(cts.Token); }
                    catch (OperationCanceledException) { break; }

                    if (line is null) break;

                    if (line.Contains("\"agentReady\""))
                    {
                        agentReadyLine = line;
                        break;
                    }

                    linesBeforeReady.Add(line);
                }
            }
            finally
            {
                try { proc.StandardInput.Close(); } catch { /* best effort */ }

                try
                {
                    using CancellationTokenSource exitCts = new(TimeSpan.FromSeconds(5));
                    await proc.WaitForExitAsync(exitCts.Token);
                }
                catch { /* best effort */ }

                if (!proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                }
            }

            Assert.NotNull(agentReadyLine);

            // Verify no extensionLoaded event arrived before agentReady.
            // Builder-registered extensions bypass ExtensionService; they go directly
            // into the IToolRegistry in DmonHostBuilder.Build() with no event emitted.
            bool hasRuntimeLoadEvent = linesBeforeReady.Any(l => l.Contains("\"extensionLoaded\""));
            Assert.False(
                hasRuntimeLoadEvent,
                "Compile-time extensions must not emit an extensionLoaded event at startup.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string LocateRepoRoot()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        return Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));
    }

    private static void WriteNugetConfig(string directory, string feedPath)
    {
        string content = $"""
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="local-dmon-feed" value="{feedPath}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
""";
        File.WriteAllText(Path.Combine(directory, "nuget.config"), content);
    }

    private static async Task RunDotnetAsync(
        string verb,
        string targetPath,
        string workingDirectory,
        int timeoutSeconds)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            Arguments = $"{verb} \"{targetPath}\"",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process proc = new() { StartInfo = psi };
        proc.Start();

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"dotnet {verb} timed out after {timeoutSeconds}s.");
        }

        string output = await stdoutTask;
        string errors = await stderrTask;

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {verb} failed (exit {proc.ExitCode}).\nstdout: {output}\nstderr: {errors}");
        }
    }
}

/// <summary>
/// In-process composition-root tests that run without a NuGet feed.
/// </summary>
public sealed class CompositionRootTests
{
    // -------------------------------------------------------------------------
    // (b-inproc) AddExtension<T> positively registers the composed tool
    // -------------------------------------------------------------------------

    [Fact]
    public void ComposedHost_GreetingExtension_RegistersGreetTool()
    {
        // Proves that AddExtension<T>() at composition-root time lands the tool in
        // the registry. Uses a local mirror of GreetingExtension so this test has
        // no ProjectReference to Dmon.SampleExtension (which uses packed packages).
        // The wire half (ComposedCoreWireTests) proves the external composed process
        // starts cleanly; this in-process half carries the positive-registration
        // guarantee for the composition pattern itself.
        using StringReader stdin = new(string.Empty);
        using StringWriter stdout = new();

        DmonBuiltHost host = DmonHost
            .CreateBuilder([])
            .WithoutTelemetry()
            .WithStdio(stdin, stdout)
            .AddExtension<LocalGreetingExtension>()
            .Build();

        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
        IReadOnlyList<AIFunction> tools = registry.GetAll();

        AIFunction? greetTool = tools.FirstOrDefault(t => t.Name == "greet");
        Assert.NotNull(greetTool);

        IDmonExtension? owner = registry.FindExtension("greet");
        Assert.NotNull(owner);
        Assert.IsType<LocalGreetingExtension>(owner);
    }

    // -------------------------------------------------------------------------
    // (c) Contract types share one identity — single ALC graph (in-process)
    // -------------------------------------------------------------------------

    [Fact]
    public void ComposedHost_ExtensionTypeIdentity_IsSingleALC()
    {
        // Define a test extension inline — its IDmonExtension type comes from
        // the same Dmon.Extensions assembly loaded into the Default ALC.
        // If the host were loading extensions into a separate ALC, the
        // registry would fail to recognise the type. The fact that the tool
        // lands in the registry proves single-identity.

        using StringReader stdin = new(string.Empty);
        using StringWriter stdout = new();

        DmonBuiltHost host = DmonHost
            .CreateBuilder([])
            .WithoutTelemetry()
            .WithStdio(stdin, stdout)
            .AddExtension<InlineTestExtension>()
            .Build();

        IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
        IReadOnlyList<AIFunction> tools = registry.GetAll();

        // The tool registered by InlineTestExtension must be present.
        AIFunction? helloTool = tools.FirstOrDefault(t => t.Name == "hello");
        Assert.NotNull(helloTool);

        // Verify the extension is discoverable by tool name — proves its IDmonExtension
        // is the same type reference the registry operates on (Default ALC, no duplication).
        IDmonExtension? ownerExtension = registry.FindExtension("hello");
        Assert.NotNull(ownerExtension);
        Assert.IsType<InlineTestExtension>(ownerExtension);

        // Confirm the IDmonExtension type seen by the test assembly is identical to
        // the type the host resolved — same assembly identity, same type object.
        Type hostExtensionType = ownerExtension.GetType().GetInterface(typeof(IDmonExtension).FullName!)!;
        Assert.Equal(typeof(IDmonExtension), hostExtensionType);
    }

    // -------------------------------------------------------------------------
    // (d) Pin-drift guard — composition roots must track ProtocolVersion.Current
    // -------------------------------------------------------------------------

    [Fact]
    public void DefaultCoreDmonCs_ContainsCurrentProtocolPin()
    {
        string repoRoot = LocateRepoRoot();
        string path = Path.Combine(repoRoot, "default-core", "Dmon.cs");

        Assert.True(File.Exists(path), $"default-core/Dmon.cs not found at {path}.");

        string content = File.ReadAllText(path);
        string expectedPin = $"dmoncore@{ProtocolVersion.Current}.*";

        Assert.Contains(expectedPin, content, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleComposedCoreDmonCs_ContainsCurrentProtocolPin()
    {
        string repoRoot = LocateRepoRoot();
        string path = Path.Combine(repoRoot, "samples", "Dmon.ComposedCore", "Dmon.cs");

        Assert.True(File.Exists(path), $"samples/Dmon.ComposedCore/Dmon.cs not found at {path}.");

        string content = File.ReadAllText(path);
        string expectedPin = $"dmoncore@{ProtocolVersion.Current}.*";

        Assert.Contains(expectedPin, content, StringComparison.Ordinal);
    }

    private static string LocateRepoRoot()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        return Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));
    }
}

/// <summary>
/// Local mirror of <c>GreetingExtension</c> from <c>Dmon.SampleExtension</c>.
/// Used by the in-process positive-registration test (b-inproc) so that the
/// test assembly has no ProjectReference to the sample package project (which
/// uses packed NuGet references that require a feed to resolve).
/// </summary>
file sealed class LocalGreetingExtension : IDmonExtension
{
    public string Name => "greeting";
    public string Description => "Local test mirror of GreetingExtension.";

    public IEnumerable<AIFunction> Tools =>
    [
        AIFunctionFactory.Create(
            (string name) => $"Hello, {name}!",
            "greet",
            "Returns a greeting for the provided name.")
    ];
}

/// <summary>
/// Minimal test-local extension for the ALC identity test (c).
/// Defined in this assembly, referencing the same Dmon.Extensions loaded by the host.
/// </summary>
file sealed class InlineTestExtension : IDmonExtension
{
    public string Name => "inline-test";
    public string Description => "Test extension for ALC identity verification.";

    public IEnumerable<AIFunction> Tools =>
    [
        AIFunctionFactory.Create(
            (string who) => $"Hello, {who}!",
            "hello",
            "Returns a greeting.")
    ];
}
