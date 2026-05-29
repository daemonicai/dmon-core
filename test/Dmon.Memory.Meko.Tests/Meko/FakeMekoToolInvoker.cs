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
    /// <summary>
    /// A fixed fake Meko conversation UUID returned for every <c>conversation_create</c>
    /// call that has no pre-enqueued result. Tests that need to assert the value passed
    /// as <c>conversation_id</c> on subsequent calls should compare against this constant.
    /// </summary>
    public const string FakeConversationId = "00000000-0000-0000-0000-000000000001";

    private static readonly CallToolResult DefaultConversationCreateResult = new()
    {
        Content =
        [
            new TextContentBlock { Text = $"{{\"id\":\"{FakeConversationId}\"}}" },
        ],
    };

    private readonly Queue<CallToolResult> _results = new();
    private readonly List<(string Tool, IReadOnlyDictionary<string, object?> Args)> _calls = new();

    /// <summary>All calls recorded in order.</summary>
    public IReadOnlyList<(string Tool, IReadOnlyDictionary<string, object?> Args)> Calls => _calls;

    /// <summary>Number of calls recorded.</summary>
    public int CallCount => _calls.Count;

    /// <summary>
    /// Enqueues a result to be returned for the next call, in FIFO order.
    /// If the queue is empty when a call arrives, an empty <see cref="CallToolResult"/> is returned.
    /// (Exception: <c>conversation_create</c> returns <see cref="DefaultConversationCreateResult"/>.)
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

        CallToolResult result;
        if (string.Equals(tool, "conversation_create", StringComparison.Ordinal))
        {
            // conversation_create always uses its fixed default unless a result was
            // specifically pre-enqueued for it. Pre-queued non-conversation_create
            // results are reserved for memory_* calls only (never consumed here).
            result = _conversationCreateResult ?? DefaultConversationCreateResult;
        }
        else if (_results.Count > 0)
        {
            result = _results.Dequeue();
        }
        else
        {
            result = new CallToolResult { Content = [] };
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Overrides the result returned for <c>conversation_create</c> calls.
    /// If not set, <see cref="DefaultConversationCreateResult"/> is used.
    /// </summary>
    public void SetConversationCreateResult(CallToolResult result) =>
        _conversationCreateResult = result;

    private CallToolResult? _conversationCreateResult;
}
