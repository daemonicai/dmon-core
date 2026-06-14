namespace Dmon.Runtime;

/// <summary>
/// Resolves the dmoncore binary through the fixed discovery precedence:
/// (1) <c>Dmon.cs</c> in the working directory (build + run),
/// (2) <c>--core-path</c> / <c>DMON_CORE_PATH</c> explicit prebuilt override,
/// (3) built-in prebuilt default (published sibling or <c>build/dmoncore/</c> dev layout).
/// No NuGet-cache or on-demand acquisition tier.
/// </summary>
internal sealed class CoreResolver
{
    /// <summary>
    /// Resolves the core, returning a <see cref="ResolvedCore"/> that describes
    /// the path and the launch mode to use.
    /// </summary>
    /// <param name="workingDirectory">
    /// Directory to search for <c>Dmon.cs</c> (tier 1). Defaults to <see cref="Directory.GetCurrentDirectory"/>.
    /// </param>
    /// <param name="corePathOverride">
    /// Value of the <c>--core-path</c> CLI argument, or <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal Task<ResolvedCore> ResolveAsync(
        string? workingDirectory,
        string? corePathOverride,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string cwd = workingDirectory ?? Directory.GetCurrentDirectory();

        // Tier 1: Dmon.cs in the working directory — build + run.
        string dmonCs = Path.Combine(cwd, "Dmon.cs");
        if (File.Exists(dmonCs))
            return Task.FromResult(new ResolvedCore(Path.GetFullPath(dmonCs), LaunchMode.FileBasedProgram));

        // Tier 2: --core-path explicit override or DMON_CORE_PATH env var.
        // A .dll override is exec'd via `dotnet exec`; any other path is treated as a
        // non-.dll executable (dev/escape-hatch) and launched directly.
        if (!string.IsNullOrEmpty(corePathOverride) && File.Exists(corePathOverride))
            return Task.FromResult(new ResolvedCore(
                Path.GetFullPath(corePathOverride),
                OverrideLaunchMode(corePathOverride)));

        string? envPath = Environment.GetEnvironmentVariable("DMON_CORE_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return Task.FromResult(new ResolvedCore(
                Path.GetFullPath(envPath),
                OverrideLaunchMode(envPath)));

        // Tier 3: built-in prebuilt default — always dotnet exec the .dll.
        // Published layout: dmoncore/ directory sits next to the dmon executable.
        string entryDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetEntryAssembly()?.Location) ?? ".";

        string publishedDll = Path.Combine(entryDir, "dmoncore", "dmoncore.dll");
        if (File.Exists(publishedDll))
            return Task.FromResult(new ResolvedCore(Path.GetFullPath(publishedDll), LaunchMode.DotnetExec));

        // Dev layout: build/dmoncore/dmoncore.dll (produced by `make build-core`).
        // The entry assembly sits 5 levels deep (e.g. src/Dmon.Terminal/bin/<cfg>/net10.0/),
        // so repo root is ../../../../../ from there.
        string repoRoot = Path.GetFullPath(Path.Combine(entryDir, "../../../../../"));
        string devDll = Path.Combine(repoRoot, "build/dmoncore/dmoncore.dll");
        if (File.Exists(devDll))
            return Task.FromResult(new ResolvedCore(Path.GetFullPath(devDll), LaunchMode.DotnetExec));

        throw new CoreAcquisitionException(
            "Could not find dmoncore. " +
            "Add a Dmon.cs composition root to the working directory, run 'make build' to produce the published layout, " +
            "or set DMON_CORE_PATH / pass --core-path pointing at a prebuilt dmoncore binary.");
    }

    // A .dll path is always exec'd via `dotnet exec`; anything else (non-.dll escape hatch) runs directly.
    private static LaunchMode OverrideLaunchMode(string path) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? LaunchMode.DotnetExec
            : LaunchMode.DirectExecutable;
}
