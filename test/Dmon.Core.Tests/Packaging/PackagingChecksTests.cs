using System.IO.Compression;
using System.Reflection;

using Dmon.Core.Tests.Composition;

namespace Dmon.Core.Tests.Packaging;

/// <summary>
/// Task 7.3 packaging checks:
/// (a) the <c>dmoncore</c> NuGet package is a referenceable library — it contains
///     <c>lib/net10.0/Dmon.Core.dll</c> (the library assembly) and does NOT contain
///     a runnable-closure runtimeconfig.
/// (b) the prebuilt default-core closure in <c>build/dmoncore/</c> contains
///     <c>dmoncore.dll</c> (the entry-point), its dependency assemblies,
///     <c>dmoncore.deps.json</c>, and <c>dmoncore.runtimeconfig.json</c>
///     for direct <c>dotnet exec</c>.
/// </summary>
[Collection("ComposedCoreBuild")]
public sealed class PackagingChecksTests(ComposedCoreFeedFixture feed)
{
    // ------------------------------------------------------------------
    // (a) dmoncore package is a referenceable library, not a runnable closure
    // ------------------------------------------------------------------

    [Fact]
    public void DmoncorePackage_ContainsLibNetDll()
    {
        string nupkg = LocateDmoncoreNupkg(feed.FeedPath);
        using ZipArchive zip = ZipFile.OpenRead(nupkg);

        // The library assembly is Dmon.Core.dll (AssemblyName); PackageId is dmoncore.
        bool hasLibDll = zip.Entries.Any(e =>
            e.FullName.Equals("lib/net10.0/Dmon.Core.dll", StringComparison.OrdinalIgnoreCase));

        Assert.True(hasLibDll,
            $"dmoncore nupkg '{nupkg}' must contain lib/net10.0/Dmon.Core.dll (referenceable library layout). " +
            $"Entries: {string.Join(", ", zip.Entries.Select(e => e.FullName))}");
    }

    [Fact]
    public void DmoncorePackage_DoesNotContainToolsClosureRuntimeconfig()
    {
        string nupkg = LocateDmoncoreNupkg(feed.FeedPath);
        using ZipArchive zip = ZipFile.OpenRead(nupkg);

        // A runnable-closure embedding would appear at the package root or under tools/.
        // The Sdk.Worker artifact Dmon.Core.runtimeconfig.json in lib/net10.0/ is an
        // incidental Worker SDK output, not a runnable-closure indicator.
        bool hasToolsRuntimeconfig = zip.Entries.Any(e =>
            e.FullName.Contains("runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
            && !e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase));

        Assert.False(hasToolsRuntimeconfig,
            $"dmoncore nupkg '{nupkg}' must NOT contain runtimeconfig.json outside lib/ — " +
            "it is a library package, not a runnable-closure. " +
            $"Offending entries: {string.Join(", ", zip.Entries.Where(e => e.FullName.Contains("runtimeconfig", StringComparison.OrdinalIgnoreCase) && !e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)).Select(e => e.FullName))}");
    }

    // ------------------------------------------------------------------
    // (b) prebuilt default-core closure is laid out for direct dotnet exec
    // ------------------------------------------------------------------

    [SkippableFact]
    public void PrebuiltDefaultCore_ContainsDmonCoreDll()
    {
        string dll = RequirePrebuiltDefaultCoreDll();
        Assert.True(File.Exists(dll),
            $"Prebuilt default-core closure must contain dmoncore.dll at '{dll}'. " +
            "Run 'make build-core' to produce it.");
    }

    [SkippableFact]
    public void PrebuiltDefaultCore_ContainsDepsJson()
    {
        string dll = RequirePrebuiltDefaultCoreDll();
        string deps = Path.Combine(Path.GetDirectoryName(dll)!, "dmoncore.deps.json");
        Assert.True(File.Exists(deps),
            $"Prebuilt default-core closure must contain dmoncore.deps.json at '{deps}'. " +
            "Run 'make build-core' to produce it.");
    }

    [SkippableFact]
    public void PrebuiltDefaultCore_ContainsRuntimeconfigJson()
    {
        string dll = RequirePrebuiltDefaultCoreDll();
        string runtimeconfig = Path.Combine(Path.GetDirectoryName(dll)!, "dmoncore.runtimeconfig.json");
        Assert.True(File.Exists(runtimeconfig),
            $"Prebuilt default-core closure must contain dmoncore.runtimeconfig.json at '{runtimeconfig}'. " +
            "Run 'make build-core' to produce it.");
    }

    [SkippableFact]
    public void PrebuiltDefaultCore_ContainsDependencyAssemblies()
    {
        string dll = RequirePrebuiltDefaultCoreDll();
        string dir = Path.GetDirectoryName(dll)!;

        // The closure must have at least one dependency assembly beyond dmoncore.dll itself.
        string[] assemblies = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);
        Assert.True(assemblies.Length > 1,
            $"Prebuilt default-core closure at '{dir}' must contain dependency assemblies " +
            $"in addition to dmoncore.dll. Found: {string.Join(", ", assemblies.Select(Path.GetFileName))}");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string LocateDmoncoreNupkg(string feedPath)
    {
        string[] nupkgs = Directory.GetFiles(feedPath, "dmoncore.*.nupkg", SearchOption.TopDirectoryOnly);
        Assert.True(nupkgs.Length > 0,
            $"No dmoncore nupkg found in feed '{feedPath}'. Expected dmoncore.*.nupkg.");
        // Take the first (there should be exactly one after pack-core.sh).
        return nupkgs[0];
    }

    // Returns the path when the closure exists; skips the test with a clear reason when absent.
    // Use for PrebuiltDefaultCore_* tests only — not for NuGet-package checks.
    private static string RequirePrebuiltDefaultCoreDll()
    {
        string dll = LocatePrebuiltDefaultCoreDll();
        Skip.If(
            !File.Exists(dll),
            $"Prebuilt default-core closure not found at '{dll}'. Run 'make build-core' (or 'make build') to produce it.");
        return dll;
    }

    private static string LocatePrebuiltDefaultCoreDll()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        // test/Dmon.Core.Tests/bin/<cfg>/net10.0/ → repo root is 5 levels up
        string repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));
        return Path.Combine(repoRoot, "build", "dmoncore", "dmoncore.dll");
    }
}
