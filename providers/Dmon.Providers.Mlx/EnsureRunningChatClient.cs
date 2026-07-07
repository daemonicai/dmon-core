using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Dmon.Providers.Mlx;

// Self-heal wrapper (design.md D2): ensures the MLX runtime is running — attach-first, so a
// healthy runtime adds no meaningful latency, respawning it if it was torn down for idle —
// before every request reaches the inner OpenAI-compatible client. Sits outermost over the
// MlxMaxTokensDefaulter -> CapabilitiesDecorator decorator stack built by MlxProviderFactory.
// Relies entirely on MlxProviderExtension.EnsureRunningAsync's own concurrency gate (design.md
// D3); does not re-implement any gating here. A fault from EnsureRunningAsync propagates
// uninterpreted — this wrapper attempts-then-delegates and never swallows.
internal sealed class EnsureRunningChatClient : DelegatingChatClient
{
    private readonly MlxProviderExtension _extension;

    public EnsureRunningChatClient(IChatClient inner, MlxProviderExtension extension)
        : base(inner)
    {
        _extension = extension;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _extension.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _extension.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        await foreach (ChatResponseUpdate update in base
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }
}
