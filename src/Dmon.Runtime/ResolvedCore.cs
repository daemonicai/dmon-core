namespace Dmon.Runtime;

/// <summary>
/// Describes how to launch the resolved dmoncore binary.
/// </summary>
internal enum LaunchMode
{
    /// <summary>Launch via the apphost executable directly (override / dev tiers).</summary>
    DirectExecutable,

    /// <summary>Launch via <c>dotnet exec dmoncore.dll</c> (cached NuGet package tier).</summary>
    DotnetExec,
}

/// <summary>
/// The result of core discovery: the path to run and the launch mode that applies to it.
/// </summary>
internal sealed record ResolvedCore(string Path, LaunchMode LaunchMode);
