using System.Threading.Channels;
using Dmon.Gateway.Sessions;

namespace Dmon.Gateway.Tests;

public sealed class SessionHandlerTests
{
    [Fact]
    public async Task DetachedEvents_AreBuffered_ThenDeliveredInOrderOnAttach()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // No connection attached: these accumulate in the buffer.
        stdout.Feed("""{"event":"a"}""");
        stdout.Feed("""{"event":"b"}""");
        stdout.Feed("""{"event":"c"}""");

        RecordingConnection connection = new();
        handler.Attach(connection);

        IReadOnlyList<string> received = await connection.WaitForCountAsync(3);

        Assert.Equal(
            ["""{"event":"a"}""", """{"event":"b"}""", """{"event":"c"}"""],
            received);
    }

    [Fact]
    public async Task LiveEventAfterAttach_IsDeliveredAfterBufferedEvents()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // Buffered while detached.
        stdout.Feed("""{"event":"buffered-1"}""");
        stdout.Feed("""{"event":"buffered-2"}""");

        RecordingConnection connection = new();
        handler.Attach(connection);

        // A live event arrives right after attach; it must land after the buffered ones.
        stdout.Feed("""{"event":"live"}""");

        IReadOnlyList<string> received = await connection.WaitForCountAsync(3);

        Assert.Equal(
            ["""{"event":"buffered-1"}""", """{"event":"buffered-2"}""", """{"event":"live"}"""],
            received);
    }

    [Fact]
    public async Task Handler_SurvivesDetach_AndContinuesDeliveringAfterReattach()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        RecordingConnection first = new();
        handler.Attach(first);
        stdout.Feed("""{"event":"1"}""");
        await first.WaitForCountAsync(1);

        handler.Detach();

        // Emitted while detached → buffered.
        stdout.Feed("""{"event":"2"}""");

        RecordingConnection second = new();
        handler.Attach(second);
        stdout.Feed("""{"event":"3"}""");

        IReadOnlyList<string> received = await second.WaitForCountAsync(2);

        Assert.Equal(["""{"event":"2"}""", """{"event":"3"}"""], received);

        // The first connection only ever saw its single live line.
        Assert.Equal(["""{"event":"1"}"""], first.Frames);
    }

    /// <summary>
    /// Records frames in delivery order and lets a test await a target count.
    /// </summary>
    private sealed class RecordingConnection : IGatewayConnection
    {
        private readonly List<string> _frames = [];
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Frames
        {
            get { lock (_gate) { return [.. _frames]; } }
        }

        public ValueTask SendAsync(string frame, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _frames.Add(frame);
                _signal.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }

        public async Task<IReadOnlyList<string>> WaitForCountAsync(int count)
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (_gate)
                {
                    if (_frames.Count >= count)
                        return [.. _frames];
                }

                try
                {
                    await _signal.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        throw new TimeoutException(
                            $"Expected {count} frames, saw {_frames.Count}: [{string.Join(", ", _frames)}]");
                    }
                }
            }
        }
    }

    /// <summary>
    /// A <see cref="TextReader"/> whose <see cref="ReadLineAsync"/> blocks until a line is fed,
    /// letting a test control when the pump observes each stdout line.
    /// </summary>
    private sealed class FeedableReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

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

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }
}
