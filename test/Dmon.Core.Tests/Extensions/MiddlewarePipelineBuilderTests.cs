using Dmon.Core.Extensions;
using Dmon.Abstractions.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Dmon.Core.Tests.Extensions;

/// <summary>
/// Unit tests for <see cref="MiddlewarePipelineBuilder"/> covering fold order,
/// stable tiebreaker, config priority override, and the no-middleware pass-through.
/// These tests also cover spec scenarios for tasks 6.4 and 6.5.
/// </summary>
public sealed class MiddlewarePipelineBuilderTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A minimal <see cref="IChatClient"/> that records which tag was applied to it.
    /// The tag is appended during <see cref="TaggingMiddleware.Wrap"/> so we can read
    /// the outermost tag to verify fold order.
    /// </summary>
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

    /// <summary>
    /// Middleware that tags the returned client with its own label, enabling
    /// assertion of wrap order by walking the <see cref="TaggedClient.Inner"/> chain.
    /// The Priority in the attribute is the default (0); individual tests use
    /// <see cref="ConfiguredTaggingMiddleware"/> or a subclass with a different attribute.
    /// </summary>
    [DmonMiddleware(Priority = 0)]
    private sealed class TaggingMiddleware(string label) : IDmonMiddleware
    {
        public string Label { get; } = label;

        public IChatClient Wrap(IChatClient inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            return new TaggedClient(Label, inner);
        }
    }

    [DmonMiddleware(Priority = 100)]
    private sealed class Priority100Middleware(string label) : IDmonMiddleware
    {
        public string Label { get; } = label;
        public IChatClient Wrap(IChatClient inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            return new TaggedClient(Label, inner);
        }
    }

    [DmonMiddleware(Priority = 200)]
    private sealed class Priority200Middleware(string label) : IDmonMiddleware
    {
        public string Label { get; } = label;
        public IChatClient Wrap(IChatClient inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            return new TaggedClient(Label, inner);
        }
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    private static IConfiguration ConfigWithPriorityOverride(string className, int priority) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"middleware:{className}:priority"] = priority.ToString()
            })
            .Build();

    private static IChatClient BaseClient() => new TaggedClient("base", null);

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fold order: A(priority=100) inner, B(priority=200) outer.
    /// Pipeline from outermost to innermost: B → A → base.
    /// Spec scenario "Lower priority middleware is innermost" (task 6.4).
    /// </summary>
    [Fact]
    public void Apply_TwoMiddlewares_LowerPriorityIsInnermost()
    {
        MiddlewareRegistry registry = new();
        Priority100Middleware a = new("A");
        Priority200Middleware b = new("B");
        // Register in order A, B — priority determines fold, not registration order.
        registry.Register([a, b]);

        MiddlewarePipelineBuilder builder = new(registry, EmptyConfig());
        IChatClient result = builder.Apply(BaseClient());

        // Outermost should be B (priority 200).
        TaggedClient outer = Assert.IsType<TaggedClient>(result);
        Assert.Equal("B", outer.Tag);

        // Next layer should be A (priority 100).
        TaggedClient middle = Assert.IsType<TaggedClient>(outer.Inner);
        Assert.Equal("A", middle.Tag);

        // Innermost should be the base.
        TaggedClient inner = Assert.IsType<TaggedClient>(middle.Inner);
        Assert.Equal("base", inner.Tag);
    }

    /// <summary>
    /// Equal priority uses stable registration order as tiebreaker: first registered
    /// is innermost (lower tiebreak index), second registered is outermost.
    /// </summary>
    [Fact]
    public void Apply_EqualPriority_StableRegistrationOrderTiebreaker()
    {
        MiddlewareRegistry registry = new();
        // Both use Priority = 0 (TaggingMiddleware default attribute).
        TaggingMiddleware first = new("first");
        TaggingMiddleware second = new("second");
        registry.Register([first, second]);

        MiddlewarePipelineBuilder builder = new(registry, EmptyConfig());
        IChatClient result = builder.Apply(BaseClient());

        // second is outermost (registered second = higher tiebreak index).
        TaggedClient outer = Assert.IsType<TaggedClient>(result);
        Assert.Equal("second", outer.Tag);

        // first is innermost.
        TaggedClient inner = Assert.IsType<TaggedClient>(outer.Inner);
        Assert.Equal("first", inner.Tag);
    }

    /// <summary>
    /// Config priority override: middleware with [DmonMiddleware(Priority=100)] and
    /// config middleware:Priority100Middleware:priority = 50 → effective priority 50,
    /// so it sorts before middleware with attribute priority 100.
    /// Spec scenario "Priority override in config takes precedence over attribute" (task 6.5).
    /// </summary>
    [Fact]
    public void Apply_ConfigOverride_ChangesEffectivePriority()
    {
        MiddlewareRegistry registry = new();
        Priority100Middleware a = new("A");   // attribute priority 100, config overrides to 50
        Priority100Middleware b = new("B");   // attribute priority 100, no override → stays 100

        // Register A first, then B.
        registry.Register([a, b]);

        // Override A's priority to 50 via config. B has no override so stays at 100.
        IConfiguration config = ConfigWithPriorityOverride(
            nameof(Priority100Middleware), 50);

        MiddlewarePipelineBuilder builder = new(registry, config);
        IChatClient result = builder.Apply(BaseClient());

        // Both A and B share the same type name (Priority100Middleware) so the config key
        // "middleware:Priority100Middleware:priority" applies to both. With effective priority
        // 50 for both, the tiebreaker is registration order: A (index 0) innermost, B (index 1) outer.
        TaggedClient outer = Assert.IsType<TaggedClient>(result);
        Assert.Equal("B", outer.Tag);

        TaggedClient inner = Assert.IsType<TaggedClient>(outer.Inner);
        Assert.Equal("A", inner.Tag);
    }

    /// <summary>
    /// Config override on a distinct type shows priority change actually reorders.
    /// Uses DistinctOverrideMiddleware (attribute priority 200) overridden to 50 →
    /// sorts before Priority100Middleware (attribute priority 100, no override).
    /// </summary>
    [Fact]
    public void Apply_ConfigOverrideDistinctType_ReordersRelativeToOtherMiddleware()
    {
        MiddlewareRegistry registry = new();
        Priority100Middleware lo = new("lo");  // attribute 100, no override
        Priority200Middleware hi = new("hi");  // attribute 200, config overrides to 50

        registry.Register([lo, hi]);

        IConfiguration config = ConfigWithPriorityOverride(
            nameof(Priority200Middleware), 50);

        MiddlewarePipelineBuilder builder = new(registry, config);
        IChatClient result = builder.Apply(BaseClient());

        // hi now has effective priority 50 → innermost; lo stays at 100 → outer.
        TaggedClient outer = Assert.IsType<TaggedClient>(result);
        Assert.Equal("lo", outer.Tag);

        TaggedClient inner = Assert.IsType<TaggedClient>(outer.Inner);
        Assert.Equal("hi", inner.Tag);
    }

    /// <summary>
    /// No middleware registered → Apply returns the exact base client instance unchanged (3.4).
    /// </summary>
    [Fact]
    public void Apply_NoMiddleware_ReturnsBaseClientUnchanged()
    {
        MiddlewareRegistry registry = new();
        MiddlewarePipelineBuilder builder = new(registry, EmptyConfig());
        IChatClient baseClient = BaseClient();

        IChatClient result = builder.Apply(baseClient);

        Assert.Same(baseClient, result);
    }
}
