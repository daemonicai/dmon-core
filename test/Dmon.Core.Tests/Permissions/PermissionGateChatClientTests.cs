using Dmon.Core.Extensions;
using Dmon.Core.Permissions;
using Dmon.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Permissions;

public sealed class PermissionGateChatClientTests
{
    // --- Test doubles ---

    private sealed class StubInnerClient : IChatClient
    {
        private readonly List<ChatMessage> _response;

        public StubInnerClient(List<ChatMessage> response)
        {
            _response = response;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken)
            => Task.FromResult(new ChatResponse(_response));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (ChatMessage msg in _response)
            {
                yield return new ChatResponseUpdate(msg.Role, msg.Contents);
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubPolicy : IPermissionPolicy
    {
        private readonly IPermissionSettings _settings;

        public StubPolicy()
        {
            _settings = new StubPermissionSettings();
        }

        public IPermissionSettings ProjectSettings => _settings;
        public IPermissionSettings? GlobalSettings => null;

        private sealed class StubPermissionSettings : IPermissionSettings
        {
            public PermissionSettings Settings => new();
            public Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }
    }

    private sealed class StubToolRegistry : IToolRegistry
    {
        public void Register(string extensionName, IDmonExtension extension, IEnumerable<AIFunction> tools) { }
        public IDmonExtension? FindExtension(string toolName) => null;
        public void Unregister(string extensionName) { }
        public IReadOnlyList<AIFunction> GetAll() => [];
        public IReadOnlyList<RegisteredExtensionSnapshot> GetSnapshot() => [];
        public void Clear() { }
    }

    private static FunctionCallContent MakeCall(string callId, string name)
        => new(callId, name, null);

    private static PermissionGateChatClient BuildGate(
        List<ChatMessage> innerResponse,
        Func<ToolConfirmRequestEvent, CancellationToken, Task<bool>> callback)
    {
        // Policy returns Prompt for all tool calls (no extension registered) so the callback is always invoked.
        return new PermissionGateChatClient(
            new StubInnerClient(innerResponse),
            new StubPolicy(),
            new StubToolRegistry(),
            callback);
    }

    // --- Tests: GetResponseAsync ---

    [Fact]
    public async Task GetResponseAsync_NonToolCallMessage_PassesThrough()
    {
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, "Hello!")];
        PermissionGateChatClient gate = BuildGate(inner, (_, _) => Task.FromResult(true));

        ChatResponse response = await gate.GetResponseAsync([], null, CancellationToken.None);

        Assert.Single(response.Messages);
        Assert.Equal("Hello!", response.Messages[0].Text);
    }

