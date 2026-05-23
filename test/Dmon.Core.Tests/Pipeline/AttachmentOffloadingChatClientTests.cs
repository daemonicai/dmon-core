using Dmon.Core.Pipeline;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Protocol.Commands;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Pipeline;

public sealed class AttachmentOffloadingChatClientTests
{
    // --- Test doubles ---

    private sealed class CapturingChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken)
        {
            LastMessages = messages.ToList();
            return Task.FromResult(new ChatResponse([]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastMessages = messages.ToList();
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubSessionHandler(SessionMeta? session) : ISessionHandler
    {
        public SessionMeta? CurrentSession => session;

        public Task CreateAsync(SessionCreateCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloneAsync(SessionCloneCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LoadAsync(SessionLoadCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ListAsync(SessionListCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetNameAsync(SessionSetNameCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetStatsAsync(SessionGetStatsCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetMessagesAsync(SessionGetMessagesCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubAttachmentStore(string? returnPath) : IAttachmentStore
    {
        public Task<string?> StoreIfLargeAsync(
            string sessionId,
            string callId,
            string content,
            string extension = "txt",
            CancellationToken cancellationToken = default)
            => Task.FromResult(returnPath);
    }

    private static SessionMeta MakeSession(string id = "sess-1")
        => new() { Id = id, Created = DateTimeOffset.UtcNow, Modified = DateTimeOffset.UtcNow };

    private static AttachmentOffloadingChatClient Build(
        CapturingChatClient inner,
        SessionMeta? session,
        string? attachmentPath)
        => new(inner, new StubSessionHandler(session), new StubAttachmentStore(attachmentPath));

    // --- Tests ---

    [Fact]
    public async Task GetResponseAsync_ResultBelowThreshold_PassesThroughUnchanged()
    {
        CapturingChatClient inner = new();
        AttachmentOffloadingChatClient client = Build(inner, MakeSession(), attachmentPath: null);

        FunctionResultContent toolResult = new("call-1", "small result");
        ChatMessage toolMessage = new(ChatRole.Tool, [toolResult]);

        await client.GetResponseAsync([toolMessage], null, CancellationToken.None);

        ChatMessage captured = Assert.Single(inner.LastMessages);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(captured.Contents));
        Assert.Equal("small result", result.Result?.ToString());
    }

    [Fact]
    public async Task GetResponseAsync_ResultAboveThreshold_ReplacesWithJsonObject()
    {
        CapturingChatClient inner = new();
        AttachmentOffloadingChatClient client = Build(inner, MakeSession(), attachmentPath: "attachments/call-1.txt");

        string largeContent = new string('x', 2000);
        FunctionResultContent toolResult = new("call-1", largeContent);
        ChatMessage toolMessage = new(ChatRole.Tool, [toolResult]);

        await client.GetResponseAsync([toolMessage], null, CancellationToken.None);

        ChatMessage captured = Assert.Single(inner.LastMessages);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(captured.Contents));
        string? resultStr = result.Result?.ToString();
        Assert.NotNull(resultStr);
        Assert.Contains("attachmentPath", resultStr);
        Assert.Contains("attachments/call-1.txt", resultStr);
        Assert.Contains("preview", resultStr);
    }

    [Fact]
    public async Task GetResponseAsync_NoActiveSession_PassesThroughUnchanged()
    {
        CapturingChatClient inner = new();
        AttachmentOffloadingChatClient client = Build(inner, session: null, attachmentPath: "attachments/call-1.txt");

        string largeContent = new string('x', 2000);
        FunctionResultContent toolResult = new("call-1", largeContent);
        ChatMessage toolMessage = new(ChatRole.Tool, [toolResult]);

        await client.GetResponseAsync([toolMessage], null, CancellationToken.None);

        ChatMessage captured = Assert.Single(inner.LastMessages);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(captured.Contents));
        Assert.Equal(largeContent, result.Result?.ToString());
    }

    [Fact]
    public async Task GetResponseAsync_NonToolMessage_PassesThroughUnchanged()
    {
        CapturingChatClient inner = new();
        AttachmentOffloadingChatClient client = Build(inner, MakeSession(), attachmentPath: "attachments/x.txt");

        ChatMessage userMessage = new(ChatRole.User, "hello");

        await client.GetResponseAsync([userMessage], null, CancellationToken.None);

        ChatMessage captured = Assert.Single(inner.LastMessages);
        Assert.Equal(ChatRole.User, captured.Role);
        Assert.Equal("hello", captured.Text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ResultAboveThreshold_ReplacesWithJsonObject()
    {
        CapturingChatClient inner = new();
        AttachmentOffloadingChatClient client = Build(inner, MakeSession(), attachmentPath: "attachments/call-s1.txt");

        string largeContent = new string('y', 2000);
        FunctionResultContent toolResult = new("call-s1", largeContent);
        ChatMessage toolMessage = new(ChatRole.Tool, [toolResult]);

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([toolMessage], null, CancellationToken.None)) { }

        ChatMessage captured = Assert.Single(inner.LastMessages);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(captured.Contents));
        string? resultStr = result.Result?.ToString();
        Assert.NotNull(resultStr);
        Assert.Contains("attachmentPath", resultStr);
    }
}
