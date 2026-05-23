using Dmon.Core.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Providers;

internal sealed class CapabilitiesDecorator(IChatClient inner, ChatClientCapabilities caps) : IChatClient
{
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientCapabilities) ? caps : inner.GetService(serviceType, serviceKey);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => inner.GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    public void Dispose() => inner.Dispose();
}
