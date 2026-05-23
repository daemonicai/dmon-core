using Dmon.Abstractions.Providers;
using Dmon.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Providers.Tests;

public sealed class CapabilitiesDecoratorTests
{
    [Fact]
    public void GetService_ChatClientCapabilities_ReturnsInjectedCaps()
    {
        ChatClientCapabilities caps = new() { SupportsToolCalling = true, SupportsReasoning = true };
        FakeInnerClient inner = new();
        CapabilitiesDecorator decorator = new(inner, caps);

        object? result = decorator.GetService(typeof(ChatClientCapabilities));

        Assert.Same(caps, result);
    }

    [Fact]
    public void GetService_OtherType_ForwardsToInnerClient()
    {
        ChatClientCapabilities caps = new();
        object sentinel = new();
        FakeInnerClient inner = new(sentinel);
        CapabilitiesDecorator decorator = new(inner, caps);

        object? result = decorator.GetService(typeof(object));

        Assert.Same(sentinel, result);
    }

    [Fact]
    public void GetService_OtherType_DoesNotReturnCaps()
    {
        ChatClientCapabilities caps = new() { SupportsToolCalling = true };
        FakeInnerClient inner = new();
        CapabilitiesDecorator decorator = new(inner, caps);

        object? result = decorator.GetService(typeof(string));

        Assert.Null(result);
        Assert.IsNotType<ChatClientCapabilities>(result);
    }

    private sealed class FakeInnerClient(object? sentinel = null) : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(object) ? sentinel : null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public void Dispose() { }
    }
}
