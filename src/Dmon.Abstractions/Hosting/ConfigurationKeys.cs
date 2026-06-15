namespace Dmon.Hosting;

/// <summary>
/// Shared configuration key names used across the hosting and provider layers.
/// Centralised here so every reader and writer references the same string.
/// </summary>
public static class ConfigurationKeys
{
    /// <summary>The configuration key that stores the active provider/model selection (e.g. <c>gemini/flash</c>).</summary>
    public const string ActiveModel = "activeModel";
}
