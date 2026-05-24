using System.Diagnostics;
using System.Reflection;
using Dmon.Core;
using Dmon.Core.Rpc;
using Dmon.Core.Telemetry;
using Microsoft.Extensions.Logging;
using NetEscapades.Configuration.Yaml;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Logs to stderr; stdout is the JSONL RPC channel.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Add YAML configuration sources for project-local and user-global dmon config.
// These are optional — core starts without them if .dmon/ hasn't been bootstrapped yet.
string cwd = Directory.GetCurrentDirectory();
string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

builder.Configuration.AddYamlFile(
    Path.Combine(cwd, ".dmon", "config.yaml"), optional: true);
builder.Configuration.AddYamlFile(
    Path.Combine(home, ".dmon", "config.yaml"), optional: true);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
// HttpClient logs request lifecycle at Information; suppress below Warning to avoid
// flooding [core-stderr] with NuGet/GitHub HTTP traffic from built-in tools.
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

// --- OpenTelemetry ---
// Service name reads OTEL_SERVICE_NAME env var, falls back to dmon-core.
string serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? DmonTelemetry.ServiceName;
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
            .AddSource(DmonTelemetry.Source.Name)
            .AddHttpClientInstrumentation();

        if (hasEndpoint || !string.IsNullOrWhiteSpace(tracesExporter))
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(DmonTelemetry.Meter.Name)
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

// IChatClient pipeline (assembled by AddDmonCore):
//   1. PermissionGateChatClient   ← evaluate policy, prompt/deny
//   2. FunctionInvokingChatClient ← M.E.AI dispatch loop
//   3. actual provider client
builder.Services
    .AddDmonProviders()
    .AddDmonAuth()
    .AddDmonExtensions()
    .AddDmonCore();

builder.Services.AddHostedService<RpcHostedService>();

IHost host = builder.Build();
host.Run();