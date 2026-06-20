using System.Runtime.CompilerServices;
using Dmon.Core.Extensions;
using Microsoft.Extensions.AI;

namespace Daemon.Routing;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that classifies each turn and dispatches to
/// the appropriate backend (e2b-with-tools, reasoner, or egress) based on the
/// classifier's <see cref="RouteDecision"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per-turn dispatch order (R5):
/// <list type="bullet">
/// <item><description>Egress — when raw scope is <c>"world"</c>, <c>Impersonal</c> is true, and <c>Confidence &gt; EgressThreshold</c>.</description></item>
/// <item><description>Reasoner — when <c>Tier == Tier.Reasoner</c> (any scope, any confidence).</description></item>
/// <item><description>E2b-with-tools — all other turns.</description></item>
/// </list>
/// </para>
/// <para>
/// Privacy invariant: when <c>Confidence &lt; EgressThreshold</c>, the effective scope is
/// forced to <c>"personal"</c> before building the tool manifest and the
/// <c>dmon.triage.misclassify.personal_to_world</c> counter is incremented.
/// </para>
/// </remarks>
public sealed class TriageRouter : DelegatingChatClient
{
    private readonly IChatClient _classifier;
    private readonly IChatClient _e2bWithTools;
    private readonly IChatClient _reasoner;
    private readonly IChatClient _egress;
    private readonly AbilityRegistry _abilities;
    private readonly TriageOptions _options;

    /// <summary>
    /// Initialises the router with its four backend clients, the ability registry,
    /// and the triage options.
    /// </summary>
    /// <param name="classifier">Raw e2b client used for structured-output classification (no tools).</param>
    /// <param name="e2bWithTools">E2b client wrapped with function invocation for tool-bearing turns.</param>
    /// <param name="reasoner">The slow, high-capability reasoner backend.</param>
    /// <param name="egress">The provider-agnostic egress backend for impersonal world turns.</param>
    /// <param name="abilities">Per-turn scope-gated tool manifest.</param>
    /// <param name="options">Configurable knobs (e.g. <see cref="TriageOptions.EgressThreshold"/>).</param>
    public TriageRouter(
        IChatClient classifier,
        IChatClient e2bWithTools,
        IChatClient reasoner,
        IChatClient egress,
        AbilityRegistry abilities,
        TriageOptions options)
        // Pass e2bWithTools as the inner so DelegatingChatClient.Dispose / metadata flow correctly.
        : base(e2bWithTools)
    {
        _classifier = classifier;
        _e2bWithTools = e2bWithTools;
        _reasoner = reasoner;
        _egress = egress;
        _abilities = abilities;
        _options = options;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        (IChatClient target, ChatOptions scopedOptions) = await ClassifyAndRouteAsync(messages, options, cancellationToken);
        return await target.GetResponseAsync(messages, scopedOptions, cancellationToken);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        (IChatClient target, ChatOptions scopedOptions) = await ClassifyAndRouteAsync(messages, options, cancellationToken);

        // Yield one synthetic ack before forwarding the target's stream (spec R8).
        string ackText = BuildAckText(target);
        yield return new ChatResponseUpdate(ChatRole.Assistant, ackText);

        await foreach (ChatResponseUpdate update in target.GetStreamingResponseAsync(messages, scopedOptions, cancellationToken))
        {
            yield return update;
        }
    }

    // --- Shared private helpers ---

    /// <summary>
    /// Performs the classify pass, applies the privacy gate, builds the scoped manifest,
    /// and returns the selected backend and the options copy to use.
    /// Both response paths call this — the gate and counter logic are in one place.
    /// </summary>
    private async Task<(IChatClient Target, ChatOptions ScopedOptions)> ClassifyAndRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // R3: fresh classify pass per turn, no caching.
        ChatResponse<RouteDecision> classifyResponse = await _classifier.GetResponseAsync<RouteDecision>(
            messages,
            cancellationToken: cancellationToken);
        // A1: Result throws JsonException on unparseable JSON (never returns null in M.E.AI 10.5+).
        // TryGetResult returns false without throwing — fail safe to confident-personal (R4).
        RouteDecision raw = classifyResponse.TryGetResult(out RouteDecision? parsed) && parsed is not null
            ? parsed
            : new RouteDecision("personal", Tier.Direct, Impersonal: false, Confidence: 1f);

        // R4/R7: privacy gate — override world→personal when confidence is below threshold.
        string effectiveScope = raw.Scope;
        if (raw.Confidence < _options.EgressThreshold &&
            string.Equals(raw.Scope, "world", StringComparison.OrdinalIgnoreCase))
        {
            effectiveScope = "personal";
            DaemonTelemetry.RecordPersonalToWorldMisclassification();
        }

        // B1: Clone preserves all caller options (including derived-type state and
        // provider-specific RawRepresentationFactory), creates a fresh Tools list,
        // and guarantees the caller's instance is never mutated.
        // The privacy invariant: Tools is always built from the EFFECTIVE (post-gate) scope.
        ChatOptions scopedOptions = options?.Clone() ?? new ChatOptions();
        scopedOptions.Tools = _abilities.ForScope(effectiveScope);

        // R5: backend selection reads the RAW decision (not the effective scope).
        IChatClient target = raw switch
        {
            { Scope: var s, Impersonal: true, Confidence: var c }
                when string.Equals(s, "world", StringComparison.OrdinalIgnoreCase)
                     && c > _options.EgressThreshold => _egress,
            { Tier: Tier.Reasoner } => _reasoner,
            _ => _e2bWithTools,
        };

        return (target, scopedOptions);
    }

    private string BuildAckText(IChatClient target) =>
        target == _egress    ? "[triage: egress]" :
        target == _reasoner  ? "[triage: reasoner]" :
                               "[triage: e2b]";
}
