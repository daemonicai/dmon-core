using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Dmon.Core.Tests.Packaging;

/// <summary>
/// Task 7.2 tool-pack integration checks.
///
/// (a) <c>dotnet pack</c> on <c>Dmon.Terminal</c> succeeds with <c>build/dmoncore/</c> present.
/// (b) The resulting <c>.nupkg</c> contains the prebuilt default-core closure under
///     <c>tools/net10.0/any/dmoncore/</c> (dmoncore.dll, dmoncore.deps.json,
///     dmoncore.runtimeconfig.json).
/// (c) The tool nuspec declares no <c>&lt;dependency&gt;</c> on dmoncore or Dmon.DefaultCore
///     — the tool carries a file payload, not a package reference.
///
/// Prerequisite: <c>build/dmoncore/dmoncore.dll</c> must exist. Run <c>make build-core</c>
/// (or <c>make build</c>) to produce it before running these tests.
/// </summary>
[Collection("ComposedCoreBuild")]
public sealed class ToolPackTests : IAsyncLifetime
{
    private string? _packOutputDir;
    private string? _nupkgPath;
    private string? _packError;
    // Set only when the prebuilt closure is absent — triggers Assert.Skip rather than Assert.True failure.
    private string? _prerequisiteSkipReason;

    public async Task InitializeAsync()
    {
        string prebuiltDll = LocatePrebuiltDefaultCoreDll();
        if (!File.Exists(prebuiltDll))
        {
            _prerequisiteSkipReason = $"Prebuilt default-core closure not found at '{prebuiltDll}'. " +
                                      "Run 'make build-core' (or 'make build') to produce it.";
            return;
        }

        _packOutputDir = Path.Combine(Path.GetTempPath(), $"dmon-tool-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_packOutputDir);

        string terminalCsproj = LocateTerminalCsproj();

        try
        {
            await RunPackAsync(terminalCsproj, _packOutputDir);
        }
        catch (InvalidOperationException ex)
        {
            _packError = ex.Message;
            return;
        }

        string[] nupkgs = Directory.GetFiles(_packOutputDir, "dmon.*.nupkg", SearchOption.TopDirectoryOnly);
        _nupkgPath = nupkgs.Length > 0 ? nupkgs[0] : null;
    }

    public Task DisposeAsync()
    {
        if (_packOutputDir is not null)
        {
            try { Directory.Delete(_packOutputDir, recursive: true); } catch { /* best effort */ }
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // (a) dotnet pack succeeds with build/dmoncore/ present
    // ------------------------------------------------------------------

    [SkippableFact]
    public void ToolPack_SucceedsWithPrebuiltPayloadPresent()
    {
        Skip.If(_prerequisiteSkipReason is not null, _prerequisiteSkipReason ?? string.Empty);
        Assert.True(_packError is null,
            $"dotnet pack failed: {_packError}");
        Assert.True(_nupkgPath is not null && File.Exists(_nupkgPath),
            $"Expected a dmon.*.nupkg in '{_packOutputDir}' but none was found.");
    }

    // ------------------------------------------------------------------
    // (b) nupkg contains the prebuilt default-core closure
    // ------------------------------------------------------------------

    [SkippableFact]
    public void ToolPack_NupkgContainsDmonCoreDll()
    {
        RequireSuccessfulPack();

        using ZipArchive zip = ZipFile.OpenRead(_nupkgPath!);
        bool hasDll = zip.Entries.Any(e =>
            e.FullName.Equals("tools/net10.0/any/dmoncore/dmoncore.dll", StringComparison.OrdinalIgnoreCase));

        Assert.True(hasDll,
            $"nupkg '{_nupkgPath}' must contain tools/net10.0/any/dmoncore/dmoncore.dll. " +
            $"Tool entries: {string.Join(", ", zip.Entries.Where(e => e.FullName.StartsWith("tools/", StringComparison.OrdinalIgnoreCase)).Select(e => e.FullName))}");
    }

    [SkippableFact]
    public void ToolPack_NupkgContainsDepsJson()
    {
        RequireSuccessfulPack();

        using ZipArchive zip = ZipFile.OpenRead(_nupkgPath!);
        bool hasDeps = zip.Entries.Any(e =>
            e.FullName.Equals("tools/net10.0/any/dmoncore/dmoncore.deps.json", StringComparison.OrdinalIgnoreCase));

        Assert.True(hasDeps,
            $"nupkg '{_nupkgPath}' must contain tools/net10.0/any/dmoncore/dmoncore.deps.json.");
    }

    [SkippableFact]
    public void ToolPack_NupkgContainsRuntimeconfigJson()
    {
        RequireSuccessfulPack();

        using ZipArchive zip = ZipFile.OpenRead(_nupkgPath!);
        bool hasRuntimeconfig = zip.Entries.Any(e =>
            e.FullName.Equals("tools/net10.0/any/dmoncore/dmoncore.runtimeconfig.json", StringComparison.OrdinalIgnoreCase));

        Assert.True(hasRuntimeconfig,
            $"nupkg '{_nupkgPath}' must contain tools/net10.0/any/dmoncore/dmoncore.runtimeconfig.json.");
    }

    // ------------------------------------------------------------------
    // (c) nuspec declares no dependency on dmoncore
    // ------------------------------------------------------------------

    [SkippableFact]
    public void ToolPack_NuspecHasNoDmoncoreDependency()
    {
        RequireSuccessfulPack();

        using ZipArchive zip = ZipFile.OpenRead(_nupkgPath!);
        ZipArchiveEntry? nuspecEntry = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(nuspecEntry);

        using StreamReader reader = new(nuspecEntry!.Open());
        string nuspec = reader.ReadToEnd();

        // No <dependency id="dmoncore" ...> or <dependency id="Dmon.DefaultCore" ...>
        bool hasDmoncoreDep = Regex.IsMatch(
            nuspec,
            @"<dependency\s[^>]*id=""(?:dmoncore|Dmon\.DefaultCore)""",
            RegexOptions.IgnoreCase);

        Assert.False(hasDmoncoreDep,
            $"nuspec must not declare a dependency on dmoncore or Dmon.DefaultCore — " +
            "the tool carries a file payload, not a package reference.\n" +
            $"Nuspec content:\n{nuspec}");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string LocatePrebuiltDefaultCoreDll()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        // test/Dmon.Core.Tests/bin/<cfg>/net10.0/ → repo root is 5 levels up
        string repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));
        return Path.Combine(repoRoot, "build", "dmoncore", "dmoncore.dll");
    }

    private static string LocateTerminalCsproj()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        string repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));
        return Path.Combine(repoRoot, "frontends", "Dmon.Terminal", "Dmon.Terminal.csproj");
    }

    private static async Task RunPackAsync(string csprojPath, string outputDir)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            Arguments = $"pack \"{csprojPath}\" -c Release -o \"{outputDir}\"",
            WorkingDirectory = Path.GetDirectoryName(csprojPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process proc = new() { StartInfo = psi };
        proc.Start();

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("dotnet pack timed out after 3 minutes.");
        }

        string output = await stdoutTask;
        string errors = await stderrTask;

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet pack failed (exit {proc.ExitCode}).\nstdout: {output}\nstderr: {errors}");
        }
    }

    private void RequireSuccessfulPack()
    {
        Skip.If(_prerequisiteSkipReason is not null, _prerequisiteSkipReason ?? string.Empty);
        Assert.True(_packError is null,
            $"dotnet pack failed: {_packError}");
        Assert.True(_nupkgPath is not null && File.Exists(_nupkgPath),
            $"No nupkg found in '{_packOutputDir}'.");
    }
}
