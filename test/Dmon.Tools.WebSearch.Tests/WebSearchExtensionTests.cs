using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Hosting;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Wizard;
using Dmon.Tools.WebSearch;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Tools.WebSearch.Tests;

// ── fake IChatClient: can be backed by a simple thunk or a full 3-arg handler

file sealed class FakeChatClient : IChatClient, IDisposable
{
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _handler;
    public int CallCount { get; private set; }
    public ChatOptions? LastOptions { get; private set; }

    // Convenience constructor: the test just supplies a fixed response.
    public FakeChatClient(Func<Task<ChatResponse>> thunk)
        : this((_, _, _) => thunk()) { }

    // Full constructor: needed when the test must observe messages/options/CT.
    public FakeChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler)
    {
        _handler = handler;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastOptions = options;
        cancellationToken.ThrowIfCancellationRequested();
        return _handler(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not used by WebSearchExtension.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

// ── IChatClientFactory backed by a FakeChatClient

file sealed class FakeChatClientFactory(FakeChatClient client) : IChatClientFactory
{
    public ValueTask<IChatClient> CreateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IChatClient>(client);
}

// ── IProviderFactory minimal test double (4.6 / 4.7)

file sealed class FakeProviderFactory : IProviderFactory
{
    public string AdapterName => "fake";
    public string DisplayName => "Fake";
    public string DefaultModelId => "fake-model";
    public string DefaultEnvVar => "FAKE_API_KEY";

    public ChatClientCapabilities GetCapabilities(string modelId) => new() { SupportsToolCalling = false };

    public ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("FakeProviderFactory.CreateAsync must not be called in unit tests.");

    public ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, string? baseUrl = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<ModelInfo>>([]);

    public ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<WizardStep>(new WizardCompletedStep { Id = "done", Prompt = "", Message = "" });
}

// ── IToolRegistration minimal test double (4.6)

file sealed class FakeToolRegistration : IToolRegistration
{
    public IServiceCollection Services { get; } = new ServiceCollection();
}

// ── shared response-building helpers

file static class Helpers
{
    public static AIFunction GetWebSearchTool(WebSearchExtension ext)
        => ext.Tools.Single(t => t.Name == "web_search");

    public static ChatResponse ResponseWithTextAndSources(string answer, string title, string uri)
    {
        UriContent uriContent = new(new Uri(uri), "text/html");
        uriContent.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        uriContent.AdditionalProperties["title"] = title;

        WebSearchToolResultContent searchResult = new("tool-call-1")
        {
            Outputs = [uriContent],
        };

        ChatMessage message = new(ChatRole.Assistant, [new TextContent(answer), searchResult]);
        return new ChatResponse([message]);
    }

    public static ChatResponse ResponseWithTextOnly(string answer)
        => new([new ChatMessage(ChatRole.Assistant, answer)]);

    /// <summary>
    /// Same logical content as <see cref="ResponseWithTextAndSources"/> but split across two
    /// assistant messages: the first carries the <see cref="TextContent"/>, the second carries
    /// the <see cref="WebSearchToolResultContent"/>. This simulates a provider that streams
    /// or batches content differently and exercises the multi-message walk in FormatResult.
    /// </summary>
    public static ChatResponse ResponseWithTextAndSourcesSplit(string answer, string title, string uri)
    {
        UriContent uriContent = new(new Uri(uri), "text/html");
        uriContent.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        uriContent.AdditionalProperties["title"] = title;

        WebSearchToolResultContent searchResult = new("tool-call-2")
        {
            Outputs = [uriContent],
        };

        // Two messages: text in the first, search results in the second.
        ChatMessage textMessage = new(ChatRole.Assistant, [new TextContent(answer)]);
        ChatMessage sourcesMessage = new(ChatRole.Assistant, [searchResult]);
        return new ChatResponse([textMessage, sourcesMessage]);
    }
}

// ════════════════════════════════════════════════════════════════════════
// 4.2 — Projection tests
// ════════════════════════════════════════════════════════════════════════

public sealed class ProjectionTests
{
    /// <summary>4.2 (a) — answer text and each source's title + uri appear in the result.</summary>
    [Fact]
    public async Task WithSourcesResponse_ResultContainsAnswerAndSource()
    {
        FakeChatClient fake = new(() => Task.FromResult(
            Helpers.ResponseWithTextAndSources("The capital is Paris.", "Wikipedia Paris", "https://en.wikipedia.org/wiki/Paris")));
        WebSearchExtension ext = new(new FakeChatClientFactory(fake));
        AIFunction tool = Helpers.GetWebSearchTool(ext);

        object? result = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "What is the capital of France?" });
        string text = result?.ToString() ?? string.Empty;

        Assert.Contains("The capital is Paris.", text);
        Assert.Contains("Wikipedia Paris", text);
        Assert.Contains("https://en.wikipedia.org/wiki/Paris", text);
    }

    /// <summary>4.2 (b) — no WebSearchToolResultContent: answer is returned, no exception.</summary>
    [Fact]
    public async Task NoSourcesResponse_ReturnsAnswerWithoutFailing()
    {
        FakeChatClient fake = new(() => Task.FromResult(
            Helpers.ResponseWithTextOnly("Paris is the capital.")));
        WebSearchExtension ext = new(new FakeChatClientFactory(fake));
        AIFunction tool = Helpers.GetWebSearchTool(ext);

        object? result = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "capital of France?" });
        string text = result?.ToString() ?? string.Empty;

        Assert.Contains("Paris is the capital.", text);
        Assert.DoesNotContain("Sources:", text);
    }

    /// <summary>
    /// 4.2 (c) — provider-agnostic: the projection is driven by content shape, not by how a
    /// specific provider arranges messages. fakeA packs text + search results into one message
    /// (Gemini/OpenAI style); fakeB splits them across two separate assistant messages. Both
    /// must project the same answer text and the same set of source titles and URIs.
    /// </summary>
    [Fact]
    public async Task SameContentShape_ProjectionIsProviderAgnostic()
    {
        const string Answer = "42 is the answer.";
        const string Title = "Hitchhiker's Guide";
        const string SourceUri = "https://example.com/42";

        // fakeA: single message with TextContent + WebSearchToolResultContent (compact layout).
        FakeChatClient fakeA = new(() => Task.FromResult(Helpers.ResponseWithTextAndSources(Answer, Title, SourceUri)));
        // fakeB: TextContent in message 1, WebSearchToolResultContent in message 2 (split layout).
        FakeChatClient fakeB = new(() => Task.FromResult(Helpers.ResponseWithTextAndSourcesSplit(Answer, Title, SourceUri)));

        WebSearchExtension extA = new(new FakeChatClientFactory(fakeA));
        WebSearchExtension extB = new(new FakeChatClientFactory(fakeB));

        object? resultA = await Helpers.GetWebSearchTool(extA).InvokeAsync(new AIFunctionArguments { ["query"] = "the answer" });
        object? resultB = await Helpers.GetWebSearchTool(extB).InvokeAsync(new AIFunctionArguments { ["query"] = "the answer" });

        string textA = resultA?.ToString() ?? string.Empty;
        string textB = resultB?.ToString() ?? string.Empty;

        // Both layouts must surface the same answer, title, and source URI.
        Assert.Contains(Answer, textA);
        Assert.Contains(Title, textA);
        Assert.Contains(SourceUri, textA);

        Assert.Contains(Answer, textB);
        Assert.Contains(Title, textB);
        Assert.Contains(SourceUri, textB);
    }
}

