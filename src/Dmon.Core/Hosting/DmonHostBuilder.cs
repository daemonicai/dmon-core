using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Profiles;
using Dmon.Abstractions.Providers;
using Dmon.Core;
using Dmon.Core.Extensions;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using NetEscapades.Configuration.Yaml;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Dmon.Hosting;

/// <summary>
/// Fluent builder for a <see cref="DmonBuiltHost"/>. Obtained via
/// <see cref="DmonHost.CreateBuilder(string[])"/> or <see cref="DmonHost.CreateBuilder()"/>.
/// Implements <see cref="IDmonHostBuilder"/> so self-type generic verb extension methods
/// in <c>Dmon.Abstractions</c> (namespace <c>Dmon.Hosting</c>) chain correctly when
/// called on the builder.
/// </summary>
public sealed class DmonHostBuilder : IDmonHostBuilder
{
    private readonly HostApplicationBuilder _appBuilder;
    private bool _telemetryEnabled = true;

    // The stdio streams may be injected before Build() via WithStdio.
    private TextWriter? _stdout;
    private TextReader? _stdin;

    // Profile override — left intact; Group 7 removes the profile subsystem.
    private string? _profileOverride;

    // Permission-mode override — preserved as per Group-3 scope.
    private PermissionMode? _permissionModeOverride;

    internal DmonHostBuilder(string[] args)
    {
        _appBuilder = Host.CreateApplicationBuilder(args);

        string cwd = Directory.GetCurrentDirectory();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Layer precedence (last wins): global < project < local.
        _appBuilder.Configuration.AddYamlFile(
            Path.Combine(home, ".dmon", "config.yaml"), optional: true);
        _appBuilder.Configuration.AddYamlFile(
            Path.Combine(cwd, ".dmon", "config.yaml"), optional: true);
        _appBuilder.Configuration.AddYamlFile(
            Path.Combine(cwd, ".dmon", "config.local.yaml"), optional: true);
    }

    // ── IDmonHostBuilder (facet surface) ─────────────────────────────────────

    /// <summary>
    /// Gets the service collection. Verb extension methods in <c>Dmon.Hosting</c>
    /// call <c>Services.AddSingleton</c> here; <see cref="Build"/> enumerates the
    /// registered instances for DI-discovery.
    /// </summary>
    public IServiceCollection Services => _appBuilder.Services;

    /// <summary>
    /// Gets the configuration manager. <see cref="DmonRegistrationExtensions.UseModel{T}"/>
    /// writes to this; <see cref="ConfigureConfiguration"/> callbacks also receive it.
    /// </summary>
    public IConfigurationManager Configuration => _appBuilder.Configuration;

    // ── Convenience methods (concrete DmonHostBuilder return type) ────────────
    // These parallel the Dmon.Abstractions extension methods but are declared here
    // so callers with DmonHostBuilder in scope use single-type-arg syntax and get
    // DmonHostBuilder back (not the facet interface) for unbroken chaining.

    /// <summary>
    /// Registers <typeparamref name="TExtension"/> as an <see cref="IToolExtension"/>
    /// singleton via DI-discovery. The instance is DI-constructed at build time.
    /// </summary>
    public DmonHostBuilder AddToolExtension<TExtension>()
        where TExtension : class, IToolExtension
    {
        Services.AddSingleton<IToolExtension, TExtension>();
        return this;
    }

