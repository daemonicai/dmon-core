namespace Dmon.Abstractions.Profiles;

/// <summary>
/// An immutable named bundle that defines an agent's identity and operating constraints
/// for a single session. Fixed at session creation; never mutated after resolution.
/// </summary>
/// <param name="Name">
/// The profile identifier (e.g. <c>"coding"</c>). Must be non-empty.
/// </param>
/// <param name="Persona">
/// A free-form system-prompt block injected as the agent's identity and behavioural
/// norms. May be empty for the default persona.
/// </param>
/// <param name="Assets">
/// When <see langword="true"/> an <c>assets/&lt;session_id&gt;/</c> directory is provisioned
/// under the workspace root for this session. Defaults to <see langword="false"/>.
/// </param>
/// <param name="PermissionMode">
/// The permission posture applied for the duration of the session.
/// </param>
public sealed record AgentProfile(
    string Name,
    string Persona,
    bool Assets,
    PermissionMode PermissionMode);