// ════════════════════════════════════════════════════════════════════════
// 4.3 — Single-call
// ════════════════════════════════════════════════════════════════════════

public sealed class SingleCallTests
{
    /// <summary>4.3 — exactly one GetResponseAsync call, options contain HostedWebSearchTool; no HTTP.</summary>
    [Fact]
    public async Task InvokesExactlyOneGetResponseAsync_WithHostedWebSearchTool()
    {
        FakeChatClient fake = new(() => Task.FromResult(Helpers.ResponseWithTextOnly("ok")));
        WebSearchExtension ext = new(new FakeChatClientFactory(fake));
        AIFunction tool = Helpers.GetWebSearchTool(ext);

        await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "anything" });

        Assert.Equal(1, fake.CallCount);
        Assert.NotNull(fake.LastOptions?.Tools);
        Assert.Contains(fake.LastOptions!.Tools!, t => t is HostedWebSearchTool);
    }
}

// ════════════════════════════════════════════════════════════════════════
// 4.4 — Permission
// ════════════════════════════════════════════════════════════════════════

public sealed class PermissionTests
{
    private static FunctionCallContent WebSearchCall()
        => new(callId: "test", name: "web_search", arguments: null);

    /// <summary>4.4 — Evaluate returns Prompt for a web_search call.</summary>
    [Fact]
    public void Evaluate_WebSearchCall_ReturnsPrompt()
    {
        FakeChatClient fake = new(() => throw new InvalidOperationException("should not be called"));
        WebSearchExtension ext = new(new FakeChatClientFactory(fake));

        PermissionResult result = ext.Evaluate(WebSearchCall(), null!, null);

        Assert.Equal(PermissionResult.Prompt, result);
    }

    /// <summary>4.4 (optional) — CreateConfirmRequest risk is Medium.</summary>
    [Fact]
    public void CreateConfirmRequest_RiskIsMedium()
    {
        FakeChatClient fake = new(() => throw new InvalidOperationException("should not be called"));
        WebSearchExtension ext = new(new FakeChatClientFactory(fake));

        Dmon.Protocol.Models.ToolConfirmRequest req = ext.CreateConfirmRequest(WebSearchCall());

        Assert.Equal(RiskLevel.Medium, req.Risk);
    }
}

