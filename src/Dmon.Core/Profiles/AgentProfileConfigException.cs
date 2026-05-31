namespace Dmon.Core.Profiles;

/// <summary>
/// Thrown when an agent profile configuration is invalid or names an unknown profile.
/// Always carries an actionable message that names the offending profile and explains
/// the fix.
/// </summary>
public sealed class AgentProfileConfigException : Exception
{
    public AgentProfileConfigException(string message) : base(message) { }

    public AgentProfileConfigException(string message, Exception innerException)
        : base(message, innerException) { }
}
