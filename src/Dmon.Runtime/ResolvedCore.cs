namespace Dmon.Runtime;

/// <summary>
/// Describes how to launch the resolved dmoncore binary.
/// </summary>
internal enum LaunchMode
{
    /// <summary>Launch via the apphost executable directly (override / dev tiers).</summary>
    DirectExecutable,

    /// <summary>Launch via <c>dotnet exec dmoncore.dll</c> (prebuilt publish closure).</summary>
    DotnetExec,

    /// <summary>
    /// Launch via <c>dotnet build Dmon.cs</c> (captured, separate process) then
    /// <c>dotnet run Dmon.cs --no-build</c> as the stdio child.
    /// The <see cref="ResolvedCore.Path"/> is the absolute path to the <c>Dmon.cs</c> file.
    /// </summary>
    FileBasedProgram,
}

/// <summary>
/// The result of core discovery: the path to run and the launch mode that applies to it.
/// </summary>
internal sealed record ResolvedCore(string Path, LaunchMode LaunchMode);
