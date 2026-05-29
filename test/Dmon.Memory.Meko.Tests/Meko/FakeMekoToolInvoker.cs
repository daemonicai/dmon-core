using ModelContextProtocol.Protocol;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// Hand-rolled fake <see cref="IMekoToolInvoker"/> for unit tests (D12).
/// Records every call so tests can assert on tool name and args.
/// Pre-configure a response via <see cref="EnqueueResult"/> or leave the default
/// (empty <see cref="CallToolResult"/>).
/// Thread-safety is not a concern — tests run synchronously within a single thread.
/// </summary>
internal sealed class FakeMekoToolInvoker : IMekoToolInvoker
{
    private readonly Queue<CallToolResult> _results = new();
    private readonly List<(string Tool, IReadOnlyDictionary<string, object?> Args)> _calls = new();

    /// <summary>All calls recorded in order.</summary>
    public IReadOnlyList<(string Tool, IReadOnlyDictionary<string, object?> Args)> Calls => _calls;

    /// <summary>Number of calls recorded.</summary>
    public int CallCount => _calls.Count;

    /// <summary>
    /// Enqueues a result to be returned for the next call, in FIFO order.
    /// If the queue is empty when a call arrives, an empty <see cref="CallToolResult"/> is returned.
    /// </summary>
    public void EnqueueResult(CallToolResult result) => _results.Enqueue(result);

    /// <summary>
    /// Convenience: enqueue a result whose single content block contains <paramref name="json"/>.
    /// </summary>
    public void EnqueueJsonResult(string json)
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = json },
            },
        };
        _results.Enqueue(result);
    }

    public Task<CallToolResult> CallToolAsync(
        string tool,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default)
    {
        _calls.Add((tool, args));
        CallToolResult result = _results.Count > 0 ? _results.Dequeue() : new CallToolResult { Content = [] };
        return Task.FromResult(result);
    }
}