// ════════════════════════════════════════════════════════════════════════
// 4.5 — Graceful failure
// ════════════════════════════════════════════════════════════════════════

public sealed class GracefulFailureTests
{
    /// <summary>4.5 — generic Exception becomes "Could not run web search: ..." string.</summary>
    [Fact]
    public async Task GenericException_ReturnsErrorString_DoesNotThrow()
    {
        FakeChatClient fake = new(() => throw new Exception("network down"));
        WebSearchExtension ext = new(new FakeChatClientFactory(fake));
        AIFunction tool = Helpers.GetWebSearchTool(ext);

        object? result = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "something" });
        string text = result?.ToString() ?? string.Empty;

        Assert.Contains("Could not run web search", text);
        Assert.Contains("network down", text);
    }

    /// <summary>4.5 — InvalidOperationException with missing-key message becomes error string.</summary>
    [Fact]
    public async Task MissingKeyException_ReturnsErrorString_DoesNotThrow()
    {
        const string Message = "GEMINI_API_KEY was not set or was empty.";
        FakeChatClient fake = new(() => throw new InvalidOperationException(Message));
        WebSearchExtension ext = new(new FakeChatClientFactory(fake));
        AIFunction tool = Helpers.GetWebSearchTool(ext);

        object? result = await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "something" });
        string text = result?.ToString() ?? string.Empty;

        Assert.Contains("Could not run web search", text);
        Assert.Contains(Message, text);
    }

    /// <summary>
    /// 4.5 — genuine cancellation propagates as OperationCanceledException (B2 fix guard).
    /// The tool must NOT swallow a cancellation by converting it to an error string.
    /// </summary>
    [Fact]
    public async Task GenuineCancellation_PropagatesOperationCanceledException()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // The full-handler overload: observes the cancellation token and throws,
        // simulating what a real provider HTTP call does under cancellation.
        FakeChatClient fake = new((_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Helpers.ResponseWithTextOnly("unreachable"));
        });
        WebSearchExtension ext = new(new FakeChatClientFactory(fake));
        AIFunction tool = Helpers.GetWebSearchTool(ext);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["query"] = "something" }, cts.Token));
    }
}

// ════════════════════════════════════════════════════════════════════════
// 4.6 — Verb registration and malformed config
// ════════════════════════════════════════════════════════════════════════

public sealed class VerbRegistrationTests
{
    /// <summary>
    /// 4.6 — AddAgentWebSearch with a valid FakeProviderFactory registers exactly one IToolExtension
    /// that exposes a tool named "web_search".
    /// </summary>
    [Fact]
    public void AddAgentWebSearch_ValidConfig_RegistersWebSearchExtension()
    {
        FakeToolRegistration reg = new();

        reg.AddAgentWebSearch(p =>
        {
            p.Services.AddSingleton<IProviderFactory>(new FakeProviderFactory());
            p.UseModel("fake", "fake-model");
        });

        ServiceProvider sp = reg.Services.BuildServiceProvider();
        IToolExtension[] extensions = sp.GetServices<IToolExtension>().ToArray();

        Assert.Single(extensions);
        Assert.Contains(extensions, t => t.Tools.Any(f => f.Name == "web_search"));
    }

    /// <summary>
    /// 4.6 — AddAgentWebSearch with an empty configure (no provider, no model) throws
    /// InvalidOperationException eagerly at call time, not deferred to host startup.
    /// </summary>
    [Fact]
    public void AddAgentWebSearch_EmptyConfigure_ThrowsInvalidOperationException()
    {
        FakeToolRegistration reg = new();

        Assert.Throws<InvalidOperationException>(() =>
            reg.AddAgentWebSearch(_ => { }));
    }
}

// ════════════════════════════════════════════════════════════════════════
// 4.7 — Lazy credential resolution
// ════════════════════════════════════════════════════════════════════════

public sealed class LazyResolutionTests
{
    /// <summary>
    /// 4.7 — Registration with a valid structural config does not throw at build time
    /// even though no real credentials are present.
    /// Construction of the IChatClient is deferred to the first CreateAsync call.
    /// </summary>
    [Fact]
    public void AddAgentWebSearch_NoCredentialsPresent_DoesNotThrowAtRegistration()
    {
        FakeToolRegistration reg = new();

        Exception? ex = Record.Exception(() =>
            reg.AddAgentWebSearch(p =>
            {
                p.Services.AddSingleton<IProviderFactory>(new FakeProviderFactory());
                p.UseModel("fake", "fake-model");
            }));

        Assert.Null(ex);
    }
}
