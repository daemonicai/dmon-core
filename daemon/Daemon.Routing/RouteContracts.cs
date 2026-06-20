namespace Daemon.Routing;

/// <summary>
/// The structured-output contract that the classifier returns for each turn.
/// </summary>
/// <param name="Scope">
/// Opaque scope label inferred by the classifier (e.g. <c>"personal"</c> or <c>"world"</c>).
/// May be overridden to <c>"personal"</c> by the privacy gate when
/// <see cref="Confidence"/> is below <see cref="TriageOptions.EgressThreshold"/>.
/// </param>
/// <param name="Impersonal">
/// <see langword="true"/> when the turn contains no personal data and can be
/// answered without personal context (egress eligibility guard).
/// </param>
/// <param name="Confidence">
/// Classifier confidence in the <see cref="Scope"/> assignment, in the range [0, 1].
/// </param>
public record RouteDecision(string Scope, bool Impersonal, float Confidence);

/// <summary>
/// Tunable knobs for <see cref="TriageRouter"/>.
/// </summary>
public record TriageOptions
{
    /// <summary>
    /// Minimum classifier confidence required before a <c>"world"</c>-scoped turn may be
    /// dispatched to the egress backend. Turns below this threshold are gated down to
    /// <c>"personal"</c> scope (privacy bias).
    /// </summary>
    public float EgressThreshold { get; init; } = 0.8f;
}
