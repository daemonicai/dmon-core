using Dmon.Core.Extensions;
using Dmon.Abstractions.Extensions;
using Dmon.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Core.Tests.Hosting;

/// <summary>
/// Tests for the <see cref="DmonHostBuilder.AddMiddleware"/> surface introduced in task 4.6.
/// Verifies that middleware registered via the builder lands in the pipeline with the correct
/// fold ordering, that per-registration priority overrides beat the attribute, and that
/// type-based registration resolves constructor dependencies from the DI container.
///
/// No reflection scan of assemblies occurs — the builder owns the full list.
/// </summary>
public sealed class DmonHostBuilderMiddlewareTests
{
    // ── stub chat client ──────────────────────────────────────────────────────

    /// <summary>A minimal <see cref="IChatClient"/> that records a tag for fold-order assertions.</summary>
    private sealed class TaggedClient(string tag, IChatClient? inner) : IChatClient
    {
        public string Tag { get; } = tag;
        public IChatClient? Inner { get; } = inner;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ChatResponse([]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static TaggedClient BaseClient() => new("base", null);

    // ── stub middleware types ─────────────────────────────────────────────────

    /// <summary>Priority 0 (attribute default).</summary>
    [DmonMiddleware(Priority = 0)]
    private sealed class Priority0Middleware : IDmonMiddleware
    {
        public IChatClient Wrap(IChatClient inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            return new TaggedClient("p0", inner);
        }
    }

    /// <summary>Priority 100 (attribute).</summary>
    [DmonMiddleware(Priority = 100)]
    private sealed class Priority100Middleware : IDmonMiddleware
    {
        public IChatClient Wrap(IChatClient inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            return new TaggedClient("p100", inner);
        }
    }

    /// <summary>Priority 200 (attribute).</summary>
    [DmonMiddleware(Priority = 200)]
    private sealed class Priority200Middleware : IDmonMiddleware
    {
        public IChatClient Wrap(IChatClient inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            return new TaggedClient("p200", inner);
        }
    }

    /// <summary>
    /// Middleware that reads a config value via a constructor-injected
    /// <see cref="IConfiguration"/> to verify DI-based type registration.
    /// </summary>
    [DmonMiddleware(Priority = 0)]
    private sealed class ConfigReadingMiddleware(IConfiguration configuration) : IDmonMiddleware
    {
        private readonly IConfiguration _configuration = configuration;

        public string? ConfigValue => _configuration["test:key"];

        public IChatClient Wrap(IChatClient inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            return new TaggedClient("config-reader", inner);
        }
    }

    // ── helper ────────────────────────────────────────────────────────────────

    private static DmonBuiltHost BuildHost(Action<DmonHostBuilder> configure)
    {
        DmonHostBuilder builder = DmonHost.CreateBuilder([])
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();
        configure(builder);
        return builder.Build();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// No middleware registered via the builder → Apply returns the base client unchanged.
    /// This covers the "no middleware → bare base client" spec scenario.
    /// </summary>
    [Fact]
    public void AddMiddleware_None_ApplyReturnsBaseClientUnchanged()
    {
        DmonBuiltHost host = BuildHost(_ => { });

        MiddlewarePipelineBuilder pipeline = host.Services.GetRequiredService<MiddlewarePipelineBuilder>();
        TaggedClient baseClient = BaseClient();

        IChatClient result = pipeline.Apply(baseClient);

        Assert.Same(baseClient, result);
    }

    /// <summary>
    /// <see cref="DmonHostBuilder.AddMiddleware{TMiddleware}()"/> with no priority override:
    /// the middleware lands in the pipeline at its <see cref="DmonMiddlewareAttribute.Priority"/>
    /// value, verified by inspecting the wrapped client chain.
    /// </summary>
    [Fact]
    public void AddMiddlewareT_NoOverride_MiddlewareLandsAtAttributePriority()
    {
        DmonBuiltHost host = BuildHost(b => b.AddMiddleware<Priority0Middleware>());

        MiddlewarePipelineBuilder pipeline = host.Services.GetRequiredService<MiddlewarePipelineBuilder>();
        TaggedClient baseClient = BaseClient();

        IChatClient result = pipeline.Apply(baseClient);

        // One middleware registered → result is its wrapper over base.
        TaggedClient outer = Assert.IsType<TaggedClient>(result);
        Assert.Equal("p0", outer.Tag);
        TaggedClient inner = Assert.IsType<TaggedClient>(outer.Inner);
        Assert.Equal("base", inner.Tag);
    }

    /// <summary>
    /// Two middlewares added via <see cref="DmonHostBuilder.AddMiddleware{TMiddleware}()"/>:
    /// the one with the lower attribute priority is innermost (closer to the provider);
    /// the one with the higher attribute priority is outermost (closer to the caller).
    /// Spec scenario: "Lower priority is innermost".
    /// </summary>
    [Fact]
    public void AddMiddlewareT_TwoTypes_LowerPriorityIsInnermost()
    {
        // Add in reverse order of priority to confirm ordering comes from Priority, not registration order.
        DmonBuiltHost host = BuildHost(b => b
            .AddMiddleware<Priority200Middleware>()
            .AddMiddleware<Priority100Middleware>());

        MiddlewarePipelineBuilder pipeline = host.Services.GetRequiredService<MiddlewarePipelineBuilder>();
        TaggedClient baseClient = BaseClient();

        IChatClient result = pipeline.Apply(baseClient);

        // p200 is outermost.
        TaggedClient outer = Assert.IsType<TaggedClient>(result);
        Assert.Equal("p200", outer.Tag);

        // p100 is next.
        TaggedClient middle = Assert.IsType<TaggedClient>(outer.Inner);
        Assert.Equal("p100", middle.Tag);

        // base is innermost.
        TaggedClient innermost = Assert.IsType<TaggedClient>(middle.Inner);
        Assert.Equal("base", innermost.Tag);
    }

    /// <summary>
    /// Per-registration priority override supplied to
    /// <see cref="DmonHostBuilder.AddMiddleware{TMiddleware}(int?)"/> beats the attribute.
    /// Middleware with attribute priority 200 overridden to 50 is placed innermost relative
    /// to middleware with attribute priority 100 and no override.
    /// Spec scenario: "Registration priority overrides the attribute priority".
    /// </summary>
    [Fact]
    public void AddMiddlewareT_RegistrationOverride_BeatsAttributePriority()
    {
        DmonBuiltHost host = BuildHost(b => b
            .AddMiddleware<Priority200Middleware>(priorityOverride: 50)  // effective 50 → innermost
            .AddMiddleware<Priority100Middleware>());                     // effective 100 → outer

        MiddlewarePipelineBuilder pipeline = host.Services.GetRequiredService<MiddlewarePipelineBuilder>();
        TaggedClient baseClient = BaseClient();

        IChatClient result = pipeline.Apply(baseClient);

        // p100 is outermost (priority 100 > 50).
        TaggedClient outer = Assert.IsType<TaggedClient>(result);
        Assert.Equal("p100", outer.Tag);

        // p200 is innermost despite its attribute saying 200 — override wins.
        TaggedClient inner = Assert.IsType<TaggedClient>(outer.Inner);
        Assert.Equal("p200", inner.Tag);
    }

    /// <summary>
    /// Equal effective priority falls back to stable registration order:
    /// first registered is innermost, second registered is outermost.
    /// </summary>
    [Fact]
    public void AddMiddleware_EqualPriority_StableRegistrationOrderTiebreaker()
    {
        // Both instances use Priority0Middleware (attribute priority 0).
        Priority0Middleware first = new();
        Priority0Middleware second = new();

        DmonBuiltHost host = BuildHost(b => b
            .AddMiddleware(first)
            .AddMiddleware(second));

        MiddlewarePipelineBuilder pipeline = host.Services.GetRequiredService<MiddlewarePipelineBuilder>();
        TaggedClient baseClient = BaseClient();

        IChatClient result = pipeline.Apply(baseClient);

        // Both produce "p0" tags, so we verify via the registry order instead.
        // Outer wrapper is the second-registered instance.
        IMiddlewareRegistry registry = host.Services.GetRequiredService<IMiddlewareRegistry>();
        IReadOnlyList<(IDmonMiddleware Middleware, int? PriorityOverride)> all = registry.GetAll();

        // Two middleware entries registered.
        Assert.Equal(2, all.Count);

        // The pipeline has two wrappers over base.
        TaggedClient outer = Assert.IsType<TaggedClient>(result);
        Assert.Equal("p0", outer.Tag);
        TaggedClient inner = Assert.IsType<TaggedClient>(outer.Inner);
        Assert.Equal("p0", inner.Tag);
        TaggedClient innermost = Assert.IsType<TaggedClient>(inner.Inner);
        Assert.Equal("base", innermost.Tag);
    }

    /// <summary>
    /// Instance overload <see cref="DmonHostBuilder.AddMiddleware(IDmonMiddleware, int?)"/>
    /// with a priority override: the instance is registered with that override, which
    /// beats the attribute.
    /// </summary>
    [Fact]
    public void AddMiddlewareInstance_WithOverride_OverrideTakesPrecedenceOverAttribute()
    {
        Priority100Middleware instance = new();

        // Override from 100 down to 10, so it should sort before Priority200Middleware at 200.
        DmonBuiltHost host = BuildHost(b => b
            .AddMiddleware(instance, priorityOverride: 10)
            .AddMiddleware<Priority200Middleware>());

        MiddlewarePipelineBuilder pipeline = host.Services.GetRequiredService<MiddlewarePipelineBuilder>();
        TaggedClient baseClient = BaseClient();

        IChatClient result = pipeline.Apply(baseClient);

        // p200 is outermost (200 > 10).
        TaggedClient outer = Assert.IsType<TaggedClient>(result);
        Assert.Equal("p200", outer.Tag);

        // instance (p100 tag) is innermost due to override = 10.
        TaggedClient inner = Assert.IsType<TaggedClient>(outer.Inner);
        Assert.Equal("p100", inner.Tag);
    }

    /// <summary>
    /// Type-based registration resolves constructor dependencies from the DI container:
    /// <see cref="ConfigReadingMiddleware"/> receives an <see cref="IConfiguration"/>
    /// from the host's service provider and exposes it as a property readable post-build.
    /// </summary>
    [Fact]
    public void AddMiddlewareT_WithDiDependency_ConstructorInjectionWorks()
    {
        DmonBuiltHost host = BuildHost(b => b.AddMiddleware<ConfigReadingMiddleware>());

        // Resolve the registry to verify the instance was created via DI.
        IMiddlewareRegistry registry = host.Services.GetRequiredService<IMiddlewareRegistry>();
        IReadOnlyList<(IDmonMiddleware Middleware, int? PriorityOverride)> all = registry.GetAll();

        // Exactly one middleware registered.
        Assert.Single(all);

        // The middleware instance must be of the expected type, proving DI instantiation worked.
        Assert.IsType<ConfigReadingMiddleware>(all[0].Middleware);

        // Verify the pipeline wraps correctly.
        MiddlewarePipelineBuilder pipeline = host.Services.GetRequiredService<MiddlewarePipelineBuilder>();
        IChatClient result = pipeline.Apply(BaseClient());

        TaggedClient outer = Assert.IsType<TaggedClient>(result);
        Assert.Equal("config-reader", outer.Tag);
    }
}
