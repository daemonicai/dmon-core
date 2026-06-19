using System.Diagnostics.Metrics;

namespace Daemon.Routing;

/// <summary>
/// OpenTelemetry metrics for the <c>Daemon.Routing</c> triage layer.
/// </summary>
public static class DaemonTelemetry
{
    private static readonly Meter _meter = new("Daemon.Routing");

    /// <summary>
    /// Incremented when a turn whose raw classifier scope was <c>"world"</c> is gated
    /// down to <c>"personal"</c> by the confidence threshold (privacy bias).
    /// </summary>
    public static readonly Counter<long> PersonalToWorldMisclassifyCounter =
        _meter.CreateCounter<long>(
            "dmon.triage.misclassify.personal_to_world",
            description: "Number of turns whose raw 'world' scope was overridden to 'personal' by the confidence gate.");

    /// <summary>
    /// Records one personal-to-world misclassification override.
    /// </summary>
    public static void RecordPersonalToWorldMisclassification() =>
        PersonalToWorldMisclassifyCounter.Add(1);
}
