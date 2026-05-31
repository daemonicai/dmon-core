using Dmon.Abstractions.Profiles;

namespace Dmon.Core.Config;

/// <summary>
/// One entry from the <c>profiles:</c> map in a single <c>config.yaml</c> file.
/// </summary>
/// <param name="Name">
/// The profile name — the map key in YAML (e.g. <c>helper</c>).
/// </param>
/// <param name="Persona">
/// Inline persona text, or <see langword="null"/> when the persona is sourced from a file.
/// </param>
/// <param name="PersonaFile">
/// Absolute path (via <see cref="Path.GetFullPath"/>) resolved relative to the declaring
/// scope's config directory, or <see langword="null"/> when the persona is inline.
/// File existence and contents are validated/read in the resolver.
/// </param>
/// <param name="Assets">
/// Whether a per-session asset directory should be provisioned. Defaults to <see langword="false"/>.
/// </param>
/// <param name="PermissionMode">
/// The permission posture for sessions running under this profile.
/// </param>
/// <remarks>
/// Neither <see cref="Persona"/>/<see cref="PersonaFile"/> mutual-exclusivity nor the
/// <c>sandbox+assets:false</c> incoherence are validated here — both are validated in the
/// resolver (Group 3).
/// </remarks>
public sealed record ProfileConfigEntry(
    string Name,
    string? Persona,
    string? PersonaFile,
    bool Assets,
    PermissionMode PermissionMode);
