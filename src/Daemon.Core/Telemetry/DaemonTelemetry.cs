using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Daemon.Core.Telemetry;

/// <summary>
/// Central registry for OpenTelemetry activity sources and metrics used by Daemon.Core.
/// </summary>
public static class DaemonTelemetry
{
    public const string ServiceName = "daemon-core";

    public static readonly ActivitySource Source = new("Daemon.Core");
    public static readonly Meter Meter = new("Daemon.Core");

    // --- Counters ---

    public static readonly Counter<long> TurnsCounter = Meter.CreateCounter<long>(
        "daemon.turns",
        description: "Number of turns executed.");

    public static readonly Counter<long> TokensCounter = Meter.CreateCounter<long>(
        "daemon.tokens",
        description: "Token usage counts by direction.");

    public static readonly Counter<double> CostUsdCounter = Meter.CreateCounter<double>(
        "daemon.cost.usd",
        description: "Estimated cost in USD.");

    public static readonly Counter<long> ToolInvocationsCounter = Meter.CreateCounter<long>(
        "daemon.tool.invocations",
        description: "Number of tool invocations.");

    public static readonly Counter<long> PermissionPromptsCounter = Meter.CreateCounter<long>(
        "daemon.permission.prompts",
        description: "Number of permission prompts presented.");

    public static readonly Counter<long> ProviderRetriesCounter = Meter.CreateCounter<long>(
        "daemon.provider.retries",
        description: "Number of provider call retries.");

    // --- Histograms ---

    public static readonly Histogram<double> TurnDurationHistogram = Meter.CreateHistogram<double>(
        "daemon.turn.duration",
        unit: "ms",
        description: "Turn duration in milliseconds.");

    // --- Convenience recording methods ---

    /// <summary>
    /// Records a completed turn metric.
    /// </summary>
    public static void RecordTurn(string provider, string model, string stopReason)
    {
        TurnsCounter.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("stop_reason", stopReason));
    }

    /// <summary>
    /// Records a turn duration.
    /// </summary>
    public static void RecordTurnDuration(double durationMs, string provider, string model, string stopReason)
    {
        TurnDurationHistogram.Record(durationMs,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("stop_reason", stopReason));
    }

    /// <summary>
    /// Records token usage.
    /// </summary>
    public static void RecordTokens(long count, string provider, string model, string direction)
    {
        TokensCounter.Add(count,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("direction", direction));
    }

    /// <summary>
    /// Records estimated cost.
    /// </summary>
    public static void RecordCost(double usd, string provider, string model)
    {
        CostUsdCounter.Add(usd,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", model));
    }

    /// <summary>
    /// Records a tool invocation.
    /// </summary>
    public static void RecordToolInvocation(string tool, bool isError)
    {
        ToolInvocationsCounter.Add(1,
            new KeyValuePair<string, object?>("tool", tool),
            new KeyValuePair<string, object?>("is_error", isError));
    }

    /// <summary>
    /// Records a permission prompt.
    /// </summary>
    public static void RecordPermissionPrompt(string risk, string decision)
    {
        PermissionPromptsCounter.Add(1,
            new KeyValuePair<string, object?>("risk", risk),
            new KeyValuePair<string, object?>("decision", decision));
    }

    /// <summary>
    /// Records a provider retry attempt.
    /// </summary>
    public static void RecordProviderRetry(string provider, string reason)
    {
        ProviderRetriesCounter.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("reason", reason));
    }
}
