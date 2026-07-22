using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Providers.Mlx.Tests;

// ---------------------------------------------------------------------------
// 4.1/4.3 — MlxProviderFactory
//
// The factory now owns the self-heal (design.md D2): CreateAsync awaits the
// extension's EnsureRunningAsync (which runs the one-time tool-calling probe)
// before snapshotting capabilities, then wraps the stack in EnsureRunningChatClient.
// Every factory is therefore built with an extension whose attach-first seam is
// stubbed (isRunningProbe -> true) and whose probe result is driven by a fake
// probeClientFactory — no real mlx_lm.server spawn, no live port dialled.
// ---------------------------------------------------------------------------

public sealed class MlxProviderFactoryTests
{
    private static MlxProviderFactory MakeFactory(
        MlxRuntimeState state,
        bool probeReturnsToolCall,
        MlxRuntimeOptions? opts = null,
        HttpMessageHandler? handler = null,
        StrongBox<int>? ensureRunningCalls = null)
    {
        opts ??= MlxRuntimeOptions.Firstline();
        MlxProviderExtension extension = new(
            opts,
            state,
            isRunningProbe: _ =>
            {
                if (ensureRunningCalls is not null)
                    Interlocked.Increment(ref ensureRunningCalls.Value);
                return Task.FromResult(true);
            },
            provisionEnvDelegate: _ => Task.FromResult("0.31.3"),
            startServerDelegate: null,
            probeClientFactory: (_, _) => new ProbeFakeChatClient(probeReturnsToolCall));

        return handler is null
            ? new MlxProviderFactory(opts, state, extension)
            : new MlxProviderFactory(opts, state, extension, handler);
    }

    private static ProviderConfig DefaultConfig(string baseUrl) => new()
    {
        Name = "mlx",
        Adapter = "mlx",
        BaseUrl = baseUrl,
        Auth = new ProviderAuthConfig { Type = "none" },
    };

    private static ProviderConfig ConfigNoBaseUrl() => new()
    {
        Name = "mlx",
        Adapter = "mlx",
        BaseUrl = null,
        Auth = new ProviderAuthConfig { Type = "none" },
    };

    // ---------------------------------------------------------------------------
    // Factory metadata
    // ---------------------------------------------------------------------------

    [Fact]
    public void AdapterName_IsMlx()
    {
        MlxProviderFactory factory = MakeFactory(new MlxRuntimeState(), probeReturnsToolCall: false);
        Assert.Equal("mlx", factory.AdapterName);
    }