    /// <summary>
    /// Registers a pre-constructed <see cref="IToolExtension"/> instance via DI-discovery.
    /// </summary>
    public DmonHostBuilder AddToolExtension(IToolExtension extension)
    {
        Services.AddSingleton<IToolExtension>(extension);
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="TMiddleware"/> via DI-discovery. An optional
    /// <paramref name="priorityOverride"/> beats the <see cref="DmonMiddlewareAttribute"/> priority.
    /// </summary>
    public DmonHostBuilder AddMiddleware<TMiddleware>(int? priorityOverride = null)
        where TMiddleware : class, IDmonMiddleware
    {
        Services.AddSingleton<MiddlewareRegistration>(sp =>
            new MiddlewareRegistration(
                ActivatorUtilities.CreateInstance<TMiddleware>(sp),
                priorityOverride));
        return this;
    }

    /// <summary>
    /// Registers a pre-constructed <see cref="IDmonMiddleware"/> instance via DI-discovery.
    /// An optional <paramref name="priorityOverride"/> beats the <see cref="DmonMiddlewareAttribute"/> priority.
    /// </summary>
    public DmonHostBuilder AddMiddleware(IDmonMiddleware middleware, int? priorityOverride = null)
    {
        Services.AddSingleton<MiddlewareRegistration>(
            new MiddlewareRegistration(middleware, priorityOverride));
        return this;
    }

    // ── Host-level verbs (return DmonHostBuilder) ─────────────────────────────

    /// <summary>
    /// Sets the active provider and model. Equivalent to
    /// <see cref="DmonRegistrationExtensions.UseModel{T}"/> but returns
    /// <see cref="DmonHostBuilder"/> for unbroken concrete-type chaining.
    /// </summary>
    public DmonHostBuilder UseModel(string provider, string modelId)
    {
        _appBuilder.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("activeModel", $"{provider}/{modelId}"),
        ]);
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
    /// Registers a callback that can read or further configure the merged configuration.
    /// Runs immediately against the live <see cref="IConfigurationManager"/>.
    /// </summary>
    /// <remarks>
    /// Precedence (last wins): YAML layers (global &lt; project &lt; local) &lt;
    /// <c>UseModel</c> in-memory override &lt; <c>ConfigureConfiguration</c> callbacks.
    /// </remarks>
    public DmonHostBuilder ConfigureConfiguration(Action<IConfigurationManager> configure)
    {
        configure(_appBuilder.Configuration);
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
    /// Overrides the agent profile name. Left intact; Group 7 removes the profile subsystem.
    /// </summary>
    public DmonHostBuilder WithProfile(string profileName)
    {
        _profileOverride = profileName;
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the host, wiring configuration, providers, extensions, and the
    /// JSONL/stdio RPC loop. Does not start the loop — call
    /// <see cref="DmonBuiltHost.RunAsync(CancellationToken)"/> for that.
    /// </summary>
    public DmonBuiltHost Build()
    {
        ConfigureLogging(_appBuilder);
        if (_telemetryEnabled)
        {
            ConfigureOpenTelemetry(_appBuilder);
        }

        // Register stdio streams before AddDmonCore so TryAdd in AddDmonCore yields to ours.
        TextWriter stdout = _stdout ?? Console.Out;
        TextReader stdin = _stdin ?? Console.In;
        _appBuilder.Services.AddSingleton<TextWriter>(_ => stdout);
        _appBuilder.Services.AddSingleton<TextReader>(_ => stdin);

        _appBuilder.Services
            .AddDmonProviders()
            .AddDmonAuth()
            .AddDmonExtensions()
            .AddDmonCore();

        // Profile override: replace the IAgentProfileResolver with a decorator that substitutes
        // the builder-supplied profile name. Left intact; Group 7 removes the profile subsystem.
        if (_profileOverride is not null)
        {
            ServiceDescriptor? profileInner = _appBuilder.Services
                .LastOrDefault(d => d.ServiceType == typeof(IAgentProfileResolver));
            if (profileInner is not null)
            {
                string profileOverride = _profileOverride;
                _appBuilder.Services.AddSingleton<IAgentProfileResolver>(sp =>
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
            ServiceDescriptor? inner = _appBuilder.Services
                .LastOrDefault(d => d.ServiceType == typeof(IAgentProfileResolver));
            if (inner is not null)
            {
                _appBuilder.Services.AddSingleton<IAgentProfileResolver>(sp =>
                {
                    IAgentProfileResolver innerResolver = inner.ImplementationFactory is not null
                        ? (IAgentProfileResolver)inner.ImplementationFactory(sp)
                        : (IAgentProfileResolver)ActivatorUtilities.CreateInstance(sp, inner.ImplementationType!);
                    return new PermissionModeOverrideResolver(innerResolver, overrideMode);
                });
            }
        }

        _appBuilder.Services.AddHostedService<RpcHostedService>();

        IHost host = _appBuilder.Build();

        // DI-discovery: enumerate MiddlewareRegistration descriptors and route into IMiddlewareRegistry.
        // Replaces the old post-build manual loop.
        IMiddlewareRegistry mwRegistry = host.Services.GetRequiredService<IMiddlewareRegistry>();
        foreach (MiddlewareRegistration descriptor in host.Services.GetServices<MiddlewareRegistration>())
        {
            mwRegistry.Register([descriptor.Middleware], descriptor.PriorityOverride);
        }

        // DI-discovery: enumerate IToolExtension singletons registered via AddToolExtension
        // and route into IToolRegistry. Builtin tools are NOT registered here — they are
        // handled by BuiltinToolsInitializer (IHostedService) which runs during host.StartAsync.
        IToolRegistry toolRegistry = host.Services.GetRequiredService<IToolRegistry>();
        foreach (IToolExtension ext in host.Services.GetServices<IToolExtension>())
        {
            toolRegistry.Register(ext.Name, ext, ext.Tools);
        }

        // DI-discovery: enumerate IProviderExtension singletons registered via AddProvider
        // and route into IProviderRegistry (gated by IsApplicable).
        // No IProviderExtension is registered by the stock core (Group 4 adds provider packages),
        // so this is a no-op for now; existing provider behaviour is unchanged.
        IProviderRegistry providerRegistry = host.Services.GetRequiredService<IProviderRegistry>();
        foreach (IProviderExtension providerExt in host.Services.GetServices<IProviderExtension>())
        {
            if (providerExt.IsApplicable())
            {
                // RegisterExtensionAsync is async; Build() is synchronous. Since this runs
                // before the host loop starts and no concurrent access exists, blocking is safe.
                providerRegistry.RegisterExtensionAsync(providerExt).GetAwaiter().GetResult();
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
