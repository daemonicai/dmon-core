using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Profiles;
using Dmon.Core;
using Dmon.Core.Extensions;
using Dmon.Core.Rpc;
using Dmon.Core.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Console;
using NetEscapades.Configuration.Yaml;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Dmon.Hosting;

/// <summary>
/// Fluent builder for a <see cref="DmonBuiltHost"/>. Obtained via
/// <see cref="DmonHost.CreateBuilder(string[])"/>.
/// </summary>
public sealed class DmonHostBuilder
{
    private readonly string[] _args;
    private readonly List<IToolExtension> _extensions = [];
    private readonly List<(Func<IServiceProvider, IDmonMiddleware> Factory, int? PriorityOverride)> _middlewares = [];
    private readonly List<Action<IConfigurationManager>> _configureCallbacks = [];

    private string? _providerOverride;
    private string? _modelOverride;
    private PermissionMode? _permissionModeOverride;
    private string? _profileOverride;
    private TextWriter? _stdout;
    private TextReader? _stdin;
    private bool _telemetryEnabled = true;

    internal DmonHostBuilder(string[] args)
    {
        _args = args;
    }

    /// <summary>
    /// Overrides the active provider/model at startup. Takes precedence over
    /// <c>activeModel</c> in config.
    /// </summary>
    public DmonHostBuilder WithModel(string provider, string modelId)
    {
        _providerOverride = provider;
        _modelOverride = modelId;
        return this;
    }

    /// <summary>
    /// Registers an extension instance whose tools will be available in the
    /// tool registry from startup. Extensions are registered solely via the builder
    /// at compile time — there is no config-declared extension set.
    /// </summary>
    public DmonHostBuilder AddExtension(IToolExtension extension)
    {
        _extensions.Add(extension);
        return this;
    }

    /// <summary>
    /// Registers an extension by type. The type must have a public parameterless constructor.
    /// </summary>
    public DmonHostBuilder AddExtension<TExtension>() where TExtension : IToolExtension, new()
    {
        _extensions.Add(new TExtension());
        return this;
    }

    /// <summary>
    /// Registers a middleware instance in the pipeline. The middleware is folded at its
    /// <see cref="DmonMiddlewareAttribute.Priority"/> value (or 0 if the attribute is absent)
    /// unless overridden by <paramref name="priorityOverride"/> or a config entry.
    /// </summary>
    public DmonHostBuilder AddMiddleware(IDmonMiddleware middleware, int? priorityOverride = null)
    {
        _middlewares.Add((_ => middleware, priorityOverride));
        return this;
    }

    /// <summary>
    /// Registers a middleware by type in the pipeline. The type is instantiated at
    /// <see cref="Build"/> time using the host's <see cref="IServiceProvider"/>, which
    /// satisfies any constructor parameters resolvable from DI; if no matching constructor
    /// is found, instantiation fails at build time with a clear error.
    /// The middleware is folded at its <see cref="DmonMiddlewareAttribute.Priority"/> value
    /// (or 0 if absent) unless overridden by <paramref name="priorityOverride"/> or config.
    /// </summary>
    public DmonHostBuilder AddMiddleware<TMiddleware>(int? priorityOverride = null)
        where TMiddleware : IDmonMiddleware
    {
        _middlewares.Add((sp => (IDmonMiddleware)ActivatorUtilities.CreateInstance<TMiddleware>(sp), priorityOverride));
        return this;
    }

    /// <summary>
    /// Overrides the permission mode for the session. Takes precedence over the
    /// profile's declared <see cref="PermissionMode"/>.
    /// </summary>
    public DmonHostBuilder WithPermissionMode(PermissionMode mode)
    {
        _permissionModeOverride = mode;
        return this;
    }

    /// <summary>
    /// Overrides the agent profile name. Equivalent to specifying a profile at
    /// session creation time, applied at startup before any session is created.
    /// </summary>
    public DmonHostBuilder WithProfile(string profileName)
    {
        _profileOverride = profileName;
        return this;
    }

    /// <summary>
    /// Injects alternative stdin/stdout streams. Used by tests to drive the
    /// JSONL/stdio loop in-process without spawning a child process.
    /// When not called, the host uses <see cref="Console.In"/> and
    /// <see cref="Console.Out"/>.
    /// </summary>
    public DmonHostBuilder WithStdio(TextReader stdin, TextWriter stdout)
    {
        _stdin = stdin;
        _stdout = stdout;
        return this;
    }

    /// <summary>
    /// Disables OpenTelemetry instrumentation. Intended for testing and embedded
    /// scenarios where OTel SDK shutdown latency is unacceptable.
    /// </summary>
    public DmonHostBuilder WithoutTelemetry()
    {
        _telemetryEnabled = false;
        return this;
    }

    /// <summary>
    /// Registers a callback that can read or further configure the merged configuration
    /// after all YAML sources and the <see cref="WithModel"/> in-memory override have been
    /// applied. Multiple calls are invoked in registration order.
    /// </summary>
    /// <remarks>
    /// Precedence (last wins): YAML layers (global &lt; project &lt; local) &lt;
    /// <see cref="WithModel"/> in-memory override &lt; <c>ConfigureConfiguration</c> callbacks.
    /// The callback receives a <see cref="IConfigurationManager"/> which implements both
    /// <see cref="IConfigurationBuilder"/> (add sources) and <see cref="IConfiguration"/>
    /// (read merged values).
    /// </remarks>
    public DmonHostBuilder ConfigureConfiguration(Action<IConfigurationManager> configure)
    {
        _configureCallbacks.Add(configure);
        return this;
    }

