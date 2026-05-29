namespace Dmon.Abstractions.Providers;

/// <summary>
/// A provider/model pair expressed as the canonical "{provider}/{model}" string.
/// Model may be null when only the provider has been specified.
/// </summary>
public sealed record ModelRef(string Provider, string? Model)
{
    /// <summary>
    /// Parses a "{provider}/{model}" string.  Never throws.
    /// Returns null when <paramref name="value"/> is null, empty, or has an empty provider segment.
    /// The model segment (everything after the first '/') is taken verbatim — it may contain
    /// additional '/' characters (e.g. "ollama/deepseek/deepseek-v4-pro").
    /// </summary>
    public static ModelRef? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        int slash = trimmed.IndexOf('/', StringComparison.Ordinal);

        if (slash < 0)
        {
            // Provider-only — no model.
            return new ModelRef(trimmed, null);
        }

        string provider = trimmed[..slash];
        if (provider.Length == 0)
        {
            return null;
        }

        string modelRaw = trimmed[(slash + 1)..];
        // Empty segment after slash → treat Model as null.
        string? model = modelRaw.Length == 0 ? null : modelRaw;

        return new ModelRef(provider, model);
    }

    /// <summary>
    /// Returns the canonical "{provider}/{model}" string, or just "{provider}" when Model is null.
    /// Round-trips with <see cref="Parse"/> for any valid ref.
    /// </summary>
    public override string ToString() => Model is null ? Provider : $"{Provider}/{Model}";
}
