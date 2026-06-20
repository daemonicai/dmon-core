using System.Runtime.CompilerServices;
using Dmon.Core.Extensions;
using Microsoft.Extensions.AI;

namespace Daemon.Routing;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that classifies each turn and dispatches to
/// the appropriate backend (first-line, escalation, or egress) using the ADR-032
/// handler-initiated escalation ladder.
/// </summary>
/// <remarks>
/// <para>
/// Per-turn dispatch order:
/// <list type="bullet">
/// <item><description>Egress — when raw scope is <c>"world"</c>, <c>Impersonal</c> is true, and <c>Confidence &gt; EgressThreshold</c>.</description></item>
/// <item><description>First-line — all other turns; offered the scoped manifest plus the <c>think_harder</c> signal tool.</description></item>
/// <item><description>Escalation — when first-line response includes a <c>think_harder</c> function call.</description></item>
/// </list>
/// </para>
/// <para>
/// Privacy invariant: when <c>Confidence &lt; EgressThreshold</c> and raw scope is <c>"world"</c>,
/// the effective scope is forced to <c>"personal"</c> before building the tool manifest and the
/// <c>dmon.triage.misclassify.personal_to_world</c> counter is incremented.
/// </para>
/// <para>
/// Backends are resolved lazily on first use (one instance per backend per router lifetime,
/// concurrency-safe via <see cref="Lazy{T}"/>). The router owns the resolved backends and
/// disposes them on teardown.
/// </para>
/// </remarks>
public sealed class TriageRouter : DelegatingChatClient
{
    private const string AckText = "[triage]";
    private const string EscalationMarker = "[escalating]";
    private const string ThinkHarderName = "think_harder";

    private readonly Lazy<Task<IChatClient>> _lazyFirstLine;
    private readonly Lazy<Task<IChatClient>> _lazyEscalation;
    private readonly Lazy<Task<IChatClient>> _lazyEgress;
    private readonly AbilityRegistry _abilities;
    private readonly TriageOptions _options;

    // The think_harder AIFunction: sets FunctionInvocationContext.Terminate and returns a sentinel.
    // Offered to first-line only; never to escalation or egress.
    private static readonly AIFunction ThinkHarder = AIFunctionFactory.Create(
        () =>
        {
            FunctionInvocationContext? ctx = FunctionInvokingChatClient.CurrentContext;
            if (ctx is not null)
            {
                ctx.Terminate = true;
            }
            return "escalating";
        },
        ThinkHarderName,
        "Signal that this turn requires escalation to a more capable backend.");

