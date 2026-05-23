using System.Net;
using System.Runtime.CompilerServices;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Providers;

/// <summary>
/// Throws HttpRequestException with status 429 on the first N calls, then succeeds.
/// </summary>
internal sealed class FailThenSucceedChatClient : IChatClient
{
    private readonly int _failCount;
    private int _callCount;

    public FailThenSucceedChatClient(int failCount)
    {
        _failCount = failCount;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _callCount);
        if (call <= _failCount)
            throw new HttpRequestException("Rate limited.", null, HttpStatusCode.TooManyRequests);

        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _callCount);
        if (call <= _failCount)
            throw new HttpRequestException("Rate limited.", null, HttpStatusCode.TooManyRequests);

        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }

    public void Dispose() { }
}

/// <summary>
/// Captures emitted events in-memory.
/// </summary>
internal sealed class RetryTestEventEmitter : IEventEmitter
{
    private readonly List<Event> _events = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<Event> Events => _events;

    public async Task EmitAsync<T>(T evt, CancellationToken cancellationToken = default) where T : Event
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _events.Add(evt); }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// Throws a 429 with Data["Retry-After"] = retryAfterSeconds on the first call, then succeeds.
/// </summary>
internal sealed class RetryAfterChatClient : IChatClient
{
    private readonly int _retryAfterSeconds;
    private int _callCount;

    public RetryAfterChatClient(int retryAfterSeconds) { _retryAfterSeconds = retryAfterSeconds; }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _callCount) == 1)
        {
            HttpRequestException ex = new("Rate limited.", null, HttpStatusCode.TooManyRequests);
            ex.Data["Retry-After"] = _retryAfterSeconds;
            throw ex;
        }
        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }

    public void Dispose() { }
}

public sealed class RetryingChatClientTests
{
    private static RetryPolicy ShortPolicy(int maxAttempts) => new()
    {
        MaxAttempts = maxAttempts,
        BaseDelay = TimeSpan.FromMilliseconds(10),
        MaxDelay = TimeSpan.FromMilliseconds(50)
    };

    [Fact]
    public async Task GetResponseAsync_RetriesAndSucceeds_AfterTransientFailures()
    {
        int failCount = 2;
        FailThenSucceedChatClient inner = new(failCount);
        RetryTestEventEmitter emitter = new();
        RetryingChatClient client = new(inner, ShortPolicy(maxAttempts: 5), emitter);

        ChatResponse response = await client.GetResponseAsync([], cancellationToken: CancellationToken.None);

        Assert.Equal("ok", response.Messages[0].Text);

        IReadOnlyList<Event> events = emitter.Events;
        int retryEvents = events.Count(e => e is RetryAttemptEvent);
        Assert.Equal(failCount, retryEvents);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_YieldsImmediately_DoesNotBuffer()
    {
        FailThenSucceedChatClient inner = new(failCount: 0);
        RetryTestEventEmitter emitter = new();
        RetryingChatClient client = new(inner, ShortPolicy(maxAttempts: 3), emitter);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([]))
        {
            updates.Add(update);
        }

        Assert.Single(updates);
        Assert.Equal("ok", updates[0].Text);
        // No retries should have fired because the call succeeded immediately.
        Assert.Empty(emitter.Events.OfType<RetryAttemptEvent>());
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RetriesAndSucceeds_EmitsRetryAttemptEvents()
    {
        int failCount = 2;
        FailThenSucceedChatClient inner = new(failCount);
        RetryTestEventEmitter emitter = new();
        RetryingChatClient client = new(inner, ShortPolicy(maxAttempts: 5), emitter);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([]))
        {
            updates.Add(update);
        }

        Assert.Single(updates);
        Assert.Equal("ok", updates[0].Text);

        int retryEvents = emitter.Events.Count(e => e is RetryAttemptEvent);
        Assert.Equal(failCount, retryEvents);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ExhaustsRetries_ThrowsLastException()
    {
        int failCount = 10;
        FailThenSucceedChatClient inner = new(failCount);
        RetryTestEventEmitter emitter = new();
        RetryingChatClient client = new(inner, ShortPolicy(maxAttempts: 3), emitter);

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([])) { }
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task GetResponseAsync_HonoursRetryAfterDataKey_OverridesComputedDelay()
    {
        // A client that throws a 429 with Data["Retry-After"] = 0 (instant retry).
        RetryAfterChatClient inner = new(retryAfterSeconds: 0);
        RetryTestEventEmitter emitter = new();
        RetryingChatClient client = new(inner, ShortPolicy(maxAttempts: 3), emitter);

        ChatResponse response = await client.GetResponseAsync([], cancellationToken: CancellationToken.None);

        Assert.Equal("ok", response.Messages[0].Text);
        RetryAttemptEvent? retryEvent = emitter.Events.OfType<RetryAttemptEvent>().FirstOrDefault();
        Assert.NotNull(retryEvent);
        // Retry-After: 0 → NextDelayMs must be 0, not the exponential backoff (~10ms).
        Assert.Equal(0, retryEvent.NextDelayMs);
    }
}
