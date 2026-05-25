using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Dmon.Abstractions.Providers;
using Dmon.Extensions.Omlx;

namespace Dmon.Extensions.Omlx.Tests;

public sealed class OmlxProviderExtensionTests
{
    // ---------------------------------------------------------------------------
    // Shared fake handlers
    // ---------------------------------------------------------------------------

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }

    private static HttpClient MakeClient(HttpMessageHandler inner, string baseUrl = "http://localhost:8666")
    {
        OmlxAuthHandler auth = new(string.Empty) { InnerHandler = inner };
        return new HttpClient(auth) { BaseAddress = new Uri(baseUrl) };
    }

    private static OmlxConfig DefaultConfig =>
        new() { BaseUrl = "http://localhost:8666", ApiKey = string.Empty };

    // ---------------------------------------------------------------------------
    // IsApplicable
    // ---------------------------------------------------------------------------

    [Fact]
    public void IsApplicable_MatchesCurrentPlatformAndArch()
    {
        using OmlxProviderExtension sut = new();
        bool expected = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        Assert.Equal(expected, sut.IsApplicable());
    }

    // ---------------------------------------------------------------------------
    // IsRunningAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task IsRunningAsync_Returns_True_When_200_And_OwnedByOmlx()
    {
        const string body = """{"object":"list","data":[{"id":"gemma-4-e4b-it-4bit","object":"model","created":1779658092,"owned_by":"omlx"}]}""";
        FakeHttpHandler inner = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        using OmlxProviderExtension sut = new(DefaultConfig, MakeClient(inner));
        bool result = await sut.IsRunningAsync();
        Assert.True(result);
    }

    [Fact]
    public async Task IsRunningAsync_Returns_False_When_OwnedBy_IsNot_Omlx()
    {
        const string body = """{"object":"list","data":[{"id":"gemma-4-e4b-it-4bit","object":"model","created":1779658092,"owned_by":"other"}]}""";
        FakeHttpHandler inner = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        using OmlxProviderExtension sut = new(DefaultConfig, MakeClient(inner));
        bool result = await sut.IsRunningAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task IsRunningAsync_Returns_False_On_ConnectionRefused()
    {
        using OmlxProviderExtension sut = new(DefaultConfig, MakeClient(new ThrowingHttpHandler()));
        bool result = await sut.IsRunningAsync();
        Assert.False(result);
    }

    // ---------------------------------------------------------------------------
    // ListModelsAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ListModelsAsync_MapsModelsCorrectly()
    {
        const string body = """{"object":"list","data":[{"id":"gemma-4-e4b-it-4bit","owned_by":"omlx"},{"id":"qwen3-8b-4bit","owned_by":"omlx"}]}""";
        FakeHttpHandler inner = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        using OmlxProviderExtension sut = new(DefaultConfig, MakeClient(inner));
        IReadOnlyList<ModelInfo> models = await sut.ListModelsAsync();

        Assert.Equal(2, models.Count);

        Assert.Equal("gemma-4-e4b-it-4bit", models[0].Id);
        // -it- pattern → tools, no reasoning
        Assert.True(models[0].Capabilities.SupportsToolCalling);
        Assert.False(models[0].Capabilities.SupportsReasoning);

        Assert.Equal("qwen3-8b-4bit", models[1].Id);
        // qwen3 prefix → tools + reasoning
        Assert.True(models[1].Capabilities.SupportsToolCalling);
        Assert.True(models[1].Capabilities.SupportsReasoning);
    }

    [Fact]
    public async Task ListModelsAsync_ReturnsEmpty_OnHttpError()
    {
        FakeHttpHandler inner = new(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using OmlxProviderExtension sut = new(DefaultConfig, MakeClient(inner));
        IReadOnlyList<ModelInfo> models = await sut.ListModelsAsync();
        Assert.Empty(models);
    }

    [Fact]
    public async Task ListModelsAsync_ReturnsEmpty_OnException()
    {
        using OmlxProviderExtension sut = new(DefaultConfig, MakeClient(new ThrowingHttpHandler()));
        IReadOnlyList<ModelInfo> models = await sut.ListModelsAsync();
        Assert.Empty(models);
    }

    // ---------------------------------------------------------------------------
    // EnsureRunningAsync — uses injectable probe + short timeout override
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task EnsureRunningAsync_NoOp_WhenAlreadyRunning()
    {
        // Probe returns true immediately — no process launch, no polling
        using OmlxProviderExtension sut = new(DefaultConfig, _ => Task.FromResult(true));
        await sut.EnsureRunningAsync(TimeSpan.FromSeconds(5));
        // No exception = pass
    }

    [Fact]
    public async Task EnsureRunningAsync_Throws_TimeoutException_WhenServerNeverResponds()
    {
        // Probe always returns false — server never comes up
        using OmlxProviderExtension sut = new(DefaultConfig, _ => Task.FromResult(false));

        // Use 2 s timeout so the test is fast; poll interval is 1 s so we get ~2 polls
        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.EnsureRunningAsync(TimeSpan.FromSeconds(2)));
    }

    // ---------------------------------------------------------------------------
    // CreateFactory
    // ---------------------------------------------------------------------------

    [Fact]
    public void CreateFactory_Returns_OmlxProviderFactory()
    {
        using OmlxProviderExtension sut = new();
        IProviderFactory factory = sut.CreateFactory();
        Assert.IsType<OmlxProviderFactory>(factory);
    }
}
