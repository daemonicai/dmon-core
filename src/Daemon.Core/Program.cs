using System.Diagnostics;
using System.Reflection;
using Daemon.Core;
using Daemon.Core.Rpc;
using Daemon.Core.Telemetry;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Logs to stderr; stdout is the JSONL RPC channel.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// --- OpenTelemetry ---
// Service name reads OTEL_SERVICE_NAME env var, falls back to daemon-core.
string serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? DaemonTelemetry.ServiceName;
string coreVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

// Determine whether an OTLP endpoint is configured.
// If no endpoint and no explicit signal exporters are set, exporters default to none.
string? otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
string? tracesExporter = Environment.GetEnvironmentVariable("OTEL_TRACES_EXPORTER");
string? metricsExporter = Environment.GetEnvironmentVariable("OTEL_METRICS_EXPORTER");
string? logsExporter = Environment.GetEnvironmentVariable("OTEL_LOGS_EXPORTER");

bool hasEndpoint = !string.IsNullOrWhiteSpace(otlpEndpoint);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName, serviceVersion: coreVersion)
        .AddAttributes(new Dictionary<string, object>
        {
            ["process.pid"] = Environment.ProcessId,
            ["host.name"] = Environment.MachineName
        }))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(DaemonTelemetry.Source.Name)
            .AddHttpClientInstrumentation();

        if (hasEndpoint || !string.IsNullOrWhiteSpace(tracesExporter))
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(DaemonTelemetry.Meter.Name)
            .AddRuntimeInstrumentation();

        if (hasEndpoint || !string.IsNullOrWhiteSpace(metricsExporter))
        {
            metrics.AddOtlpExporter();
        }
    });

// Route ILogger output through the OTel Logs pipeline.
// The built-in stderr console logger is retained above; the OTel logger is additive.
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.ParseStateValues = true;

    if (hasEndpoint || !string.IsNullOrWhiteSpace(logsExporter))
    {
        logging.AddOtlpExporter();
    }
});

// IChatClient pipeline (assembled by AddDaemonCore):
//   1. PermissionGateChatClient   ← evaluate policy, prompt/deny
//   2. FunctionInvokingChatClient ← M.E.AI dispatch loop
//   3. actual provider client
builder.Services
    .AddDaemonProviders()
    .AddDaemonAuth()
    .AddDaemonExtensions()
    .AddDaemonCore();

builder.Services.AddHostedService<RpcHostedService>();

IHost host = builder.Build();
host.Run();