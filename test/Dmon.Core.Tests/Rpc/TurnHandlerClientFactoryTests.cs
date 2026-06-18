using System.Runtime.CompilerServices;
using Dmon.Abstractions.Hosting;
using Dmon.Core.Rpc;
using Dmon.Protocol.Commands;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dmon.Core.Tests.Rpc;

// ---------------------------------------------------------------------------
// Stub helpers (file-scoped — not visible outside this file)
// ---------------------------------------------------------------------------

/// <summary>
/// IChatClient that records whether it was called (via GetStreamingResponseAsync).
/// </summary>
file sealed class TrackingChatClient : IChatClient
{
    public bool WasCalled { get; private set; }
    private readonly string _text;

    public TrackingChatClient(string text = "tracking response")
    {
        _text = text;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, _text)]));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, _text);
    }

    public void Dispose() { }
}

/// <summary>
/// ITerminalClientFactory that always returns the given sentinel client.
/// </summary>
file sealed class SentinelTerminalClientFactory : ITerminalClientFactory
{
    private readonly IChatClient _sentinel;

    public SentinelTerminalClientFactory(IChatClient sentinel)
    {
        _sentinel = sentinel;
    }

    public IChatClient Create(IServiceProvider services) => _sentinel;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class TurnHandlerClientFactoryTests
{
    // 4.2a — factory present: the sentinel (factory output) receives the chat call.
    [Fact]
    public async Task Submit_WithFactory_UsesSentinelClientNotProviderRegistry()
    {
        TrackingChatClient sentinel = new("sentinel response");
        TrackingChatClient providerClient = new("provider response");

        SentinelTerminalClientFactory factory = new(sentinel);
        IServiceProvider sp = new ServiceCollection().BuildServiceProvider();

        // Build a TurnHandler with a factory + serviceProvider.
        // TurnHandlerFactory.Create uses the provider-registry path; we build directly here.
        (TurnHandler handler, TestEventEmitter emitter) = TurnHandlerFactory.Create(
            providers: new StubProviderRegistry(providerClient),
            terminalClientFactory: factory,
            serviceProvider: sp);

        TurnSubmitCommand cmd = new() { Id = "req-factory", Message = "Hello" };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        Assert.True(sentinel.WasCalled, "Sentinel (factory output) must have received the chat call.");
        Assert.False(providerClient.WasCalled, "Provider-registry client must NOT be called when a factory is present.");
    }

    // 4.2b — factory absent: the provider-registry path is unchanged.
    [Fact]
    public async Task Submit_WithoutFactory_UsesProviderRegistry()
    {
        TrackingChatClient providerClient = new("provider response");

        // No factory — use the standard two-arg helper.
        (TurnHandler handler, TestEventEmitter emitter) = TurnHandlerFactory.Create(providerClient);

        TurnSubmitCommand cmd = new() { Id = "req-no-factory", Message = "Hello" };
        await handler.SubmitAsync(cmd, CancellationToken.None);

        Assert.True(providerClient.WasCalled, "Provider-registry client must receive the chat call when no factory is set.");
    }

    // Reviewer carry-forward: ctor guard — factory without serviceProvider throws.
    [Fact]
    public void Ctor_FactoryWithoutServiceProvider_ThrowsArgumentNullException()
    {
        TrackingChatClient providerClient = new();
        SentinelTerminalClientFactory factory = new(new TrackingChatClient());

        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
            TurnHandlerFactory.Create(
                providers: new StubProviderRegistry(providerClient),
                terminalClientFactory: factory,
                serviceProvider: null));

        Assert.Equal("serviceProvider", ex.ParamName);
    }
}