    /// <summary>
    /// Constructs the router with lazy backend delegates and ability registry.
    /// No I/O is performed at construction time.
    /// </summary>
    /// <param name="firstLineFactory">Delegate that resolves the first-line client. Invoked at most once.</param>
    /// <param name="escalationFactory">Delegate that resolves the escalation client. Invoked at most once.</param>
    /// <param name="egressFactory">Delegate that resolves the egress client. Invoked at most once.</param>
    /// <param name="services">Service provider passed to the factory delegates.</param>
    /// <param name="abilities">Per-turn scope-gated tool manifest.</param>
    /// <param name="options">Configurable knobs (e.g. <see cref="TriageOptions.EgressThreshold"/>).</param>
    public TriageRouter(
        Func<IServiceProvider, ValueTask<IChatClient>> firstLineFactory,
        Func<IServiceProvider, ValueTask<IChatClient>> escalationFactory,
        Func<IServiceProvider, ValueTask<IChatClient>> egressFactory,
        IServiceProvider services,
        AbilityRegistry abilities,
        TriageOptions options)
        // Use a no-op placeholder as the DelegatingChatClient inner; actual dispatch goes to lazily-resolved clients.
        : base(new NopChatClient())
    {
        _lazyFirstLine = new Lazy<Task<IChatClient>>(
            () => firstLineFactory(services).AsTask(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _lazyEscalation = new Lazy<Task<IChatClient>>(
            () => escalationFactory(services).AsTask(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _lazyEgress = new Lazy<Task<IChatClient>>(
            () => egressFactory(services).AsTask(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _abilities = abilities;
        _options = options;
    }

    /// <summary>
    /// Test constructor that accepts pre-resolved clients directly.
    /// Backends are wrapped as already-completed tasks; no I/O occurs.
    /// </summary>
    internal TriageRouter(
        IChatClient firstLine,
        IChatClient escalation,
        IChatClient egress,
        AbilityRegistry abilities,
        TriageOptions options)
        : this(
            _ => ValueTask.FromResult(firstLine),
            _ => ValueTask.FromResult(escalation),
            _ => ValueTask.FromResult(egress),
            new NopServiceProvider(),
            abilities,
            options)
    {
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        (RouteDecision raw, string effectiveScope) = await ClassifyAsync(messages, cancellationToken);

        // Egress path: stream directly after ack (no escalation logic).
        if (IsEgressEligible(raw))
        {
            IChatClient egress = await _lazyEgress.Value;
            ChatOptions egressOptions = BuildOptions(options, effectiveScope, includeThinkHarder: false);
            return await egress.GetResponseAsync(messages, egressOptions, cancellationToken);
        }

        // First-line path: offer think_harder in the manifest.
        IChatClient firstLineRaw = await _lazyFirstLine.Value;
        IChatClient firstLineWithFic = firstLineRaw
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
        ChatOptions firstLineOptions = BuildOptions(options, effectiveScope, includeThinkHarder: true);
        ChatResponse firstLineResponse = await firstLineWithFic.GetResponseAsync(messages, firstLineOptions, cancellationToken);

        // Detect escalation: did first-line call think_harder?
        if (!TryFindThinkHarderCalls(firstLineResponse.Messages, out HashSet<string> thinkHarderCallIds))
        {
            // No escalation — return the first-line answer directly.
            return firstLineResponse;
        }

        // Escalation path.
        IList<ChatMessage> inheritedMessages = BuildInheritedMessages(messages, firstLineResponse.Messages, thinkHarderCallIds);
        IChatClient escalation = await _lazyEscalation.Value;
        ChatOptions escalationOptions = BuildOptions(options, effectiveScope, includeThinkHarder: false);
        return await escalation.GetResponseAsync(inheritedMessages, escalationOptions, cancellationToken);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Yield the ack before any backend output. Does not name the final route.
        yield return new ChatResponseUpdate(ChatRole.Assistant, AckText);

        (RouteDecision raw, string effectiveScope) = await ClassifyAsync(messages, cancellationToken);

        // Egress path: stream directly (no buffering needed).
        if (IsEgressEligible(raw))
        {
            IChatClient egress = await _lazyEgress.Value;
            ChatOptions egressOptions = BuildOptions(options, effectiveScope, includeThinkHarder: false);
            await foreach (ChatResponseUpdate update in egress.GetStreamingResponseAsync(messages, egressOptions, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        // First-line path: run as non-streaming to allow buffering for possible escalation.
        // D5: first-line tokens are never streamed directly; only the committed backend streams.
        IChatClient firstLineRaw = await _lazyFirstLine.Value;
        IChatClient firstLineWithFic = firstLineRaw
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
        ChatOptions firstLineOptions = BuildOptions(options, effectiveScope, includeThinkHarder: true);
        ChatResponse firstLineResponse = await firstLineWithFic.GetResponseAsync(messages, firstLineOptions, cancellationToken);

        if (!TryFindThinkHarderCalls(firstLineResponse.Messages, out HashSet<string> thinkHarderCallIds))
        {
            // No escalation — replay the buffered first-line answer as streaming updates.
            // The replay is text-only by design: V1 first-line turns produce text answers,
            // so any non-text final content is intentionally not re-emitted on the streaming path.
            foreach (ChatMessage msg in firstLineResponse.Messages)
            {
                foreach (AIContent content in msg.Contents)
                {
                    if (content is TextContent textContent)
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, textContent.Text);
                    }
                }
            }
            yield break;
        }

        // Escalation path: emit the marker, then stream from the escalation client.
        yield return new ChatResponseUpdate(ChatRole.Assistant, EscalationMarker);

        IList<ChatMessage> inheritedMessages = BuildInheritedMessages(messages, firstLineResponse.Messages, thinkHarderCallIds);
        IChatClient escalation = await _lazyEscalation.Value;
        ChatOptions escalationOptions = BuildOptions(options, effectiveScope, includeThinkHarder: false);
        await foreach (ChatResponseUpdate update in escalation.GetStreamingResponseAsync(inheritedMessages, escalationOptions, cancellationToken))
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Dispose resolved backends only. Do not force-resolve un-started lazies.
            // The inner NopChatClient is disposed by base.Dispose(disposing) — do not double-dispose it.
            if (_lazyFirstLine.IsValueCreated && _lazyFirstLine.Value.IsCompletedSuccessfully)
            {
                _lazyFirstLine.Value.Result.Dispose();
            }
            if (_lazyEscalation.IsValueCreated && _lazyEscalation.Value.IsCompletedSuccessfully)
            {
                _lazyEscalation.Value.Result.Dispose();
            }
            if (_lazyEgress.IsValueCreated && _lazyEgress.Value.IsCompletedSuccessfully)
            {
                _lazyEgress.Value.Result.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    // --- Private helpers ---

    private async Task<(RouteDecision Raw, string EffectiveScope)> ClassifyAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        IChatClient classifier = await _lazyFirstLine.Value;
        ChatResponse<RouteDecision> classifyResponse = await classifier.GetResponseAsync<RouteDecision>(
            messages,
            cancellationToken: cancellationToken);

        RouteDecision raw = classifyResponse.TryGetResult(out RouteDecision? parsed) && parsed is not null
            ? parsed
            : new RouteDecision("personal", Impersonal: false, Confidence: 1f);

        string effectiveScope = raw.Scope;
        if (raw.Confidence < _options.EgressThreshold &&
            string.Equals(raw.Scope, "world", StringComparison.OrdinalIgnoreCase))
        {
            effectiveScope = "personal";
            DaemonTelemetry.RecordPersonalToWorldMisclassification();
        }

        return (raw, effectiveScope);
    }

    private bool IsEgressEligible(RouteDecision raw) =>
        raw.Impersonal &&
        string.Equals(raw.Scope, "world", StringComparison.OrdinalIgnoreCase) &&
        raw.Confidence > _options.EgressThreshold;

    private ChatOptions BuildOptions(ChatOptions? caller, string effectiveScope, bool includeThinkHarder)
    {
        ChatOptions opts = caller?.Clone() ?? new ChatOptions();
        IList<AITool> scopedTools = _abilities.ForScope(effectiveScope);
        if (includeThinkHarder)
        {
            List<AITool> tools = new(scopedTools) { ThinkHarder };
            opts.Tools = tools;
        }
        else
        {
            opts.Tools = scopedTools;
        }
        return opts;
    }

    /// <summary>
    /// Scans <paramref name="responseMessages"/> for <c>think_harder</c>
    /// <see cref="FunctionCallContent"/>s, collecting the <c>CallId</c> of every
    /// such call into <paramref name="callIds"/>. Returns <see langword="true"/> if
    /// at least one was found (escalation requested). Collecting the full set keeps
    /// the strip correct even if the model violates the call-alone rule and emits
    /// <c>think_harder</c> more than once with distinct call ids.
    /// </summary>
    private static bool TryFindThinkHarderCalls(IEnumerable<ChatMessage> responseMessages, out HashSet<string> callIds)
    {
        callIds = [];
        bool found = false;
        foreach (ChatMessage msg in responseMessages)
        {
            foreach (AIContent content in msg.Contents)
            {
                if (content is FunctionCallContent fcc && fcc.Name == ThinkHarderName)
                {
                    found = true;
                    if (fcc.CallId is not null)
                    {
                        callIds.Add(fcc.CallId);
                    }
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Builds the inherited message list for the escalation client:
    /// original input messages + first-line response messages, with every
    /// <c>think_harder</c> <see cref="FunctionCallContent"/> and every matching
    /// <see cref="FunctionResultContent"/> removed (results matched by the set of
    /// <c>think_harder</c> <c>CallId</c>s, so no result is left dangling even when
    /// the model emits multiple <c>think_harder</c> calls).
    /// </summary>
    private static IList<ChatMessage> BuildInheritedMessages(
        IEnumerable<ChatMessage> inputMessages,
        IEnumerable<ChatMessage> firstLineMessages,
        IReadOnlySet<string> thinkHarderCallIds)
    {
        List<ChatMessage> result = [];
        result.AddRange(inputMessages);

        foreach (ChatMessage msg in firstLineMessages)
        {
            // Filter out think_harder calls and their results from message contents.
            List<AIContent> filteredContents = [];
            foreach (AIContent content in msg.Contents)
            {
                bool skip = content is FunctionCallContent fcc && fcc.Name == ThinkHarderName
                         || content is FunctionResultContent frc && frc.CallId is not null && thinkHarderCallIds.Contains(frc.CallId);
                if (!skip)
                {
                    filteredContents.Add(content);
                }
            }
            // Include the message only if it still has content after filtering.
            if (filteredContents.Count > 0)
            {
                result.Add(new ChatMessage(msg.Role, filteredContents));
            }
        }

        return result;
    }

    // Minimal no-op IChatClient used as DelegatingChatClient's required inner client.
    // The router never delegates to it — all dispatch goes through the lazy backends.
    private sealed class NopChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("nop", null, null);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("TriageRouter should never delegate to its inner NopChatClient.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("TriageRouter should never delegate to its inner NopChatClient.");

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    // Minimal no-op IServiceProvider for use in the internal test constructor.
    private sealed class NopServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
