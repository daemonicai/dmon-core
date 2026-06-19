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
// each other's delta counts. Disable intra-assembly parallelism — the suite is small (22 tests)
// and fast; this is the safest, least error-prone fix.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Daemon.Routing.Tests;

// ── Shared fakes (file-scoped, not exported) ──────────────────────────────────

/// <summary>
/// A classifier fake that returns a canned <see cref="RouteDecision"/> as JSON
/// in the assistant message text. The M.E.AI GetResponseAsync&lt;T&gt; extension
/// deserialises this directly (RouteDecision is an object schema — no wrapping).
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
/// A spy backend that records whether it was called and captures the <see cref="ChatOptions"/>
/// passed to it. Returns a minimal canned <see cref="ChatResponse"/>.
/// </summary>
file sealed class BackendSpy : IChatClient
{
    private readonly string _name;

    public int CallCount { get; private set; }
    public ChatOptions? ReceivedOptions { get; private set; }

    public BackendSpy(string name)
    {
        _name = name;
    }

    public ChatClientMetadata Metadata => new($"spy-{_name}", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        ReceivedOptions = options;
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, $"[{_name}]"));
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        ReceivedOptions = options;
        return AsyncEnumerable.Empty<ChatResponseUpdate>();
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
    public static (TriageRouter router, BackendSpy e2b, BackendSpy reasoner, BackendSpy egress)
        Build(
            IChatClient classifier,
            IAbilityProvider? personalProvider = null,
            IAbilityProvider? worldProvider = null,
            TriageOptions? options = null)
    {
        BackendSpy e2b = new("e2b");
        BackendSpy reasoner = new("reasoner");
        BackendSpy egress = new("egress");

        List<IAbilityProvider> providers = [];
        if (personalProvider is not null) providers.Add(personalProvider);
        if (worldProvider is not null) providers.Add(worldProvider);

        AbilityRegistry abilities = new(providers);
        TriageRouter router = new(classifier, e2b, reasoner, egress, abilities, options ?? new TriageOptions());

        return (router, e2b, reasoner, egress);
    }

    public static IEnumerable<ChatMessage> AMessage()
        => [new ChatMessage(ChatRole.User, "hello")];
}

// ── 11.2 — Routing invariants ─────────────────────────────────────────────────

