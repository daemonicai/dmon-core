using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Dmon.Core.Telemetry;

/// <summary>
/// Central registry for OpenTelemetry activity sources and metrics used by Dmon.Core.
/// </summary>
public static class DmonTelemetry
{
    public const string ServiceName = "dmon-core";

    public static readonly ActivitySource Source = new("Dmon.Core");
    public static readonly Meter Meter = new("Dmon.Core");

    // --- Counters ---

    public static readonly Counter<long> TurnsCounter = Meter.CreateCounter<long>(
        "dmon.turns",
        description: "Number of turns executed.");

    public static readonly Counter<long> TokensCounter = Meter.CreateCounter<long>(
        "dmon.tokens",
        description: "Token usage counts by direction.");

    public static readonly Counter<double> CostUsdCounter = Meter.CreateCounter<double>(
        "dmon.cost.usd",
        description: "Estimated cost in USD.");

    public static readonly Counter<long> ToolInvocationsCounter = Meter.CreateCounter<long>(
        "dmon.tool.invocations",
        description: "Number of tool invocations.");

    public static readonly Counter<long> PermissionPromptsCounter = Meter.CreateCounter<long>(
        "dmon.permission.prompts",
        description: "Number of permission prompts presented.");

    public static readonly Counter<long> ProviderRetriesCounter = Meter.CreateCounter<long>(
        "dmon.provider.retries",
        description: "Number of provider call retries.");

    // --- Histograms ---

    public static readonly Histogram<double> TurnDurationHistogram = Meter.CreateHistogram<double>(
        "dmon.turn.duration",
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