    /// <summary>
    /// Builds the host, wiring configuration, providers, extensions, and the
    /// JSONL/stdio RPC loop. Does not start the loop — call
    /// <see cref="DmonBuiltHost.RunAsync(CancellationToken)"/> for that.
    /// </summary>
    public DmonBuiltHost Build()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(_args);

        string cwd = Directory.GetCurrentDirectory();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Layer precedence (last wins): global < project < local.
        builder.Configuration.AddYamlFile(
            Path.Combine(home, ".dmon", "config.yaml"), optional: true);
        builder.Configuration.AddYamlFile(
            Path.Combine(cwd, ".dmon", "config.yaml"), optional: true);
        builder.Configuration.AddYamlFile(
            Path.Combine(cwd, ".dmon", "config.local.yaml"), optional: true);

        ConfigureLogging(builder);
        if (_telemetryEnabled)
        {
            ConfigureOpenTelemetry(builder);
        }

        // Apply model override via in-memory config after YAML sources so it wins.
        // ActiveModelStore reads the scalar key "activeModel" and parses it as "{provider}/{model}".
        if (_providerOverride is not null && _modelOverride is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["activeModel"] = $"{_providerOverride}/{_modelOverride}",
            });
        }

        // Run composition-code callbacks last so they win over both YAML and WithModel.
        foreach (Action<IConfigurationManager> callback in _configureCallbacks)
        {
            callback(builder.Configuration);
        }

        // Register stdio streams before AddDmonCore so TryAdd in AddDmonCore yields to ours.
        TextWriter stdout = _stdout ?? Console.Out;
        TextReader stdin = _stdin ?? Console.In;
        builder.Services.AddSingleton<TextWriter>(_ => stdout);
        builder.Services.AddSingleton<TextReader>(_ => stdin);

        builder.Services
            .AddDmonProviders()
            .AddDmonAuth()
            .AddDmonExtensions()
            .AddDmonCore();

        // Profile override: replace the IAgentProfileResolver with a decorator that substitutes
        // the builder-supplied profile name as the default (used when the caller passes null).
        if (_profileOverride is not null)
        {
            ServiceDescriptor? profileInner = builder.Services
                .LastOrDefault(d => d.ServiceType == typeof(IAgentProfileResolver));
            if (profileInner is not null)
            {
                string profileOverride = _profileOverride;
                builder.Services.AddSingleton<IAgentProfileResolver>(sp =>
                {
                    IAgentProfileResolver innerResolver = profileInner.ImplementationFactory is not null
                        ? (IAgentProfileResolver)profileInner.ImplementationFactory(sp)
                        : (IAgentProfileResolver)ActivatorUtilities.CreateInstance(sp, profileInner.ImplementationType!);
                    return new ProfileOverrideResolver(innerResolver, profileOverride);
                });
            }
        }

        // Permission mode override: replace the IAgentProfileResolver with a decorator
        // that wraps the one registered by AddDmonCore and overrides PermissionMode.
        if (_permissionModeOverride is PermissionMode overrideMode)
        {
            // Capture the descriptor registered by AddDmonCore before adding ours.
            ServiceDescriptor? inner = builder.Services
                .LastOrDefault(d => d.ServiceType == typeof(IAgentProfileResolver));
            if (inner is not null)
            {
                builder.Services.AddSingleton<IAgentProfileResolver>(sp =>
                {
                    IAgentProfileResolver innerResolver = inner.ImplementationFactory is not null
                        ? (IAgentProfileResolver)inner.ImplementationFactory(sp)
                        : (IAgentProfileResolver)ActivatorUtilities.CreateInstance(sp, inner.ImplementationType!);
                    return new PermissionModeOverrideResolver(innerResolver, overrideMode);
                });
            }
        }

        builder.Services.AddHostedService<RpcHostedService>();

        IHost host = builder.Build();

        // Register inline extensions into the tool registry after the container is built.
        // IToolRegistry is a singleton; this is safe before RunAsync starts the loop.
        if (_extensions.Count > 0)
        {
            IToolRegistry registry = host.Services.GetRequiredService<IToolRegistry>();
            foreach (IToolExtension ext in _extensions)
            {
                registry.Register(ext.Name, ext, ext.Tools);
            }
        }

        // Register builder-supplied middleware into the middleware registry after the
        // container is built so that type-based factories can resolve IServiceProvider.
        if (_middlewares.Count > 0)
        {
            IMiddlewareRegistry mwRegistry = host.Services.GetRequiredService<IMiddlewareRegistry>();
            IServiceProvider sp = host.Services;
            foreach ((Func<IServiceProvider, IDmonMiddleware> factory, int? priorityOverride) in _middlewares)
            {
                IDmonMiddleware instance = factory(sp);
                mwRegistry.Register([instance], priorityOverride);
            }
        }

        return new DmonBuiltHost(host);
    }

    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o =>
        {
            o.LogToStandardErrorThreshold = LogLevel.Trace;
            o.FormatterName = ConsoleFormatterNames.Json;
        });
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
    }

    private static void ConfigureOpenTelemetry(HostApplicationBuilder builder)
    {
        string serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
            ?? DmonTelemetry.ServiceName;
        string coreVersion = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "0.0.0";

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
    }
}