public sealed class RoutingInvariantTests
{
    [Fact]
    public async Task Personal_Direct_RoutesToE2b()
    {
        RouteDecision decision = new("personal", Tier.Direct, Impersonal: false, Confidence: 0.95f);
        (TriageRouter router, BackendSpy e2b, BackendSpy reasoner, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake(decision));

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(1, e2b.CallCount);
        Assert.Equal(0, reasoner.CallCount);
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task World_Impersonal_HighConfidence_RoutesToEgress()
    {
        // Confidence > 0.8 (default threshold), impersonal=true, scope=world → egress
        RouteDecision decision = new("world", Tier.Direct, Impersonal: true, Confidence: 0.95f);
        (TriageRouter router, BackendSpy e2b, BackendSpy reasoner, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake(decision));

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(0, e2b.CallCount);
        Assert.Equal(0, reasoner.CallCount);
        Assert.Equal(1, egress.CallCount);
    }

    [Fact]
    public async Task World_Impersonal_LowConfidence_RoutesToE2b()
    {
        // Confidence < 0.8 — privacy gate overrides to personal → e2b, NOT egress
        RouteDecision decision = new("world", Tier.Direct, Impersonal: true, Confidence: 0.5f);
        (TriageRouter router, BackendSpy e2b, BackendSpy reasoner, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake(decision));

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(1, e2b.CallCount);
        Assert.Equal(0, reasoner.CallCount);
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task ReasonerTier_AnyScope_RoutesToReasoner()
    {
        RouteDecision decision = new("personal", Tier.Reasoner, Impersonal: false, Confidence: 0.95f);
        (TriageRouter router, BackendSpy e2b, BackendSpy reasoner, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake(decision));

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(0, e2b.CallCount);
        Assert.Equal(1, reasoner.CallCount);
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task ReasonerTier_WorldScope_Personal_HighConfidence_RoutesToReasoner()
    {
        // Tier=Reasoner, scope=world, Impersonal=false, confidence above threshold.
        // Egress arm requires Impersonal=true, so it does not fire — reasoner is dispatched.
        // Confidence >= threshold so the privacy gate does not fire either.
        RouteDecision decision = new("world", Tier.Reasoner, Impersonal: false, Confidence: 0.95f);
        (TriageRouter router, BackendSpy e2b, BackendSpy reasoner, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake(decision));

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(0, e2b.CallCount);
        Assert.Equal(1, reasoner.CallCount);
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task EgressConditionMet_ReasonerTier_RoutesToEgress_NotReasoner()
    {
        // Spec dispatch order: egress FIRST (world+impersonal+confidence>threshold), THEN reasoner tier.
        // A Tier.Reasoner turn that also satisfies the egress condition routes to egress — egress wins.
        RouteDecision decision = new("world", Tier.Reasoner, Impersonal: true, Confidence: 0.95f);
        (TriageRouter router, BackendSpy e2b, BackendSpy reasoner, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake(decision));

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(0, e2b.CallCount);
        Assert.Equal(0, reasoner.CallCount);
        Assert.Equal(1, egress.CallCount);
    }

    [Fact]
    public async Task World_Personal_ConfidenceExactlyAtThreshold_RoutesToE2b()
    {
        // Confidence == threshold is NOT > threshold, so it routes to e2b (personal bias)
        TriageOptions options = new() { EgressThreshold = 0.8f };
        RouteDecision decision = new("world", Tier.Direct, Impersonal: true, Confidence: 0.8f);
        (TriageRouter router, BackendSpy e2b, BackendSpy reasoner, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake(decision), options: options);

        await router.GetResponseAsync(RouterFactory.AMessage());

        // Confidence == 0.8 is not > 0.8, so egress guard fails → falls through to e2b
        Assert.Equal(1, e2b.CallCount);
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

        RouteDecision decision = new("personal", Tier.Direct, Impersonal: false, Confidence: 0.95f);
        (TriageRouter router, BackendSpy e2b, _, _) =
            RouterFactory.Build(new ClassifierFake(decision), personalProvider, worldProvider);

        await router.GetResponseAsync(RouterFactory.AMessage());

        IList<AITool>? tools = e2b.ReceivedOptions?.Tools;
        Assert.NotNull(tools);
        Assert.Single(tools);
        Assert.Equal("personal_tool", ((AIFunction)tools[0]).Name);
    }

    [Fact]
    public async Task PersonalTurn_ManifestContainsNoWorldTools()
    {
        FakeAbilityProvider personalProvider = new("personal", PersonalTool());
        FakeAbilityProvider worldProvider = new("world", WorldTool());

        RouteDecision decision = new("personal", Tier.Direct, Impersonal: false, Confidence: 0.95f);
        (TriageRouter router, BackendSpy e2b, _, _) =
            RouterFactory.Build(new ClassifierFake(decision), personalProvider, worldProvider);

        await router.GetResponseAsync(RouterFactory.AMessage());

        IList<AITool>? tools = e2b.ReceivedOptions?.Tools;
        Assert.NotNull(tools);
        Assert.DoesNotContain(tools, t => t is AIFunction f && f.Name == "world_tool");
    }

    [Fact]
    public async Task WorldTurn_HighConfidence_ManifestContainsOnlyWorldTools()
    {
        FakeAbilityProvider personalProvider = new("personal", PersonalTool());
        FakeAbilityProvider worldProvider = new("world", WorldTool());

        RouteDecision decision = new("world", Tier.Direct, Impersonal: true, Confidence: 0.95f);
        (TriageRouter router, _, _, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake(decision), personalProvider, worldProvider);

        await router.GetResponseAsync(RouterFactory.AMessage());

        IList<AITool>? tools = egress.ReceivedOptions?.Tools;
        Assert.NotNull(tools);
        Assert.Single(tools);
        Assert.Equal("world_tool", ((AIFunction)tools[0]).Name);
    }

    [Fact]
    public async Task WorldTurn_HighConfidence_ManifestContainsNoPersonalTools()
    {
        FakeAbilityProvider personalProvider = new("personal", PersonalTool());
        FakeAbilityProvider worldProvider = new("world", WorldTool());

        RouteDecision decision = new("world", Tier.Direct, Impersonal: true, Confidence: 0.95f);
        (TriageRouter router, _, _, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake(decision), personalProvider, worldProvider);

        await router.GetResponseAsync(RouterFactory.AMessage());

        IList<AITool>? tools = egress.ReceivedOptions?.Tools;
        Assert.NotNull(tools);
        Assert.DoesNotContain(tools, t => t is AIFunction f && f.Name == "personal_tool");
    }

    [Fact]
    public async Task LowConfidenceWorld_ManifestIsPersonal_BackendIsE2b()
    {
        // RAW scope = world, but confidence < threshold → effective scope = personal
        // Backend reads RAW (e2b), manifest reads EFFECTIVE (personal tools only).
        FakeAbilityProvider personalProvider = new("personal", PersonalTool());
        FakeAbilityProvider worldProvider = new("world", WorldTool());

        RouteDecision decision = new("world", Tier.Direct, Impersonal: true, Confidence: 0.5f);
        (TriageRouter router, BackendSpy e2b, _, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake(decision), personalProvider, worldProvider);

        await router.GetResponseAsync(RouterFactory.AMessage());

        // Backend: e2b (raw world failed egress guard, so fell to e2b)
        Assert.Equal(1, e2b.CallCount);
        Assert.Equal(0, egress.CallCount);

        // Manifest: personal tools (effective scope = personal after gate)
        IList<AITool>? tools = e2b.ReceivedOptions?.Tools;
        Assert.NotNull(tools);
        Assert.Single(tools);
        Assert.Equal("personal_tool", ((AIFunction)tools[0]).Name);
    }
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
        RouteDecision decision = new("world", Tier.Direct, Impersonal: true, Confidence: 0.5f);
        (TriageRouter router, _, _, _) = RouterFactory.Build(new ClassifierFake(decision));

        long delta = await MeasureCounterDeltaAsync(() => router.GetResponseAsync(RouterFactory.AMessage()));

        Assert.Equal(1, delta);
    }

    [Fact]
    public async Task ConfidentPersonal_DoesNotIncrementCounter()
    {
        RouteDecision decision = new("personal", Tier.Direct, Impersonal: false, Confidence: 0.95f);
        (TriageRouter router, _, _, _) = RouterFactory.Build(new ClassifierFake(decision));

        long delta = await MeasureCounterDeltaAsync(() => router.GetResponseAsync(RouterFactory.AMessage()));

        Assert.Equal(0, delta);
    }

    [Fact]
    public async Task HighConfidenceWorld_DoesNotIncrementCounter()
    {
        // High-confidence world goes to egress; no privacy gate fires, so no counter increment.
        RouteDecision decision = new("world", Tier.Direct, Impersonal: true, Confidence: 0.95f);
        (TriageRouter router, _, _, _) = RouterFactory.Build(new ClassifierFake(decision));

        long delta = await MeasureCounterDeltaAsync(() => router.GetResponseAsync(RouterFactory.AMessage()));

        Assert.Equal(0, delta);
    }
}

// ── 11.2 / A1 — Garbage classifier response ──────────────────────────────────

public sealed class ClassifierFailSafeTests
{
    [Fact]
    public async Task GarbageClassifierResponse_FailSafe_RoutesToE2b()
    {
        // A1: unparseable JSON → TryGetResult returns false → fail safe to personal/Direct
        (TriageRouter router, BackendSpy e2b, BackendSpy reasoner, BackendSpy egress) =
            RouterFactory.Build(new ClassifierFake("not valid json at all!!!"));

        await router.GetResponseAsync(RouterFactory.AMessage());

        Assert.Equal(1, e2b.CallCount);
        Assert.Equal(0, reasoner.CallCount);
        Assert.Equal(0, egress.CallCount);
    }

    [Fact]
    public async Task GarbageClassifierResponse_FailSafe_DoesNotIncrementCounter()
    {
        // A1 fail-safe yields confident personal (Confidence: 1f) → no gate fires → no counter
        (TriageRouter router, _, _, _) =
            RouterFactory.Build(new ClassifierFake("not valid json at all!!!"));

        long delta = await MisclassifyCounterTests.MeasureCounterDeltaAsync(
            () => router.GetResponseAsync(RouterFactory.AMessage()));

        Assert.Equal(0, delta);
    }
}

// ── 11.2 + streaming ack (spec R8) ───────────────────────────────────────────

public sealed class StreamingAckTests
{
    [Fact]
    public async Task GetStreamingResponse_YieldsAckBeforeTargetStream()
    {
        RouteDecision decision = new("personal", Tier.Direct, Impersonal: false, Confidence: 0.95f);
        (TriageRouter router, _, _, _) = RouterFactory.Build(new ClassifierFake(decision));

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in router.GetStreamingResponseAsync(RouterFactory.AMessage()))
        {
            updates.Add(update);
        }

        // At least one update (the ack)
        Assert.NotEmpty(updates);
        // First update is the synthetic ack
        Assert.Equal("[triage: e2b]", updates[0].Text);
    }

    [Fact]
    public async Task GetStreamingResponse_EgressRoute_YieldsEgressAck()
    {
        RouteDecision decision = new("world", Tier.Direct, Impersonal: true, Confidence: 0.95f);
        (TriageRouter router, _, _, _) = RouterFactory.Build(new ClassifierFake(decision));

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in router.GetStreamingResponseAsync(RouterFactory.AMessage()))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        Assert.Equal("[triage: egress]", updates[0].Text);
    }

    [Fact]
    public async Task GetStreamingResponse_ReasonerRoute_YieldsReasonerAck()
    {
        RouteDecision decision = new("personal", Tier.Reasoner, Impersonal: false, Confidence: 0.95f);
        (TriageRouter router, _, _, _) = RouterFactory.Build(new ClassifierFake(decision));

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in router.GetStreamingResponseAsync(RouterFactory.AMessage()))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        Assert.Equal("[triage: reasoner]", updates[0].Text);
    }
}

