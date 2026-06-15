using System.Net;
using System.Text;
using Dmon.Abstractions.Providers;
using Dmon.Providers.OpenAI;

namespace Dmon.Providers.Tests;

public sealed class OpenAiProviderFactoryTests
{
    [Fact]
    public void GetCapabilities_O1Model_ReturnsReasoningSupport()
    {
        OpenAiProviderFactory factory = new();
        ChatClientCapabilities caps = factory.GetCapabilities("o1-preview");
        Assert.True(caps.SupportsToolCalling);
        Assert.True(caps.SupportsReasoning);
    }

    [Fact]
    public void GetCapabilities_O3Model_ReturnsReasoningSupport()
    {
        OpenAiProviderFactory factory = new();
        ChatClientCapabilities caps = factory.GetCapabilities("o3-mini");
        Assert.True(caps.SupportsToolCalling);
        Assert.True(caps.SupportsReasoning);
    }

    [Fact]
    public void GetCapabilities_Gpt4oModel_ReturnsToolCallingNoReasoning()
    {
        OpenAiProviderFactory factory = new();
        ChatClientCapabilities caps = factory.GetCapabilities("gpt-4o");
        Assert.True(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }

    [Fact]
    public void GetCapabilities_UnknownModel_ReturnsConservativeDefaults()
    {
        OpenAiProviderFactory factory = new();
        ChatClientCapabilities caps = factory.GetCapabilities("unknown-model-xyz");
        Assert.False(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }

    // 4.1 — custom baseUrl returns all data[].id entries unfiltered
    [Fact]
    public async Task GetAvailableModelsAsync_CustomBaseUrl_ReturnsAllIdsUnfiltered()
    {
        const string json = """
            {
              "data": [
                { "id": "llama3.2" },
                { "id": "mlx-community/Meta-Llama-3.1-8B-Instruct-4bit" }
              ]
            }
            """;

        StubHandler handler = new(HttpStatusCode.OK, json);
        OpenAiProviderFactory factory = new(handler);

        IReadOnlyList<ModelInfo> models = await factory.GetAvailableModelsAsync(
            "any-key", "http://localhost:8080/v1");

        Assert.Equal(2, models.Count);
        Assert.Equal("llama3.2", models[0].Id);
        Assert.Equal("mlx-community/Meta-Llama-3.1-8B-Instruct-4bit", models[1].Id);
    }

    // 4.2 — custom baseUrl + null/empty apiKey → no Authorization header
    [Fact]
    public async Task GetAvailableModelsAsync_CustomBaseUrl_NullApiKey_SendsNoAuthorizationHeader()
    {
        const string json = """{"data": [{"id": "llama3.2"}]}""";

        CapturingHandler handler = new(HttpStatusCode.OK, json);
        OpenAiProviderFactory factory = new(handler);

        await factory.GetAvailableModelsAsync(null, "http://localhost:8080/v1");

        Assert.NotNull(handler.CapturedRequest);
        Assert.False(handler.CapturedRequest!.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task GetAvailableModelsAsync_CustomBaseUrl_EmptyApiKey_SendsNoAuthorizationHeader()
    {
        const string json = """{"data": [{"id": "llama3.2"}]}""";

        CapturingHandler handler = new(HttpStatusCode.OK, json);
        OpenAiProviderFactory factory = new(handler);

        await factory.GetAvailableModelsAsync(string.Empty, "http://localhost:8080/v1");

        Assert.NotNull(handler.CapturedRequest);
        Assert.False(handler.CapturedRequest!.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task GetAvailableModelsAsync_CustomBaseUrl_NonEmptyApiKey_SendsAuthorizationHeader()
    {
        const string json = """{"data": [{"id": "llama3.2"}]}""";

        CapturingHandler handler = new(HttpStatusCode.OK, json);
        OpenAiProviderFactory factory = new(handler);

        await factory.GetAvailableModelsAsync("my-key", "http://localhost:8080/v1");

        Assert.NotNull(handler.CapturedRequest);
        Assert.True(handler.CapturedRequest!.Headers.Contains("Authorization"));
    }

    // 4.3 — custom baseUrl failure → empty list, never fallback
    [Fact]
    public async Task GetAvailableModelsAsync_CustomBaseUrl_Non2xx_ReturnsEmptyList()
    {
        StubHandler handler = new(HttpStatusCode.InternalServerError, string.Empty);
        OpenAiProviderFactory factory = new(handler);

        IReadOnlyList<ModelInfo> models = await factory.GetAvailableModelsAsync(
            "key", "http://localhost:8080/v1");

        Assert.Empty(models);
        AssertNotFallbackList(models);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_CustomBaseUrl_Throws_ReturnsEmptyList()
    {
        ThrowingHandler handler = new();
        OpenAiProviderFactory factory = new(handler);

        IReadOnlyList<ModelInfo> models = await factory.GetAvailableModelsAsync(
            "key", "http://localhost:8080/v1");

        Assert.Empty(models);
        AssertNotFallbackList(models);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_CustomBaseUrl_EmptyData_ReturnsEmptyList()
    {
        const string json = """{"data": []}""";

        StubHandler handler = new(HttpStatusCode.OK, json);
        OpenAiProviderFactory factory = new(handler);

        IReadOnlyList<ModelInfo> models = await factory.GetAvailableModelsAsync(
            "key", "http://localhost:8080/v1");

        Assert.Empty(models);
        AssertNotFallbackList(models);
    }

    // 4.4 regression — null baseUrl + null apiKey → static fallback; successful cloud fetch → gpt-/o filter applied
    [Fact]
    public async Task GetAvailableModelsAsync_NullBaseUrl_NullApiKey_ReturnsFallbackList()
    {
        OpenAiProviderFactory factory = new();

        IReadOnlyList<ModelInfo> models = await factory.GetAvailableModelsAsync(null, null);

        Assert.True(models.Count > 0);
        Assert.Contains(models, m => m.Id == "gpt-4o");
        Assert.Contains(models, m => m.Id == "gpt-4o-mini");
        Assert.Contains(models, m => m.Id == "o3");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_NullBaseUrl_StubbedCloudResponse_AppliesGptOFilter()
    {
        // Response contains both chat (gpt-/o+digit) and non-chat models; only chat models should be returned.
        const string json = """
            {
              "data": [
                { "id": "gpt-4o" },
                { "id": "gpt-4o-mini" },
                { "id": "o3" },
                { "id": "text-embedding-3-large" },
                { "id": "whisper-1" },
                { "id": "tts-1" }
              ]
            }
            """;

        StubHandler handler = new(HttpStatusCode.OK, json);
        OpenAiProviderFactory factory = new(handler);

        IReadOnlyList<ModelInfo> models = await factory.GetAvailableModelsAsync(
            "sk-valid-key", null);

        Assert.Contains(models, m => m.Id == "gpt-4o");
        Assert.Contains(models, m => m.Id == "gpt-4o-mini");
        Assert.Contains(models, m => m.Id == "o3");
        Assert.DoesNotContain(models, m => m.Id == "text-embedding-3-large");
        Assert.DoesNotContain(models, m => m.Id == "whisper-1");
        Assert.DoesNotContain(models, m => m.Id == "tts-1");
    }

    private static void AssertNotFallbackList(IReadOnlyList<ModelInfo> models)
    {
        // The OpenAI fallback list always contains exactly gpt-4o, gpt-4o-mini, and o3.
        bool isFallback = models.Count == 3
            && models.Any(m => m.Id == "gpt-4o")
            && models.Any(m => m.Id == "gpt-4o-mini")
            && models.Any(m => m.Id == "o3");
        Assert.False(isFallback, "Expected empty list but got the static OpenAI fallback list.");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public StubHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public HttpRequestMessage? CapturedRequest { get; private set; }

        public CapturingHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            HttpResponseMessage response = new(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Simulated network failure.");
    }
}
