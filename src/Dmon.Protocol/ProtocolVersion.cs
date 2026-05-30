namespace Dmon.Protocol;

/// <summary>
/// Single source of truth for the wire protocol version.
/// Major.Minor identifies the protocol contract; Patch is not part of compatibility checks.
/// </summary>
public static class ProtocolVersion
{
    public const string Current = "0.1";

    /// <summary>
    /// Parses a 2- or 3-part version string and returns the "Major.Minor" segment.
    /// Returns <see langword="null"/> when <paramref name="version"/> cannot be parsed.
    /// </summary>
    public static string? MajorMinor(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        string[] parts = version.Split('.');
        if (parts.Length < 2)
            return null;

        if (!int.TryParse(parts[0], out _) || !int.TryParse(parts[1], out _))
            return null;

        return $"{parts[0]}.{parts[1]}";
    }
}
