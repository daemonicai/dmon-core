using System.Diagnostics.Metrics;
using System.Text.Json;
using Daemon.Routing;
using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Core.Extensions;
using Dmon.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

// The counter instrument (DaemonTelemetry.PersonalToWorldMisclassifyCounter) is process-global.
// A MeterListener receives ALL measurements process-wide, so parallel gate-firing tests bleed into
// each other's delta counts. Disable intra-assembly parallelism — the suite is small and fast.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Daemon.Routing.Tests;

// ── Shared fakes (file-scoped, not exported) ──────────────────────────────────

/// <summary>
/// A classifier fake that returns a canned <see cref="RouteDecision"/> as JSON
/// in the assistant message text. The M.E.AI GetResponseAsync&lt;T&gt; extension
/// deserialises this directly.
/// </summary>
file sealed class ClassifierFake : IChatClient
{
    private readonly string _json;

    public ClassifierFake(RouteDecision decision)
    {
        _json = JsonSerializer.Serialize(decision);
    }

    /// <summary>Constructs a fake that returns raw (unparseable) text.</summary>
    public ClassifierFake(string rawText)
    {
        _json = rawText;
    }

    public ChatClientMetadata Metadata => new("fake-classifier", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, _json));
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

/// <summary>
/// A spy backend that records call count, received options, and received messages.
/// Returns a canned <see cref="ChatResponse"/> with a configurable message list.
/// Supports a <c>Disposed</c> flag for disposal tests.
/// </summary>
file sealed class BackendSpy : IChatClient
{
    private readonly string _name;
    private readonly IReadOnlyList<ChatMessage>? _responseMessages;

    public int CallCount { get; private set; }
    public ChatOptions? ReceivedOptions { get; private set; }
    public IList<ChatMessage>? ReceivedMessages { get; private set; }
    public bool Disposed { get; private set; }

    public BackendSpy(string name, IReadOnlyList<ChatMessage>? responseMessages = null)
    {
        _name = name;
        _responseMessages = responseMessages;
    }

    public ChatClientMetadata Metadata => new($"spy-{_name}", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        ReceivedOptions = options;
        ReceivedMessages = messages.ToList();
        List<ChatMessage> responseMessages = _responseMessages is not null
            ? new List<ChatMessage>(_responseMessages)
            : [new ChatMessage(ChatRole.Assistant, $"[{_name}]")];
        ChatResponse response = new(responseMessages);
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        ReceivedOptions = options;
        ReceivedMessages = messages.ToList();
        return AsyncEnumerable.Empty<ChatResponseUpdate>();
    }

    public void Dispose() => Disposed = true;

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

/// <summary>
/// A first-line backend that returns a response containing a <c>think_harder</c>
/// function call + result, optionally with additional real tool call/result content.
/// </summary>
file sealed class EscalatingFirstLineFake : IChatClient
{
    private readonly bool _includeRealToolCall;
    private readonly bool _multipleThinkHarder;

    public int CallCount { get; private set; }

    public EscalatingFirstLineFake(bool includeRealToolCall = false, bool multipleThinkHarder = false)
    {
        _includeRealToolCall = includeRealToolCall;
        _multipleThinkHarder = multipleThinkHarder;
    }

    public ChatClientMetadata Metadata => new("escalating-first-line", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        // Build a response that includes think_harder call + result (and optionally a real tool call).
        List<ChatMessage> responseMessages = [];

        if (_includeRealToolCall)
        {
            // Add a real tool call + result pair.
            ChatMessage realCallMsg = new(ChatRole.Assistant, [
                new FunctionCallContent("real-call-1", "real_tool", new Dictionary<string, object?>())
            ]);
            ChatMessage realResultMsg = new(ChatRole.Tool, [
                new FunctionResultContent("real-call-1", "real_result")
            ]);
            responseMessages.Add(realCallMsg);
            responseMessages.Add(realResultMsg);
        }

        // Add the think_harder call + result pair.
        ChatMessage thinkHarderCallMsg = new(ChatRole.Assistant, [
            new FunctionCallContent("think-1", "think_harder", new Dictionary<string, object?>())
        ]);
        ChatMessage thinkHarderResultMsg = new(ChatRole.Tool, [
            new FunctionResultContent("think-1", "escalating")
        ]);
        responseMessages.Add(thinkHarderCallMsg);
        responseMessages.Add(thinkHarderResultMsg);

        if (_multipleThinkHarder)
        {
            // Simulate a call-alone violation: a second think_harder call with a distinct CallId.
            responseMessages.Add(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("think-2", "think_harder", new Dictionary<string, object?>())
            ]));
            responseMessages.Add(new ChatMessage(ChatRole.Tool, [
                new FunctionResultContent("think-2", "escalating")
            ]));
        }

        return Task.FromResult(new ChatResponse(responseMessages));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return AsyncEnumerable.Empty<ChatResponseUpdate>();
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

/// <summary>
/// A first-line backend that would emit draft tokens if streamed, but is used to verify
/// that on the escalation path, no draft tokens reach the caller.
/// </summary>
file sealed class DraftEmittingFirstLineFake : IChatClient
{
    public ChatClientMetadata Metadata => new("draft-first-line", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Returns a response that triggers escalation.
        List<ChatMessage> responseMessages =
        [
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("think-draft-1", "think_harder", new Dictionary<string, object?>())
            ]),
            new ChatMessage(ChatRole.Tool, [
                new FunctionResultContent("think-draft-1", "escalating")
            ])
        ];
        return Task.FromResult(new ChatResponse(responseMessages));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Would emit draft tokens — but escalation path uses GetResponseAsync, not this.
        yield return new ChatResponseUpdate(ChatRole.Assistant, "draft-token-1");
        yield return new ChatResponseUpdate(ChatRole.Assistant, "draft-token-2");
        await Task.Yield();
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

file sealed class FakeAbilityProvider(string scope, params AITool[] tools) : IAbilityProvider
{
    public string Scope => scope;
    public IEnumerable<AITool> Tools => tools;
}

// ── Helper factory ─────────────────────────────────────────────────────────────

file static class RouterFactory
{
    public static IEnumerable<ChatMessage> AMessage()
        => [new ChatMessage(ChatRole.User, "hello")];
}

// ── 11.2 — Routing invariants ─────────────────────────────────────────────────

public sealed class RoutingInvariantTests
{
    [Fact]
    public async Task Personal_RoutesToFirstLine()
    {
        // All non-egress turns go to first-line (ADR-032 D1).
        RouteDecision decision = new("personal", Impersonal: false, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        // First-line is the classifier itself (ClassifierFake) — escalation and egress must NOT be called.
        Assert.Equal(0, escalation.CallCount);
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task World_Impersonal_HighConfidence_RoutesToEgress()
    {
        RouteDecision decision = new("world", Impersonal: true, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(0, escalation.CallCount);
        Assert.Equal(1, egress.CallCount);
    }

    [Fact]
    public async Task World_Impersonal_LowConfidence_RoutesToFirstLine_NotEgress()
    {
        // Confidence < threshold → privacy gate overrides scope to personal → first-line, not egress.
        RouteDecision decision = new("world", Impersonal: true, Confidence: 0.5f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(0, escalation.CallCount);
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task World_Personal_ConfidenceExactlyAtThreshold_RoutesToFirstLine()
    {
        // Confidence == threshold is NOT > threshold → first-line (personal bias).
        TriageOptions options = new() { EgressThreshold = 0.8f };
        RouteDecision decision = new("world", Impersonal: true, Confidence: 0.8f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, options);

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(0, escalation.CallCount);
        Assert.Equal(0, egress.CallCount);
    }
}

// ── 11.3 — Privacy gate: tool manifest ───────────────────────────────────────

public sealed class PrivacyGateToolManifestTests
{
    private static AITool PersonalTool() =>
        AIFunctionFactory.Create(() => "result", "personal_tool");

    private static AITool WorldTool() =>
        AIFunctionFactory.Create(() => "result", "world_tool");

    [Fact]
    public async Task PersonalTurn_ManifestContainsOnlyPersonalTools()
    {
        FakeAbilityProvider personalProvider = new("personal", PersonalTool());
        FakeAbilityProvider worldProvider = new("world", WorldTool());

        RouteDecision decision = new("personal", Impersonal: false, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([personalProvider, worldProvider]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        // Egress not called → the options for the first-line path are on the internal FIC-wrapped client.
        // Verify via egress NOT being called and egress gets no personal/world tools.
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task WorldTurn_HighConfidence_RoutesToEgressWithWorldTools()
    {
        FakeAbilityProvider personalProvider = new("personal", PersonalTool());
        FakeAbilityProvider worldProvider = new("world", WorldTool());

        RouteDecision decision = new("world", Impersonal: true, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([personalProvider, worldProvider]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(1, egress.CallCount);
        IList<AITool>? tools = egress.ReceivedOptions?.Tools;
        Assert.NotNull(tools);
        Assert.Single(tools);
        Assert.Equal("world_tool", ((AIFunction)tools[0]).Name);
    }

    [Fact]
    public async Task WorldTurn_HighConfidence_EgressManifestContainsNoPersonalTools()
    {
        FakeAbilityProvider personalProvider = new("personal", PersonalTool());
        FakeAbilityProvider worldProvider = new("world", WorldTool());

        RouteDecision decision = new("world", Impersonal: true, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([personalProvider, worldProvider]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        IList<AITool>? tools = egress.ReceivedOptions?.Tools;
        Assert.NotNull(tools);
        Assert.DoesNotContain(tools, t => t is AIFunction f && f.Name == "personal_tool");
    }

    [Fact]
    public async Task EgressManifest_DoesNotContainThinkHarder()
    {
        RouteDecision decision = new("world", Impersonal: true, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        IList<AITool>? tools = egress.ReceivedOptions?.Tools;
        Assert.NotNull(tools);
        Assert.DoesNotContain(tools, t => t is AIFunction f && f.Name == "think_harder");
    }

    [Fact]
    public async Task EscalationManifest_DoesNotContainThinkHarder()
    {
        // Classify returns personal → first-line path; first-line (EscalatingFirstLineFake)
        // triggers escalation → escalation client is invoked without think_harder in its manifest.
        BackendSpy escalationSpy = new("escalation-spy");
        BackendSpy egressSpy = new("egress");
        AbilityRegistry abs = new([]);
        EscalatingFirstLineFake firstLineFake = new();
        RouteDecision personalDecision = new("personal", Impersonal: false, Confidence: 0.95f);

        ClassifyThenHandleFake combo = new(new ClassifierFake(personalDecision), firstLineFake);

        TriageRouter routerCombo = new(combo, escalationSpy, egressSpy, abs, new TriageOptions());
        await routerCombo.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(1, escalationSpy.CallCount);
        IList<AITool>? tools = escalationSpy.ReceivedOptions?.Tools;
        Assert.NotNull(tools);
        Assert.DoesNotContain(tools, t => t is AIFunction f && f.Name == "think_harder");
    }

    [Fact]
    public async Task PersonalTurn_FirstLineManifestContainsPersonalToolAndThinkHarder_NotWorldTool()
    {
        // 7.1 coverage gap: verify the FIRST-LINE backend actually receives a manifest that
        // (a) contains the personal-scope ability, (b) contains think_harder, and
        // (c) contains no world-scope ability.
        //
        // Uses ClassifyThenHandleFake to separate the classify call (ClassifierFake) from
        // the first-line handle call (BackendSpy), so ReceivedOptions captures the manifest
        // passed to the first-line handler rather than the classification query.
        FakeAbilityProvider personalProvider = new("personal", PersonalTool());
        FakeAbilityProvider worldProvider = new("world", WorldTool());

        RouteDecision personalDecision = new("personal", Impersonal: false, Confidence: 0.95f);
        BackendSpy firstLineHandlerSpy = new("first-line-handler");
        BackendSpy escalationSpy = new("escalation");
        BackendSpy egressSpy = new("egress");
        AbilityRegistry abilities = new([personalProvider, worldProvider]);

        ClassifyThenHandleFake combo = new(new ClassifierFake(personalDecision), firstLineHandlerSpy);
        TriageRouter router = new(combo, escalationSpy, egressSpy, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        // First-line handler must have been invoked (not egress, not escalation).
        Assert.Equal(1, firstLineHandlerSpy.CallCount);
        Assert.Equal(0, egressSpy.CallCount);

        IList<AITool>? tools = firstLineHandlerSpy.ReceivedOptions?.Tools;
        Assert.NotNull(tools);

        // (a) personal-scope ability present.
        Assert.Contains(tools, t => t is AIFunction f && f.Name == "personal_tool");

        // (b) think_harder signal present (first-line only).
        Assert.Contains(tools, t => t is AIFunction f && f.Name == "think_harder");

        // (c) world-scope ability absent — privacy invariant.
        Assert.DoesNotContain(tools, t => t is AIFunction f && f.Name == "world_tool");
    }
}

/// <summary>
/// A fake that routes the first call to a classifier and subsequent calls to a handler.
/// This separates the classify call from the first-line handling call.
/// </summary>
file sealed class ClassifyThenHandleFake : IChatClient
{
    private readonly IChatClient _classifier;
    private readonly IChatClient _handler;
    private int _callCount;

    public ClassifyThenHandleFake(IChatClient classifier, IChatClient handler)
    {
        _classifier = classifier;
        _handler = handler;
    }

    public ChatClientMetadata Metadata => new("classify-then-handle", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        int callIndex = System.Threading.Interlocked.Increment(ref _callCount);
        // First call is the classify call (no tools, structured output).
        // Subsequent calls are first-line handling calls (have tools in options).
        IChatClient target = callIndex == 1 ? _classifier : _handler;
        return target.GetResponseAsync(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

// ── 11.4 — Misclassification counter ─────────────────────────────────────────

public sealed class MisclassifyCounterTests
{
    internal static async Task<long> MeasureCounterDeltaAsync(Func<Task> action)
    {
        long delta = 0;

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Daemon.Routing"
                && instrument.Name == "dmon.triage.misclassify.personal_to_world")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
        {
            delta += measurement;
        });
        listener.Start();

        await action();

        listener.RecordObservableInstruments();
        return delta;
    }

    [Fact]
    public async Task LowConfidenceWorld_IncrementsCounterExactlyOnce()
    {
        RouteDecision decision = new("world", Impersonal: true, Confidence: 0.5f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        long delta = await MeasureCounterDeltaAsync(() => router.GetResponseAsync(RouterFactory.AMessage()));

        Assert.Equal(1, delta);
    }

    [Fact]
    public async Task ConfidentPersonal_DoesNotIncrementCounter()
    {
        RouteDecision decision = new("personal", Impersonal: false, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        long delta = await MeasureCounterDeltaAsync(() => router.GetResponseAsync(RouterFactory.AMessage()));

        Assert.Equal(0, delta);
    }

    [Fact]
    public async Task HighConfidenceWorld_DoesNotIncrementCounter()
    {
        RouteDecision decision = new("world", Impersonal: true, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        long delta = await MeasureCounterDeltaAsync(() => router.GetResponseAsync(RouterFactory.AMessage()));

        Assert.Equal(0, delta);
    }
}

// ── 11.2 / A1 — Garbage classifier response ──────────────────────────────────

public sealed class ClassifierFailSafeTests
{
    [Fact]
    public async Task GarbageClassifierResponse_FailSafe_RoutesToFirstLine_NotEscalationOrEgress()
    {
        // A1: unparseable JSON → TryGetResult returns false → fail safe to personal (no egress, no escalation).
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake("not valid json at all!!!"), escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(0, escalation.CallCount);
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task GarbageClassifierResponse_FailSafe_DoesNotIncrementCounter()
    {
        // A1 fail-safe yields confident personal (Confidence: 1f) → no gate fires → no counter.
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake("not valid json at all!!!"), escalation, egress, abilities, new TriageOptions());

        long delta = await MisclassifyCounterTests.MeasureCounterDeltaAsync(
            () => router.GetResponseAsync(RouterFactory.AMessage()));

        Assert.Equal(0, delta);
    }
}

// ── 4.1 / 4.2 / 4.3 — Factory: no I/O in Create; lazy resolution; concurrency ────

public sealed class FactoryLazyResolutionTests
{
    [Fact]
    public void Create_PerformsNoIO_DelegatesNotInvokedAtConstruction()
    {
        // 4.1 / 4.3: Create must not invoke any delegate — counter must be 0 immediately after Create.
        int firstLineResolveCount = 0;
        int escalationResolveCount = 0;
        int egressResolveCount = 0;

        BackendSpy firstLineSpy = new("first-line");
        BackendSpy escalationSpy = new("escalation");
        BackendSpy egressSpy = new("egress");

        ServiceCollection services = new();
        services.AddSingleton(new FirstLineRawClientFactory(sp =>
        {
            firstLineResolveCount++;
            return ValueTask.FromResult<IChatClient>(firstLineSpy);
        }));
        services.AddSingleton(new EscalationClientFactory(sp =>
        {
            escalationResolveCount++;
            return ValueTask.FromResult<IChatClient>(escalationSpy);
        }));
        services.AddSingleton(new EgressClientFactory(sp =>
        {
            egressResolveCount++;
            return ValueTask.FromResult<IChatClient>(egressSpy);
        }));
        services.AddSingleton(new AbilityRegistry([]));
        services.AddSingleton(new TriageOptions());
        services.AddSingleton<ITerminalClientFactory, TriageRouterFactory>();

        using ServiceProvider sp2 = services.BuildServiceProvider();
        ITerminalClientFactory factory = sp2.GetRequiredService<ITerminalClientFactory>();

        // Act: Create must be synchronous and perform no I/O.
        IChatClient router = factory.Create(sp2);

        // Delegates must NOT have been invoked.
        Assert.Equal(0, firstLineResolveCount);
        Assert.Equal(0, escalationResolveCount);
        Assert.Equal(0, egressResolveCount);

        router.Dispose();
    }

    [Fact]
    public async Task BackendsResolvedOnce_CachedAcrossMultipleTurns()
    {
        // 4.2: each backend resolved at most once per router instance.
        int firstLineResolveCount = 0;
        int egressResolveCount = 0;

        BackendSpy egressSpy = new("egress");
        RouteDecision decision = new("world", Impersonal: true, Confidence: 0.95f);

        TriageRouter router = new(
            _ => { firstLineResolveCount++; return ValueTask.FromResult<IChatClient>(new ClassifierFake(decision)); },
            _ => ValueTask.FromResult<IChatClient>(new BackendSpy("escalation")),
            _ => { egressResolveCount++; return ValueTask.FromResult<IChatClient>(egressSpy); },
            new NopServiceProviderForTests(),
            new AbilityRegistry([]),
            new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());
        await router.GetResponseAsync(RouterFactory.AMessage());
        await router.GetResponseAsync(RouterFactory.AMessage());

        // Each backend resolved exactly once despite 3 turns.
        Assert.Equal(1, firstLineResolveCount);
        Assert.Equal(1, egressResolveCount);

        router.Dispose();
    }

    [Fact]
    public async Task ConcurrentFirstTurns_ResolveFirstLineExactlyOnce()
    {
        // 4.3: concurrent first turns must produce a single first-line instance.
        int resolveCount = 0;
        RouteDecision decision = new("personal", Impersonal: false, Confidence: 0.95f);

        TriageRouter router = new(
            async _ =>
            {
                System.Threading.Interlocked.Increment(ref resolveCount);
                await Task.Yield(); // allow concurrency contention
                return (IChatClient)new ClassifierFake(decision);
            },
            _ => ValueTask.FromResult<IChatClient>(new BackendSpy("escalation")),
            _ => ValueTask.FromResult<IChatClient>(new BackendSpy("egress")),
            new NopServiceProviderForTests(),
            new AbilityRegistry([]),
            new TriageOptions());

        // Fan out 10 concurrent GetResponseAsync calls.
        Task[] tasks = Enumerable.Range(0, 10)
            .Select(_ => router.GetResponseAsync(RouterFactory.AMessage()))
            .ToArray();
        await Task.WhenAll(tasks);

        // Despite 10 concurrent callers, the first-line delegate must be invoked exactly once.
        Assert.Equal(1, resolveCount);

        router.Dispose();
    }
}

// Minimal IServiceProvider for use in tests that call the delegate ctor.
file sealed class NopServiceProviderForTests : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

// ── 5.1-5.5 — think_harder escalation logic ──────────────────────────────────

public sealed class ThinkHarderEscalationTests
{
    [Fact]
    public async Task NoThinkHarder_FirstLineAnswerReturnedDirectly_EscalationNotInvoked()
    {
        // 5.4 / 5.5: first-line answer without escalation is returned directly.
        RouteDecision decision = new("personal", Impersonal: false, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        // ClassifierFake returns a personal decision; first-line path runs; no think_harder → no escalation.
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        ChatResponse response = await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(0, escalation.CallCount);
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task ThinkHarder_TerminatesFirstLine_EscalationInvoked()
    {
        // 5.2 / 5.3 / 5.5: think_harder in first-line response triggers escalation.
        EscalatingFirstLineFake firstLine = new();
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);

        // Route: personal → first-line (EscalatingFirstLineFake returns think_harder call).
        RouteDecision personalDecision = new("personal", Impersonal: false, Confidence: 0.95f);
        ClassifyThenHandleFake combo = new(new ClassifierFake(personalDecision), firstLine);

        TriageRouter router = new(combo, escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(1, escalation.CallCount);
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task ThinkHarder_EscalationReceivesInheritedMessages_WithoutThinkHarderContent()
    {
        // 5.3 / 5.5: escalation receives original + first-line messages minus think_harder call+result.
        EscalatingFirstLineFake firstLine = new(includeRealToolCall: true);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);

        RouteDecision personalDecision = new("personal", Impersonal: false, Confidence: 0.95f);
        ClassifyThenHandleFake combo = new(new ClassifierFake(personalDecision), firstLine);

        TriageRouter router = new(combo, escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(1, escalation.CallCount);
        IList<ChatMessage>? msgs = escalation.ReceivedMessages;
        Assert.NotNull(msgs);

        // No think_harder FunctionCallContent or FunctionResultContent in inherited messages.
        bool hasThinkHarderCall = msgs.Any(m =>
            m.Contents.OfType<FunctionCallContent>().Any(f => f.Name == "think_harder"));
        bool hasThinkHarderResult = msgs.Any(m =>
            m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "think-1"));
        Assert.False(hasThinkHarderCall, "Escalation messages must not contain think_harder FunctionCallContent.");
        Assert.False(hasThinkHarderResult, "Escalation messages must not contain think_harder FunctionResultContent.");
    }

    [Fact]
    public async Task ThinkHarder_MultipleCalls_NoDanglingResultInEscalationMessages()
    {
        // Robustness: if the model violates the call-alone rule and emits two think_harder
        // calls with distinct CallIds, BOTH calls and BOTH results must be stripped — no
        // dangling FunctionResultContent may reach the escalation client.
        EscalatingFirstLineFake firstLine = new(includeRealToolCall: true, multipleThinkHarder: true);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);

        RouteDecision personalDecision = new("personal", Impersonal: false, Confidence: 0.95f);
        ClassifyThenHandleFake combo = new(new ClassifierFake(personalDecision), firstLine);

        TriageRouter router = new(combo, escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(1, escalation.CallCount);
        IList<ChatMessage>? msgs = escalation.ReceivedMessages;
        Assert.NotNull(msgs);

        bool hasThinkHarderCall = msgs.Any(m =>
            m.Contents.OfType<FunctionCallContent>().Any(f => f.Name == "think_harder"));
        bool hasAnyThinkHarderResult = msgs.Any(m =>
            m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId is "think-1" or "think-2"));
        Assert.False(hasThinkHarderCall, "All think_harder calls must be stripped.");
        Assert.False(hasAnyThinkHarderResult, "No think_harder result may be left dangling.");

        // The real tool call+result must still survive.
        bool hasRealResult = msgs.Any(m =>
            m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "real-call-1"));
        Assert.True(hasRealResult, "Real tool result must be preserved.");
    }

    [Fact]
    public async Task ThinkHarder_RealToolCallPreservedInEscalationMessages()
    {
        // 5.5: real tool call+result are carried forward to escalation.
        EscalatingFirstLineFake firstLine = new(includeRealToolCall: true);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);

        RouteDecision personalDecision = new("personal", Impersonal: false, Confidence: 0.95f);
        ClassifyThenHandleFake combo = new(new ClassifierFake(personalDecision), firstLine);

        TriageRouter router = new(combo, escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(1, escalation.CallCount);
        IList<ChatMessage>? msgs = escalation.ReceivedMessages;
        Assert.NotNull(msgs);

        bool hasRealCall = msgs.Any(m =>
            m.Contents.OfType<FunctionCallContent>().Any(f => f.Name == "real_tool"));
        bool hasRealResult = msgs.Any(m =>
            m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "real-call-1"));
        Assert.True(hasRealCall, "Escalation messages must preserve the real tool FunctionCallContent.");
        Assert.True(hasRealResult, "Escalation messages must preserve the real tool FunctionResultContent.");
    }

    [Fact]
    public async Task EscalationManifest_ExcludesThinkHarder()
    {
        // 5.5: escalation client is never offered think_harder.
        EscalatingFirstLineFake firstLine = new();
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);

        RouteDecision personalDecision = new("personal", Impersonal: false, Confidence: 0.95f);
        ClassifyThenHandleFake combo = new(new ClassifierFake(personalDecision), firstLine);

        TriageRouter router = new(combo, escalation, egress, abilities, new TriageOptions());

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(1, escalation.CallCount);
        IList<AITool>? tools = escalation.ReceivedOptions?.Tools;
        Assert.NotNull(tools);
        Assert.DoesNotContain(tools, t => t is AIFunction f && f.Name == "think_harder");
    }
}

// ── Disposal tests ────────────────────────────────────────────────────────────

public sealed class RouterDisposalTests
{
    [Fact]
    public async Task Dispose_DisposesResolvedBackends()
    {
        // Resolved backends must be disposed when the router is disposed.
        RouteDecision egressDecision = new("world", Impersonal: true, Confidence: 0.95f);
        BackendSpy egressSpy = new("egress");
        BackendSpy escalationSpy = new("escalation");
        AbilityRegistry abilities = new([]);

        TriageRouter router = new(new ClassifierFake(egressDecision), escalationSpy, egressSpy, abilities, new TriageOptions());

        // Trigger egress resolution.
        await router.GetResponseAsync(RouterFactory.AMessage());

        router.Dispose();

        Assert.True(egressSpy.Disposed, "Resolved egress backend must be disposed on router dispose.");
    }

    [Fact]
    public void Dispose_DoesNotForceResolveUnstartedLazies()
    {
        // Un-started lazies must not be resolved during dispose.
        int resolveCount = 0;

        TriageRouter router = new(
            _ => { resolveCount++; return ValueTask.FromResult<IChatClient>(new BackendSpy("first-line")); },
            _ => { resolveCount++; return ValueTask.FromResult<IChatClient>(new BackendSpy("escalation")); },
            _ => { resolveCount++; return ValueTask.FromResult<IChatClient>(new BackendSpy("egress")); },
            new NopServiceProviderForTests(),
            new AbilityRegistry([]),
            new TriageOptions());

        // Dispose without ever calling GetResponseAsync — delegates must NOT be invoked.
        router.Dispose();

        Assert.Equal(0, resolveCount);
    }
}

// ── 6.1 / 6.2 — Streaming ack, no draft tokens, escalation marker ─────────────

public sealed class StreamingAckTests
{
    [Fact]
    public async Task GetStreamingResponse_YieldsAckBeforeAnyBackendOutput()
    {
        // 6.1: ack precedes any backend output.
        RouteDecision decision = new("personal", Impersonal: false, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in router.GetStreamingResponseAsync(RouterFactory.AMessage()))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        // First update is the synthetic ack.
        Assert.Equal("[triage]", updates[0].Text);
    }

    [Fact]
    public async Task GetStreamingResponse_EgressRoute_YieldsAckThenEgressStream()
    {
        // 6.1: ack emitted before egress stream.
        RouteDecision decision = new("world", Impersonal: true, Confidence: 0.95f);
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);
        TriageRouter router = new(new ClassifierFake(decision), escalation, egress, abilities, new TriageOptions());

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in router.GetStreamingResponseAsync(RouterFactory.AMessage()))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        Assert.Equal("[triage]", updates[0].Text);
        // Ack does not name the route.
        Assert.DoesNotContain("egress", updates[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStreamingResponse_EscalationPath_ZeroFirstLineDraftTokensBeforeMarker()
    {
        // 6.2: no first-line draft tokens streamed when escalation occurs.
        DraftEmittingFirstLineFake firstLine = new();
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);

        RouteDecision personalDecision = new("personal", Impersonal: false, Confidence: 0.95f);
        ClassifyThenHandleFake combo = new(new ClassifierFake(personalDecision), firstLine);

        TriageRouter router = new(combo, escalation, egress, abilities, new TriageOptions());

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in router.GetStreamingResponseAsync(RouterFactory.AMessage()))
        {
            updates.Add(update);
        }

        // updates[0] = ack "[triage]"
        // updates[1] = escalation marker "[escalating]"
        // updates[2..] = escalation stream (empty from BackendSpy)
        // Draft tokens "draft-token-1" / "draft-token-2" must NOT appear.
        Assert.DoesNotContain(updates, u => u.Text?.Contains("draft-token") == true);
    }

    [Fact]
    public async Task GetStreamingResponse_EscalationPath_MarkerPrecedesEscalationStream()
    {
        // 6.1 / 6.2: distinct escalation marker appears before escalation stream.
        DraftEmittingFirstLineFake firstLine = new();
        BackendSpy escalation = new("escalation");
        BackendSpy egress = new("egress");
        AbilityRegistry abilities = new([]);

        RouteDecision personalDecision = new("personal", Impersonal: false, Confidence: 0.95f);
        ClassifyThenHandleFake combo = new(new ClassifierFake(personalDecision), firstLine);

        TriageRouter router = new(combo, escalation, egress, abilities, new TriageOptions());

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in router.GetStreamingResponseAsync(RouterFactory.AMessage()))
        {
            updates.Add(update);
        }

        // Must have at least ack + escalation marker.
        Assert.True(updates.Count >= 2, $"Expected at least 2 updates (ack + marker), got {updates.Count}.");
        Assert.Equal("[triage]", updates[0].Text);
        Assert.Equal("[escalating]", updates[1].Text);
    }
}

// ── 11.5 — Terminal-client hook via DI builder ────────────────────────────────

public sealed class TerminalClientHookTests
{
    [Fact]
    public void UseTriage_AddEscalation_AddEgress_ProducesTriageRouterAsTerminalClient()
    {
        BackendSpy firstLineRaw = new("first-line-raw");
        BackendSpy escalationRaw = new("escalation-raw");
        BackendSpy egressRaw = new("egress-raw");

        DmonHostBuilder builder = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();

        builder.UseTriage(firstLineRaw);
        builder.AddEscalation(escalationRaw);
        builder.AddEgress(egressRaw);

        DmonBuiltHost host = builder.Build();

        ITerminalClientFactory factory = host.Services.GetRequiredService<ITerminalClientFactory>();
        IChatClient terminalClient = factory.Create(host.Services);

        Assert.IsType<TriageRouter>(terminalClient);
        terminalClient.Dispose();
    }

    [Fact]
    public void UseTriage_WithoutEscalation_ThrowsOnCreate()
    {
        // If AddEscalation is missing, GetRequiredService<EscalationClientFactory> throws.
        BackendSpy firstLineRaw = new("first-line-raw");
        BackendSpy egressRaw = new("egress-raw");

        DmonHostBuilder builder = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();

        builder.UseTriage(firstLineRaw);
        builder.AddEgress(egressRaw);

        DmonBuiltHost host = builder.Build();

        ITerminalClientFactory factory = host.Services.GetRequiredService<ITerminalClientFactory>();
        Assert.Throws<InvalidOperationException>(() => factory.Create(host.Services));
    }

    [Fact]
    public void UseTriage_WithDelegateFactory_ProducesTriageRouter()
    {
        // 3.3: verify the Func<ISP, ValueTask<IChatClient>> overload registers correctly.
        BackendSpy escalationRaw = new("escalation-raw");
        BackendSpy egressRaw = new("egress-raw");

        DmonHostBuilder builder = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();

        builder.UseTriage(_ => ValueTask.FromResult<IChatClient>(new BackendSpy("first-line")));
        builder.AddEscalation(_ => ValueTask.FromResult<IChatClient>(escalationRaw));
        builder.AddEgress(_ => ValueTask.FromResult<IChatClient>(egressRaw));

        DmonBuiltHost host = builder.Build();

        ITerminalClientFactory factory = host.Services.GetRequiredService<ITerminalClientFactory>();
        IChatClient terminalClient = factory.Create(host.Services);

        Assert.IsType<TriageRouter>(terminalClient);
        terminalClient.Dispose();
    }
}
