using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Dmon.Memory.Index;

/// <summary>
/// Resolves and loads the sqlite-vec <c>vec0</c> loadable extension into an open
/// <see cref="SqliteConnection"/>.
///
/// Resolution order (first existing path wins):
/// 1. <c>runtimes/&lt;rid&gt;/native/</c> under the assembly directory — the primary
///    location for a plain framework-dependent <c>dotnet build</c> / <c>dotnet test</c>
///    output (HiraokaHyperTools.sqlite-vec 0.1.9 NuGet layout).
/// 2. The same RID-qualified sub-tree under the current working directory.
/// 3. <c>runtimes/&lt;computed-rid&gt;/native/</c> using a &lt;os&gt;-&lt;arch&gt; RID
///    (fallback when <see cref="RuntimeInformation.RuntimeIdentifier"/> is a portable
///    or non-canonical form like <c>osx</c>).
/// 4. Any <c>runtimes/*/native/</c> directory under the assembly dir that contains
///    a matching file name (enumeration fallback — covers extra RIDs shipped by the
///    package without explicit probing logic).
/// 5. The assembly directory itself (self-contained publish flattens the native asset
///    alongside the DLL).
/// 6. The current working directory.
///
/// Fail-fast: if the loadable is not found at any candidate path, an
/// <see cref="InvalidOperationException"/> is thrown that names the RID, the file name
/// expected, and every path that was checked.
/// </summary>
internal static class SqliteVecLoader
{
    // RID → native asset file name, matching HiraokaHyperTools.sqlite-vec 0.1.9
    // runtimes/<rid>/native/ layout.
    private static string LoadableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "vec0.dylib" :
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "vec0.dll"   :
        "vec0.so";

    // Canonical <os>-<arch> RID derived from RuntimeInformation, as a fallback when
    // RuntimeIdentifier is a portable or shortened form.
    private static string ComputedRid
    {
        get
        {
            string os =
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "osx"   :
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows)  ? "win"   :
                "linux";

            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64   => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86   => "x86",
                _                  => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
            };

            return $"{os}-{arch}";
        }
    }

    /// <summary>
    /// Enables extension loading and loads <c>vec0</c> into <paramref name="connection"/>.
    /// Must be called immediately after opening the connection, before any DDL.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown with an actionable message when the loadable is not found.
    /// </exception>
    internal static void LoadVec0(SqliteConnection connection)
    {
        string loadableName = LoadableName;
        string resolvedPath = Resolve(loadableName);

        connection.EnableExtensions(true);
        connection.LoadExtension(resolvedPath, proc: null);
    }

    private static string Resolve(string loadableName)
    {
        List<string> candidates = BuildCandidates(loadableName);

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        string rid = RuntimeInformation.RuntimeIdentifier;
        string checkedPaths = string.Join(Environment.NewLine + "  ", candidates);
        throw new InvalidOperationException(
            $"sqlite-vec loadable '{loadableName}' not found for RID '{rid}' " +
            $"(computed: '{ComputedRid}'). " +
            $"Ensure the HiraokaHyperTools.sqlite-vec 0.1.9 NuGet package is " +
            $"referenced. Under a plain 'dotnet build' or 'dotnet test' the loadable " +
            $"is expected at runtimes/<rid>/native/ relative to the output directory. " +
            $"Checked:{Environment.NewLine}  {checkedPaths}");
    }

    private static List<string> BuildCandidates(string loadableName)
    {
        string assemblyDir = AppContext.BaseDirectory;
        string cwd = Directory.GetCurrentDirectory();
        string rid = RuntimeInformation.RuntimeIdentifier;
        string computedRid = ComputedRid;

        List<string> candidates = [];

        // Primary: runtimes/<rid>/native/ under assembly dir (plain dotnet build/test layout).
        candidates.Add(Path.Combine(assemblyDir, "runtimes", rid,         "native", loadableName));
        candidates.Add(Path.Combine(assemblyDir, "runtimes", computedRid, "native", loadableName));

        // Same RID-qualified paths under cwd.
        candidates.Add(Path.Combine(cwd, "runtimes", rid,         "native", loadableName));
        candidates.Add(Path.Combine(cwd, "runtimes", computedRid, "native", loadableName));

        // Enumeration fallback: scan all runtimes/<any>/native/ dirs under assemblyDir.
        // Covers extra RIDs shipped by the package that aren't matched by the probes above.
        string runtimesRoot = Path.Combine(assemblyDir, "runtimes");
        if (Directory.Exists(runtimesRoot))
        {
            foreach (string ridDir in Directory.EnumerateDirectories(runtimesRoot))
            {
                string nativePath = Path.Combine(ridDir, "native", loadableName);
                // Add only if not already in the list (avoid duplicates from the computed-RID hit).
                if (!candidates.Contains(nativePath, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(nativePath);
            }
        }

        // Flat fallbacks: self-contained publish flattens the native asset next to the DLL.
        candidates.Add(Path.Combine(assemblyDir, loadableName));
        candidates.Add(Path.Combine(cwd,         loadableName));

        return candidates;
    }
}
