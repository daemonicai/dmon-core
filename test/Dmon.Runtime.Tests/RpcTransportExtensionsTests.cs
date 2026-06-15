using System.Text.Json;
using System.Threading.Channels;
using Dmon.Protocol;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;

namespace Dmon.Runtime.Tests;

/// <summary>
/// Unit tests for <see cref="RpcTransportExtensions.RequestAsync"/>.
///
/// Covers:
///   (a) Returns the correlated <see cref="ResultEvent"/> and does NOT read past it —
///       a line fed after the match is still readable from the same reader.
///   (b) A <see cref="CommandErrorEvent"/> with the matching CommandId is returned (not thrown).
///   (c) Timeout faults <see cref="RpcTimeoutException"/>.
///   (d) Caller cancellation faults <see cref="OperationCanceledException"/>, distinct from timeout.
/// </summary>
public sealed class RpcTransportExtensionsTests
{
    // ---------------------------------------------------------------
    // (a) Returns correlated result; does NOT read past the match
    // ---------------------------------------------------------------

    /// <summary>
    /// (a) After the helper returns the matched result, the underlying reader must still
    /// have the next line available — i.e. break left it unread (zero read-ahead).
    /// </summary>
    [Fact]
    public async Task RequestAsync_ReturnsCorrelatedResult_DoesNotReadPastMatch()
    {
        // Arrange: pre-feed two events into a FeedableReader.
        // The first is the correlated result; the second must remain unread.
        FeedableReader reader = new();
        CapturingWriter writer = new();

        string matchLine = SerializeEvent(MakeSessionCreated("cmd-1"));
        const string postMatchLine = """{"type":"agentStart"}""";

        reader.Feed(matchLine);
        reader.Feed(postMatchLine);

        CoreProcessRpcTransport transport = new(reader, writer);
        SessionCreateCommand command = new() { Id = "cmd-1" };

        // Act.
        ResultEvent result = await transport.RequestAsync(
            command, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert: correct result type and CommandId.
        SessionCreatedResultEvent created = Assert.IsType<SessionCreatedResultEvent>(result);
        Assert.Equal("cmd-1", created.CommandId);

        // Assert: the post-match line is still readable from the same underlying reader.
        // If the helper had read ahead it would have consumed postMatchLine from the channel,
        // and this ReadLineAsync would block (or return a different value).
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        string? nextLine = await reader.ReadLineAsync(cts.Token);
        Assert.Equal(postMatchLine, nextLine);
    }

    /// <summary>
    /// (a variant) Non-matching events (different CommandId) are skipped; the helper
    /// continues to the matching result.
    /// </summary>
    [Fact]
    public async Task RequestAsync_SkipsNonMatchingEvents_ThenReturnsMatch()
    {
        FeedableReader reader = new();
        CapturingWriter writer = new();

        // Feed two events with wrong ids, then the matching one.
        reader.Feed(SerializeEvent(MakeSessionCreated("other-cmd")));
        reader.Feed(SerializeEvent(MakeSessionCreated("also-wrong")));
        reader.Feed(SerializeEvent(MakeSessionCreated("cmd-target")));

        CoreProcessRpcTransport transport = new(reader, writer);
        SessionCreateCommand command = new() { Id = "cmd-target" };

        ResultEvent result = await transport.RequestAsync(
            command, TimeSpan.FromSeconds(5), CancellationToken.None);

        SessionCreatedResultEvent created = Assert.IsType<SessionCreatedResultEvent>(result);
        Assert.Equal("cmd-target", created.CommandId);
    }

    // ---------------------------------------------------------------
    // (b) CommandErrorEvent with matching CommandId is returned, not thrown
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_ReturnsCommandErrorEvent_WhenCoreRespondsWithError()
    {
        FeedableReader reader = new();
        CapturingWriter writer = new();

        CommandErrorEvent errorEvt = new()
        {
            CommandId = "cmd-err",
            Command = "session.create",
            Code = "noActiveSession",
            Message = "handshake failed",
        };
        reader.Feed(SerializeEvent(errorEvt));

        CoreProcessRpcTransport transport = new(reader, writer);
        SessionCreateCommand command = new() { Id = "cmd-err" };

        ResultEvent result = await transport.RequestAsync(
            command, TimeSpan.FromSeconds(5), CancellationToken.None);

        CommandErrorEvent returned = Assert.IsType<CommandErrorEvent>(result);
        Assert.Equal("cmd-err", returned.CommandId);
        Assert.Equal("noActiveSession", returned.Code);
    }

    // ---------------------------------------------------------------
    // (c) Timeout faults RpcTimeoutException, not OperationCanceledException
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_Timeout_ThrowsRpcTimeoutException_NotOperationCanceled()
    {
        // No lines fed — timeout fires after 100ms.
        FeedableReader reader = new();
        CapturingWriter writer = new();
        CoreProcessRpcTransport transport = new(reader, writer);
        SessionCreateCommand command = new() { Id = "cmd-timeout" };

        Exception? thrown = null;
        try
        {
            await transport.RequestAsync(command, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.NotNull(thrown);
        RpcTimeoutException rpcEx = Assert.IsType<RpcTimeoutException>(thrown);
        Assert.Equal("cmd-timeout", rpcEx.CommandId);
        Assert.False(thrown is OperationCanceledException,
            "Timeout must not surface as OperationCanceledException.");
    }

    // ---------------------------------------------------------------
    // (d) Caller cancellation faults OperationCanceledException, distinct from timeout
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_CallerCancels_ThrowsOperationCanceledException_NotTimeout()
    {
        // Long timeout (will not fire); caller cancels before any event.
        FeedableReader reader = new();
        CapturingWriter writer = new();
        CoreProcessRpcTransport transport = new(reader, writer);
        SessionCreateCommand command = new() { Id = "cmd-cancel" };

        using CancellationTokenSource cts = new();

        Task<ResultEvent> request = transport.RequestAsync(
            command, TimeSpan.FromSeconds(30), cts.Token);

        // Cancel before any line is fed.
        await cts.CancelAsync();

        Exception? thrown = null;
        try { await request; }
        catch (Exception ex) { thrown = ex; }

        Assert.NotNull(thrown);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.False(thrown is RpcTimeoutException,
            "Caller cancellation must not surface as RpcTimeoutException.");
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static SessionCreatedResultEvent MakeSessionCreated(string commandId) =>
        new()
        {
            CommandId = commandId,
            Session = new SessionMeta
            {
                Id = "test-session",
                Created = DateTimeOffset.UtcNow,
                Modified = DateTimeOffset.UtcNow,
            },
        };

    private static string SerializeEvent(Event evt) =>
        JsonSerializer.Serialize(evt, WireSerializerOptions.Default);

    /// <summary>
    /// A <see cref="TextReader"/> backed by a channel. Lines are fed by the test via
    /// <see cref="Feed"/>, blocking <see cref="ReadLineAsync(CancellationToken)"/> until available.
    /// </summary>
    private sealed class FeedableReader : TextReader
    {
        private readonly Channel<string> _lines =
            Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = false });

        public void Feed(string line) => _lines.Writer.TryWrite(line);
        public void Complete() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public override Task<string?> ReadLineAsync() =>
            ReadLineAsync(CancellationToken.None).AsTask();
    }

    /// <summary>Captures writes to the transport's send path (not asserted in these tests).</summary>
    private sealed class CapturingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