// ── 11.5 — Terminal-client hook via DI builder ────────────────────────────────

public sealed class TerminalClientHookTests
{
    [Fact]
    public void UseTriage_AddReasoner_AddEgress_ProducesTriageRouterAsTerminalClient()
    {
        BackendSpy e2bRaw = new("e2b-raw");
        BackendSpy reasonerRaw = new("reasoner-raw");
        BackendSpy egressRaw = new("egress-raw");

        // Build() is on DmonHostBuilder (concrete), not IDmonHostBuilder.
        // Assign the concrete builder first, then call the Dmon.Hosting extension verbs on it.
        DmonHostBuilder builder = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();

        builder.UseTriage(e2bRaw);
        builder.AddReasoner(reasonerRaw);
        builder.AddEgress(egressRaw);

        DmonBuiltHost host = builder.Build();

        ITerminalClientFactory factory = host.Services.GetRequiredService<ITerminalClientFactory>();
        IChatClient terminalClient = factory.Create(host.Services);

        Assert.IsType<TriageRouter>(terminalClient);
    }

    [Fact]
    public void UseTriage_WithoutReasoner_ThrowsOnCreate()
    {
        // If AddReasoner is missing, GetRequiredService<ReasonerClient> throws.
        BackendSpy e2bRaw = new("e2b-raw");
        BackendSpy egressRaw = new("egress-raw");

        DmonHostBuilder builder = DmonHost.CreateBuilder()
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();

        builder.UseTriage(e2bRaw);
        builder.AddEgress(egressRaw);

        DmonBuiltHost host = builder.Build();

        ITerminalClientFactory factory = host.Services.GetRequiredService<ITerminalClientFactory>();
        Assert.Throws<InvalidOperationException>(() => factory.Create(host.Services));
    }
}
