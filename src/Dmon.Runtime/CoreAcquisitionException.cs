namespace Dmon.Runtime;

/// <summary>
/// Thrown when dmoncore cannot be resolved or acquired, with an actionable message
/// that names the <c>--core-path</c> / <c>DMON_CORE_PATH</c> offline overrides.
/// </summary>
public sealed class CoreAcquisitionException : Exception
{
    public CoreAcquisitionException(string message)
        : base(message) { }

    public CoreAcquisitionException(string message, Exception innerException)
        : base(message, innerException) { }
}
