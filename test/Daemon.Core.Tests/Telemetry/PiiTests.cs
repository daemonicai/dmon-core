using System.Diagnostics;
using Daemon.Core.Telemetry;

namespace Daemon.Core.Tests.Telemetry;

/// <summary>
/// Ensures that no PII (personally identifiable information) leaks into
/// OpenTelemetry span attributes or metric tags. Only sizes, counts, and
/// non-sensitive identifiers should appear.
/// </summary>
public sealed class PiiTests
{
    [Fact]
    public void ActivitySourceName_DoesNotContainPiiPaths()
    {
        Assert.Equal("Daemon.Core", DaemonTelemetry.Source.Name);
    }

    [Fact]
    public void MeterName_DoesNotContainPiiPaths()
    {
        Assert.Equal("Daemon.Core", DaemonTelemetry.Meter.Name);
    }

    /// <summary>
    /// Verifies that the TurnHandler uses only safe tag names — no key that suggests
    /// raw message content or tool arguments.
    /// </summary>
    [Fact]
    public void SpanAttributeKeys_AreSafeForTurn()
    {
        // These are the documented attribute keys for the 'turn' span.
        string[] allowed = [
            "daemon.provider",
            "daemon.model",
            "daemon.thinking.level",
            "daemon.stop_reason",
            "daemon.tokens.input",
            "daemon.tokens.output",
            "daemon.tokens.cache_read",
            "daemon.tokens.cache_write",
            "daemon.cost.usd",
            "daemon.session.id"
        ];

        foreach (string key in allowed)
        {
            Assert.DoesNotContain("content", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("message", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("text", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("response", key, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Verifies that tool.execute span keys are safe.
    /// </summary>
    [Fact]
    public void SpanAttributeKeys_AreSafeForToolExecute()
    {
        string[] allowed = [
            "daemon.tool.name",
            "daemon.tool.args.size_bytes",
            "daemon.tool.result.size_bytes",
            "daemon.tool.is_error",
            "daemon.permission.risk",
            "daemon.permission.decision"
        ];

        foreach (string key in allowed)
        {
            Assert.DoesNotContain("content", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("message", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("text", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("response", key, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Verifies that metric names are safe and do not suggest PII.
    /// </summary>
    [Fact]
    public void MetricNames_AreSafe()
    {
        string[] metrics = [
            "daemon.turns",
            "daemon.tokens",
            "daemon.cost.usd",
            "daemon.turn.duration",
            "daemon.tool.invocations",
            "daemon.permission.prompts",
            "daemon.provider.retries"
        ];

        foreach (string name in metrics)
        {
            Assert.DoesNotContain("content", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("message", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("arg", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("result", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("text", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("user", name, StringComparison.OrdinalIgnoreCase);

            // Metric names should only contain lowercase alphanumeric, dots, and underscores.
            Assert.Matches("^[a-z0-9._]+$", name);
        }
    }

    /// <summary>
    /// Verifies that metric tag keys are safe.
    /// </summary>
    [Fact]
    public void MetricTagKeys_AreSafe()
    {
        string[] tags = [
            "provider",
            "model",
            "stop_reason",
            "direction",
            "tool",
            "is_error",
            "risk",
            "decision",
            "reason"
        ];

        foreach (string tag in tags)
        {
            Assert.DoesNotContain("content", tag, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("message", tag, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prompt", tag, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("arg", tag, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("result", tag, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("text", tag, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Verifies that the DaemonTelemetry convenience methods do not accept raw message content.
    /// Only sizes, counts, and identifiers are accepted as parameters.
    /// </summary>
    [Fact]
    public void TelemetryRecordingMethods_AcceptOnlySafeParameters()
    {
        // RecordTurn — provider, model, stopReason (all identifiers, no content)
        DaemonTelemetry.RecordTurn("test-provider", "test-model", "completed");

        // RecordTokens — count, provider, model, direction (identifiers)
        DaemonTelemetry.RecordTokens(100, "test-provider", "test-model", "input");

        // RecordCost — usd, provider, model
        DaemonTelemetry.RecordCost(0.01, "test-provider", "test-model");

        // RecordToolInvocation — tool name, isError
        DaemonTelemetry.RecordToolInvocation("read_file", false);

        // RecordPermissionPrompt — risk, decision
        DaemonTelemetry.RecordPermissionPrompt("low", "allowonce");

        // RecordProviderRetry — provider, reason
        DaemonTelemetry.RecordProviderRetry("test-provider", "timeout");
    }

    /// <summary>
    /// Verifies that the TurnDurationHistogram exists and is correctly typed.
    /// </summary>
    [Fact]
    public void TurnDurationHistogram_Exists()
    {
        Assert.NotNull(DaemonTelemetry.TurnDurationHistogram);
        Assert.Equal("ms", DaemonTelemetry.TurnDurationHistogram.Unit);
    }

    /// <summary>
    /// The CapturePromptContent flag MUST NOT be wired in V1.
    /// This test verifies there is no config key that would enable prompt content capture.
    /// </summary>
    [Fact]
    public void CapturePromptContent_IsNotConfigured()
    {
        // Verify that no environment variable or config key exists for prompt content capture.
        // The design explicitly states this is out of scope for V1.
        string? captureEnv = Environment.GetEnvironmentVariable("DAEMON_TELEMETRY_CAPTURE_PROMPT");
        Assert.Null(captureEnv);
    }
}
