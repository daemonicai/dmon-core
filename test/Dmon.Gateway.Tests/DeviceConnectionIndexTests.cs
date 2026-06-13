using Dmon.Gateway.Sessions;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 5.3 — DeviceConnectionIndex unit tests and SessionHandler integration tests.
///
/// DeviceConnectionIndex direct tests: add/remove idempotency, snapshot isolation.
/// SessionHandler integration tests: index maintained on attach, detach, eviction, multi-session,
/// and null-keyId connections (never indexed).
/// </summary>
public sealed class DeviceConnectionIndexTests
{
    // -------------------------------------------------------------------------
    // DeviceConnectionIndex unit tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Add_ThenGetConnections_ContainsConnection()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection conn = new("k1");

        index.Add("k1", conn);

        IReadOnlyCollection<IGatewayConnection> result = index.GetConnections("k1");
        Assert.Contains(conn, result);
    }

    [Fact]
    public void Remove_AfterAdd_LeavesEmptyResult()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection conn = new("k1");

        index.Add("k1", conn);
        index.Remove("k1", conn);

        Assert.Empty(index.GetConnections("k1"));
    }

    [Fact]
    public void Remove_IsIdempotent_WhenConnectionAbsent()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection conn = new("k1");

        // Remove without Add must not throw.
        index.Remove("k1", conn);
        // Remove again is also a no-op.
        index.Remove("k1", conn);

        Assert.Empty(index.GetConnections("k1"));
    }

    [Fact]
    public void GetConnections_ReturnsEmpty_ForUnknownKeyId()
    {
        DeviceConnectionIndex index = new();

        Assert.Empty(index.GetConnections("no-such-key"));
    }

    [Fact]
    public void GetConnections_ReturnsSnapshot_NotLiveView()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection connA = new("k1");
        KeyedConnection connB = new("k1");

        index.Add("k1", connA);
        IReadOnlyCollection<IGatewayConnection> snapshot = index.GetConnections("k1");

        // Mutate the index after taking the snapshot.
        index.Add("k1", connB);
        index.Remove("k1", connA);

        // Snapshot must be unaffected.
        Assert.Contains(connA, snapshot);
        Assert.DoesNotContain(connB, snapshot);
    }

    [Fact]
    public void MultipleConnections_SameKeyId_AllPresent()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection connA = new("k1");
        KeyedConnection connB = new("k1");

        index.Add("k1", connA);
        index.Add("k1", connB);

        IReadOnlyCollection<IGatewayConnection> result = index.GetConnections("k1");
        Assert.Contains(connA, result);
        Assert.Contains(connB, result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Remove_OnlyClearsTarget_LeavesOtherConnection()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection connA = new("k1");
        KeyedConnection connB = new("k1");

        index.Add("k1", connA);
        index.Add("k1", connB);
        index.Remove("k1", connA);

        IReadOnlyCollection<IGatewayConnection> result = index.GetConnections("k1");
        Assert.DoesNotContain(connA, result);
        Assert.Contains(connB, result);
    }

    // -------------------------------------------------------------------------
    // SessionHandler integration tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Attach_AddsConnectionToIndex()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection conn = new("k1");

        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", new SessionHandlerTestOptions { Stdout = stdout, Stdin = stdin, ConnectionIndex = index });

        handler.Attach(conn, 0);

        Assert.Contains(conn, index.GetConnections("k1"));
    }

    [Fact]
    public async Task Detach_RemovesConnectionFromIndex()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection conn = new("k1");

        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", new SessionHandlerTestOptions { Stdout = stdout, Stdin = stdin, ConnectionIndex = index });

        handler.Attach(conn, 0);
        handler.Detach(conn);

        Assert.Empty(index.GetConnections("k1"));
    }

    [Fact]
    public async Task Attach_Eviction_RemovesEvictedConnection_Atomically()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection connA = new("k1");
        KeyedConnection connB = new("k1");

        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", new SessionHandlerTestOptions { Stdout = stdout, Stdin = stdin, ConnectionIndex = index });

        handler.Attach(connA, 0);
        // Second attach evicts connA atomically: both swap and index update happen inside _lock.
        handler.Attach(connB, 0);

        IReadOnlyCollection<IGatewayConnection> result = index.GetConnections("k1");
        // connA was evicted — it must not be in the index even before its forwarding loop calls
        // Detach(connA). Detach will call Remove again (idempotent no-op).
        Assert.DoesNotContain(connA, result);
        Assert.Contains(connB, result);
    }

    [Fact]
    public async Task Detach_EvictedConnection_IsIdempotent()
    {
        // Simulates the evicted loop calling Detach after Attach already removed it from the index.
        DeviceConnectionIndex index = new();
        KeyedConnection connA = new("k1");
        KeyedConnection connB = new("k1");

        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", new SessionHandlerTestOptions { Stdout = stdout, Stdin = stdin, ConnectionIndex = index });

        handler.Attach(connA, 0);
        handler.Attach(connB, 0); // evicts connA; removes connA from index inside _lock

        // Now the "evicted loop" calls Detach(connA) — must be a safe no-op.
        handler.Detach(connA);

        IReadOnlyCollection<IGatewayConnection> result = index.GetConnections("k1");
        Assert.DoesNotContain(connA, result);
        Assert.Contains(connB, result);
    }

    [Fact]
    public async Task MultiSession_SameKeyId_SharedIndex_ContainsBoth()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection connA = new("k1");
        KeyedConnection connB = new("k1");

        FeedableReader stdoutA = new();
        FeedableReader stdoutB = new();
        StringWriter stdinA = new();
        StringWriter stdinB = new();

        await using SessionHandler handlerA = new("sA", new SessionHandlerTestOptions { Stdout = stdoutA, Stdin = stdinA, ConnectionIndex = index });
        await using SessionHandler handlerB = new("sB", new SessionHandlerTestOptions { Stdout = stdoutB, Stdin = stdinB, ConnectionIndex = index });

        handlerA.Attach(connA, 0);
        handlerB.Attach(connB, 0);

        IReadOnlyCollection<IGatewayConnection> result = index.GetConnections("k1");
        Assert.Contains(connA, result);
        Assert.Contains(connB, result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task MultiSession_DetachOne_LeavesOtherIndexed()
    {
        DeviceConnectionIndex index = new();
        KeyedConnection connA = new("k1");
        KeyedConnection connB = new("k1");

        FeedableReader stdoutA = new();
        FeedableReader stdoutB = new();
        StringWriter stdinA = new();
        StringWriter stdinB = new();

        await using SessionHandler handlerA = new("sA", new SessionHandlerTestOptions { Stdout = stdoutA, Stdin = stdinA, ConnectionIndex = index });
        await using SessionHandler handlerB = new("sB", new SessionHandlerTestOptions { Stdout = stdoutB, Stdin = stdinB, ConnectionIndex = index });

        handlerA.Attach(connA, 0);
        handlerB.Attach(connB, 0);

        handlerA.Detach(connA);

        IReadOnlyCollection<IGatewayConnection> result = index.GetConnections("k1");
        Assert.DoesNotContain(connA, result);
        Assert.Contains(connB, result);
    }

    [Fact]
    public async Task NullKeyId_Connection_IsNotIndexed()
    {
        DeviceConnectionIndex index = new();
        // A connection with KeyId == null (auth disabled — AuthorizedNoKey path).
        NullKeyConnection conn = new();

        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", new SessionHandlerTestOptions { Stdout = stdout, Stdin = stdin, ConnectionIndex = index });

        handler.Attach(conn, 0);
        // Index must have no entry at all for any key (we never added a bucket).
        // Detach must also be a clean no-op.
        handler.Detach(conn);
    }

    // -------------------------------------------------------------------------
    // Fake connection implementations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Minimal connection with a non-null KeyId for index tests.
    /// </summary>
    private sealed class KeyedConnection(string keyId) : IGatewayConnection
    {
        public string? KeyId { get; } = keyId;

        public ValueTask SendAsync(string frame, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Abort() { }
    }

    /// <summary>
    /// Minimal connection with a null KeyId (auth-disabled path).
    /// </summary>
    private sealed class NullKeyConnection : IGatewayConnection
    {
        public string? KeyId => null;

        public ValueTask SendAsync(string frame, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Abort() { }
    }

    /// <summary>
    /// A <see cref="TextReader"/> whose <see cref="ReadLineAsync"/> blocks until a line is fed
    /// or <see cref="End"/> is called. Identical seam used in other gateway test classes.
    /// </summary>
    private sealed class FeedableReader : TextReader
    {
        private readonly System.Threading.Channels.Channel<string?> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<string?>();

        public void Feed(string line) => _channel.Writer.TryWrite(line);

        public void End() => _channel.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return null;
            }
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }
}