    [Fact]
    public void DefaultModelId_ReflectsOptions()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        MlxProviderFactory factory = MakeFactory(new MlxRuntimeState(), probeReturnsToolCall: false, opts: opts);
        Assert.Equal(opts.ModelId, factory.DefaultModelId);
    }

    // ---------------------------------------------------------------------------
    // Capabilities + wrapping
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_ReturnsCapabilitiesDecoratorWrappedClient()
    {
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };

        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: true);
        IChatClient client = await factory.CreateAsync(DefaultConfig(state.BaseUrl), apiKey: null);

        object? caps = client.GetService(typeof(ChatClientCapabilities));
        Assert.NotNull(caps);
        Assert.IsType<ChatClientCapabilities>(caps);
    }

    [Fact]
    public async Task CreateAsync_Capabilities_ReflectProbeVerifiedToolCalling_True()
    {
        // The probe (run inside CreateAsync via EnsureRunningAsync) returns a tool call,
        // so the capability snapshot taken afterwards advertises tool calling.
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };

        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: true);
        IChatClient client = await factory.CreateAsync(DefaultConfig(state.BaseUrl), apiKey: null);

        ChatClientCapabilities caps = (ChatClientCapabilities)client.GetService(typeof(ChatClientCapabilities))!;
        Assert.True(caps.SupportsToolCalling);
    }

    [Fact]
    public async Task CreateAsync_Capabilities_ReflectProbeVerifiedToolCalling_False()
    {
        // The probe returns no tool call → capabilities advertise tool calling disabled.
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };

        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: false);
        IChatClient client = await factory.CreateAsync(DefaultConfig(state.BaseUrl), apiKey: null);

        ChatClientCapabilities caps = (ChatClientCapabilities)client.GetService(typeof(ChatClientCapabilities))!;
        Assert.False(caps.SupportsToolCalling);
    }

    [Fact]
    public async Task CreateAsync_Capabilities_ToolCalling_ResolvedByProbe_FromUnprobedState()
    {
        // State starts unprobed (null); the probe run inside CreateAsync resolves it. A probe
        // that returns no tool call yields SupportsToolCalling == false — driven by the probe,
        // not a pre-probe default.
        MlxRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:8800/v1",
            ToolCallingVerified = null,
        };

        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: false);
        IChatClient client = await factory.CreateAsync(DefaultConfig(state.BaseUrl), apiKey: null);

        ChatClientCapabilities caps = (ChatClientCapabilities)client.GetService(typeof(ChatClientCapabilities))!;
        Assert.False(caps.SupportsToolCalling);
        Assert.False(state.ToolCallingVerified);
    }

    [Fact]
    public async Task CreateAsync_DoesNotThrow_WithValidBaseUrl()
    {
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: true);

        Exception? ex = await Record.ExceptionAsync(() =>
            factory.CreateAsync(DefaultConfig(state.BaseUrl), apiKey: null).AsTask());
        Assert.Null(ex);
    }

    [Fact]
    public async Task CreateAsync_ComposesV1Endpoint_FromOptions_WhenBaseUrlUnset()
    {
        // Exercises the factory's fallback: config.BaseUrl == null AND runtimeState.BaseUrl == ""
        // → factory composes http://{Host}:{Port}/v1 from MlxRuntimeOptions.
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline(port: 8800);
        MlxRuntimeState state = new() { BaseUrl = string.Empty };

        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: true, opts: opts);
        IChatClient client = await factory.CreateAsync(ConfigNoBaseUrl(), apiKey: null);

        Assert.NotNull(client);
        object? caps = client.GetService(typeof(ChatClientCapabilities));
        Assert.NotNull(caps);
        Assert.IsType<ChatClientCapabilities>(caps);
    }

    [Fact]
    public async Task CreateAsync_InvokesEnsureRunning_AndReturnsSelfHealingClient()
    {
        // 3.2 — the start path fires during construction (EnsureRunningAsync ran, via the
        // seam), and the returned client is EnsureRunningChatClient-wrapped so a subsequent
        // request re-invokes the ensure-running seam before dispatching to the runtime.
        StrongBox<int> ensureRunningCalls = new(0);
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        CapturingHandler handler = new(BuildTextResponse());

        MlxProviderFactory factory = MakeFactory(
            state,
            probeReturnsToolCall: true,
            handler: handler,
            ensureRunningCalls: ensureRunningCalls);

        IChatClient client = await factory.CreateAsync(DefaultConfig(state.BaseUrl), apiKey: null);

        Assert.Equal(1, ensureRunningCalls.Value);
        Assert.IsType<EnsureRunningChatClient>(client);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(2, ensureRunningCalls.Value);
    }

    [Fact]
    public async Task CreateAsync_ActiveProviderClient_AdvertisesToolCalling_WhenProbeVerified()
    {
        // 3.3 — the capability snapshot is taken after the probe, so a probe that verified
        // tool support surfaces as SupportsToolCalling == true on the returned client.
        MlxRuntimeState state = new()
        {
            BaseUrl = "http://127.0.0.1:8800/v1",
            ToolCallingVerified = null,
        };

        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: true);
        IChatClient client = await factory.CreateAsync(DefaultConfig(state.BaseUrl), apiKey: null);

        ChatClientCapabilities caps = (ChatClientCapabilities)client.GetService(typeof(ChatClientCapabilities))!;
        Assert.True(caps.SupportsToolCalling);
        Assert.True(state.ToolCallingVerified);
    }

    // ---------------------------------------------------------------------------
    // 4.3 — Reasoning + tool_calls round-trip (LOAD-BEARING)
    //
    // stock mlx_lm.server emits a "reasoning" field the OpenAI SDK ignores as unknown.
    // These tests confirm tool_calls still parse correctly and reasoning is not surfaced
    // as TextContent, while also verifying the generous max_tokens default is injected.
    // ---------------------------------------------------------------------------

    private const string ReasoningText = "I need to call the test tool before answering.";
    private const string ToolName = "stub_tool";

    private static string BuildToolCallResponse() => $$"""
        {
            "id": "chatcmpl-reasoning-test",
            "object": "chat.completion",
            "created": 1234567890,
            "model": "mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit",
            "choices": [{
                "index": 0,
                "message": {
                    "role": "assistant",
                    "reasoning": "{{ReasoningText}}",
                    "content": null,
                    "tool_calls": [{
                        "id": "call_stub123",
                        "type": "function",
                        "function": {
                            "name": "{{ToolName}}",
                            "arguments": "{\"param\":\"value\"}"
                        }
                    }]
                },
                "finish_reason": "tool_calls"
            }],
            "usage": { "prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15 }
        }
        """;

    private static string BuildTextResponse() => """
        {
            "id": "chatcmpl-plain",
            "object": "chat.completion",
            "created": 1234567890,
            "model": "mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit",
            "choices": [{
                "index": 0,
                "message": {
                    "role": "assistant",
                    "content": "Hello!"
                },
                "finish_reason": "stop"
            }],
            "usage": { "prompt_tokens": 5, "completion_tokens": 1, "total_tokens": 6 }
        }
        """;

    [Fact]
    public async Task ToolCalls_ParseCorrectly_WhenReasoningFieldPresent()
    {
        CapturingHandler handler = new(BuildToolCallResponse());
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: true, opts: opts, handler: handler);

        IChatClient client = await factory.CreateAsync(
            DefaultConfig("http://127.0.0.1:8800/v1"), apiKey: null);

        AIFunction tool = AIFunctionFactory.Create(
            (string param) => $"result:{param}", ToolName);

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "call the tool")],
            new ChatOptions { Tools = [tool] });

        // Tool call present with the expected name.
        IList<AIContent> assistantContents = response.Messages[^1].Contents;
        FunctionCallContent? call = assistantContents
            .OfType<FunctionCallContent>()
            .FirstOrDefault();
        Assert.NotNull(call);
        Assert.Equal(ToolName, call.Name);

        // The "reasoning" field must NOT surface as TextContent — stock OpenAI SDK discards it.
        IEnumerable<TextContent> texts = assistantContents.OfType<TextContent>();
        Assert.DoesNotContain(
            texts,
            t => t.Text?.Contains(ReasoningText, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task MaxTokens_DefaultApplied_WhenCallerOmitsMaxOutputTokens()
    {
        CapturingHandler handler = new(BuildTextResponse());
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: false, opts: opts, handler: handler);

        IChatClient client = await factory.CreateAsync(
            DefaultConfig("http://127.0.0.1:8800/v1"), apiKey: null);

        // No MaxOutputTokens set by the caller → defaulter must inject 8192.
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        string captured = handler.LastRequestBody;
        Assert.False(string.IsNullOrEmpty(captured), "No outgoing request body captured.");

        bool hasMaxTokensField =
            captured.Contains("\"max_tokens\"", StringComparison.Ordinal) ||
            captured.Contains("\"max_completion_tokens\"", StringComparison.Ordinal);
        Assert.True(hasMaxTokensField,
            $"Request body should contain a max_tokens or max_completion_tokens field. Body: {captured}");
        Assert.Contains("8192", captured, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxTokens_CallerCap_NotClobbered()
    {
        CapturingHandler handler = new(BuildTextResponse());
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: false, opts: opts, handler: handler);

        IChatClient client = await factory.CreateAsync(
            DefaultConfig("http://127.0.0.1:8800/v1"), apiKey: null);

        const int callerCap = 256;
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { MaxOutputTokens = callerCap });

        string captured = handler.LastRequestBody;
        Assert.False(string.IsNullOrEmpty(captured), "No outgoing request body captured.");

        // Caller-supplied cap must be forwarded verbatim.
        Assert.Contains("256", captured, StringComparison.Ordinal);

        // The defaulter must not have overwritten the caller cap with 8192.
        Assert.DoesNotContain("8192", captured, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // 4.3 — Streaming MaxTokens (exercises MlxMaxTokensDefaulter.GetStreamingResponseAsync)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task MaxTokens_Streaming_DefaultApplied_WhenCallerOmitsMaxOutputTokens()
    {
        StreamingCapturingHandler handler = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: false, opts: opts, handler: handler);

        IChatClient client = await factory.CreateAsync(
            DefaultConfig("http://127.0.0.1:8800/v1"), apiKey: null);

        // No MaxOutputTokens set by the caller → defaulter must inject 8192 on the streaming path.
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")]))
        {
            // drain to completion
        }

        string captured = handler.LastRequestBody;
        Assert.False(string.IsNullOrEmpty(captured), "No outgoing request body captured (streaming).");

        bool hasMaxTokensField =
            captured.Contains("\"max_tokens\"", StringComparison.Ordinal) ||
            captured.Contains("\"max_completion_tokens\"", StringComparison.Ordinal);
        Assert.True(hasMaxTokensField,
            $"Streaming request body should contain a max_tokens field. Body: {captured}");
        Assert.Contains("8192", captured, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxTokens_Streaming_CallerCap_NotClobbered()
    {
        StreamingCapturingHandler handler = new();
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        MlxRuntimeState state = new() { BaseUrl = "http://127.0.0.1:8800/v1" };
        MlxProviderFactory factory = MakeFactory(state, probeReturnsToolCall: false, opts: opts, handler: handler);

        IChatClient client = await factory.CreateAsync(
            DefaultConfig("http://127.0.0.1:8800/v1"), apiKey: null);

        const int callerCap = 256;
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { MaxOutputTokens = callerCap }))
        {
            // drain to completion
        }

        string captured = handler.LastRequestBody;
        Assert.False(string.IsNullOrEmpty(captured), "No outgoing request body captured (streaming).");

        Assert.Contains("256", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("8192", captured, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // Test infrastructure
    // ---------------------------------------------------------------------------

    // Fake probe client driving the factory's in-CreateAsync tool-calling probe: its response
    // either includes or omits a FunctionCallContent. It is wired via probeClientFactory, so it
    // never touches the CapturingHandler HTTP stub that captures the real chat request.
    private sealed class ProbeFakeChatClient(bool hasToolCall) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<AIContent> contents = hasToolCall
                ? [new FunctionCallContent("call-1", "get_test_value", new Dictionary<string, object?>())]
                : [new TextContent("I cannot call tools.")];

            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // In-memory HTTP handler that returns a canned JSON response and captures request bodies.
    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    // In-memory SSE handler for the streaming path — returns text/event-stream content and
    // captures the outgoing request body so tests can assert on max_tokens injection.
    private sealed class StreamingCapturingHandler : HttpMessageHandler
    {
        // Minimal SSE stream: one content chunk + stop chunk + [DONE] sentinel.
        private const string SseBody =
            "data: {\"id\":\"chatcmpl-s\",\"object\":\"chat.completion.chunk\",\"created\":1234567890," +
            "\"model\":\"mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit\"," +
            "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"chatcmpl-s\",\"object\":\"chat.completion.chunk\",\"created\":1234567890," +
            "\"model\":\"mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit\"," +
            "\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(SseBody, Encoding.UTF8, "text/event-stream"),
            };
            return response;
        }
    }
}