    [Fact]
    public async Task GetResponseAsync_ToolCall_Confirmed_PassesThrough()
    {
        FunctionCallContent call = MakeCall("call-1", "my_tool");
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        bool callbackInvoked = false;
        PermissionGateChatClient gate = BuildGate(inner, (evt, _) =>
        {
            callbackInvoked = true;
            Assert.Equal("call-1", evt.ConfirmId);
            Assert.Equal("my_tool", evt.Name);
            return Task.FromResult(true);
        });

        ChatResponse response = await gate.GetResponseAsync([], null, CancellationToken.None);

        Assert.True(callbackInvoked);
        // The assistant message with the call should pass through.
        ChatMessage assistantMsg = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, assistantMsg.Role);
        Assert.Contains(call, assistantMsg.Contents);
    }

    [Fact]
    public async Task GetResponseAsync_ToolCall_Denied_ReplacesWithErrorResult()
    {
        FunctionCallContent call = MakeCall("call-2", "dangerous_tool");
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        PermissionGateChatClient gate = BuildGate(inner, (_, _) => Task.FromResult(false));

        ChatResponse response = await gate.GetResponseAsync([], null, CancellationToken.None);

        // No assistant message; instead a tool-role error result.
        ChatMessage toolMsg = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Tool, toolMsg.Role);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(toolMsg.Contents));
        Assert.Equal("call-2", result.CallId);
        Assert.Equal("Tool call denied by permission policy.", result.Result?.ToString());
    }

    [Fact]
    public async Task GetResponseAsync_MultipleToolCalls_EachEvaluatedIndependently()
    {
        FunctionCallContent allowed = MakeCall("c1", "safe");
        FunctionCallContent denied = MakeCall("c2", "dangerous");
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [allowed, denied])];

        // Confirm only the first call.
        PermissionGateChatClient gate = BuildGate(inner, (evt, _) =>
            Task.FromResult(evt.ConfirmId == "c1"));

        ChatResponse response = await gate.GetResponseAsync([], null, CancellationToken.None);

        // Should have one assistant message (with allowed call) and one tool error message.
        Assert.Equal(2, response.Messages.Count);
        ChatMessage assistantMsg = response.Messages[0];
        Assert.Equal(ChatRole.Assistant, assistantMsg.Role);
        Assert.Single(assistantMsg.Contents); // only allowed
        ChatMessage toolMsg = response.Messages[1];
        Assert.Equal(ChatRole.Tool, toolMsg.Role);
    }

    // --- Tests: GetStreamingResponseAsync ---

    [Fact]
    public async Task GetStreamingResponseAsync_ToolCall_Confirmed_Emitted()
    {
        FunctionCallContent call = MakeCall("s1", "streaming_tool");
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        PermissionGateChatClient gate = BuildGate(inner, (_, _) => Task.FromResult(true));

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in gate.GetStreamingResponseAsync([], null, CancellationToken.None))
        {
            updates.Add(update);
        }

        ChatResponseUpdate assistantUpdate = Assert.Single(updates);
        Assert.Equal(ChatRole.Assistant, assistantUpdate.Role);
        Assert.Contains(call, assistantUpdate.Contents);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ToolCall_Denied_EmitsErrorResult()
    {
        FunctionCallContent call = MakeCall("s2", "streaming_dangerous");
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        PermissionGateChatClient gate = BuildGate(inner, (_, _) => Task.FromResult(false));

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in gate.GetStreamingResponseAsync([], null, CancellationToken.None))
        {
            updates.Add(update);
        }

        ChatResponseUpdate toolUpdate = Assert.Single(updates);
        Assert.Equal(ChatRole.Tool, toolUpdate.Role);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(toolUpdate.Contents));
        Assert.Equal("s2", result.CallId);
    }

    // --- Test doubles for FindExtension delegation ---

    private sealed class StubEvaluatingExtension(PermissionResult result) : IDmonExtension
    {
        public bool EvaluateCalled { get; private set; }
        public string Name => "stub";
        public string Description => "stub";
        public IEnumerable<AIFunction> Tools => [];

        public PermissionResult Evaluate(
            FunctionCallContent call,
            IPermissionSettings project,
            IPermissionSettings? global)
        {
            EvaluateCalled = true;
            return result;
        }
    }

    private sealed class StubRegistryWithExtension(string toolName, IDmonExtension extension) : IToolRegistry
    {
        public void Register(string extensionName, IDmonExtension ext, IEnumerable<AIFunction> tools) { }
        public IDmonExtension? FindExtension(string name) => name == toolName ? extension : null;
        public void Unregister(string extensionName) { }
        public IReadOnlyList<AIFunction> GetAll() => [];
        public IReadOnlyList<RegisteredExtensionSnapshot> GetSnapshot() => [];
        public void Clear() { }
    }

    // --- FindExtension delegation tests ---

    [Fact]
    public async Task GetResponseAsync_ToolCall_CallsFindExtension_AndDelegatesToExtensionEvaluate()
    {
        StubEvaluatingExtension extension = new(PermissionResult.Allow);
        FunctionCallContent call = MakeCall("call-ext", "my_tool");
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        PermissionGateChatClient gate = new(
            new StubInnerClient(inner),
            new StubPolicy(),
            new StubRegistryWithExtension("my_tool", extension),
            (_, _) => Task.FromResult(true));

        await gate.GetResponseAsync([], null, CancellationToken.None);

        Assert.True(extension.EvaluateCalled);
    }

    [Fact]
    public async Task GetResponseAsync_ExtensionReturnsAllow_SkipsConfirmCallback()
    {
        StubEvaluatingExtension extension = new(PermissionResult.Allow);
        FunctionCallContent call = MakeCall("call-allow", "safe_tool");
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        bool callbackInvoked = false;
        PermissionGateChatClient gate = new(
            new StubInnerClient(inner),
            new StubPolicy(),
            new StubRegistryWithExtension("safe_tool", extension),
            (_, _) => { callbackInvoked = true; return Task.FromResult(true); });

        ChatResponse response = await gate.GetResponseAsync([], null, CancellationToken.None);

        Assert.False(callbackInvoked);
        ChatMessage assistantMsg = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, assistantMsg.Role);
        Assert.Contains(call, assistantMsg.Contents);
    }

    [Fact]
    public async Task GetResponseAsync_ExtensionReturnsDeny_ReturnsErrorResultWithoutCallback()
    {
        StubEvaluatingExtension extension = new(PermissionResult.Deny);
        FunctionCallContent call = MakeCall("call-deny", "dangerous_tool");
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        bool callbackInvoked = false;
        PermissionGateChatClient gate = new(
            new StubInnerClient(inner),
            new StubPolicy(),
            new StubRegistryWithExtension("dangerous_tool", extension),
            (_, _) => { callbackInvoked = true; return Task.FromResult(true); });

        ChatResponse response = await gate.GetResponseAsync([], null, CancellationToken.None);

        Assert.False(callbackInvoked);
        ChatMessage toolMsg = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Tool, toolMsg.Role);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(toolMsg.Contents));
        Assert.Equal("Tool call denied by permission policy.", result.Result?.ToString());
    }
}
