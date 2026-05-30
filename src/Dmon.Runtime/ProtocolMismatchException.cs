namespace Dmon.Runtime;

/// <summary>
/// Thrown when the core reports a <c>protocolVersion</c> whose Major.Minor does not
/// match <see cref="Dmon.Protocol.ProtocolVersion.Current"/>.
/// </summary>
public sealed class ProtocolMismatchException : Exception
{
    public string CoreProtocolVersion { get; }
    public string HostProtocolVersion { get; }

    public ProtocolMismatchException(string coreVersion, string hostVersion)
        : base(BuildMessage(coreVersion, hostVersion))
    {
        CoreProtocolVersion = coreVersion;
        HostProtocolVersion = hostVersion;
    }

    private static string BuildMessage(string coreVersion, string hostVersion) =>
        $"Protocol version mismatch: host expects {hostVersion}, core reported {coreVersion}.\n" +
        "The cached core may be stale, or the binary set via --core-path / DMON_CORE_PATH is incompatible.\n" +
        "Clear the stale cached version or set --core-path / DMON_CORE_PATH to a compatible core.";
}
