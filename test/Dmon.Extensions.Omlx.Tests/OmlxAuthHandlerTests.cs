using System.Net;
using Dmon.Extensions.Omlx;

namespace Dmon.Extensions.Omlx.Tests;

public sealed class OmlxAuthHandlerTests
{
    private sealed class CapturingHandler : DelegatingHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient client, CapturingHandler capturer) BuildClient(string apiKey)
    {
        CapturingHandler capturer = new();
        OmlxAuthHandler handler = new(apiKey) { InnerHandler = capturer };
        return (new HttpClient(handler), capturer);
    }

    [Fact]
    public async Task SendAsync_WithApiKey_InjectsXApiKeyHeader()
    {
        (HttpClient client, CapturingHandler capturer) = BuildClient("test-key-123");
        await client.GetAsync("http://localhost/v1/models");

        Assert.NotNull(capturer.CapturedRequest);
        Assert.True(capturer.CapturedRequest!.Headers.TryGetValues("x-api-key", out IEnumerable<string>? values));
        Assert.Equal("test-key-123", values!.Single());
    }

    [Fact]
    public async Task SendAsync_WithEmptyApiKey_OmitsXApiKeyHeader()
    {
        (HttpClient client, CapturingHandler capturer) = BuildClient(string.Empty);
        await client.GetAsync("http://localhost/v1/models");

        Assert.NotNull(capturer.CapturedRequest);
        Assert.False(capturer.CapturedRequest!.Headers.Contains("x-api-key"));
    }

    [Fact]
    public async Task SendAsync_NeverSendsAuthorizationHeader()
    {
        (HttpClient client, CapturingHandler capturer) = BuildClient("some-key");
        await client.GetAsync("http://localhost/v1/models");

        Assert.NotNull(capturer.CapturedRequest);
        Assert.False(capturer.CapturedRequest!.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task SendAsync_WithEmptyApiKey_NeverSendsAuthorizationHeader()
    {
        (HttpClient client, CapturingHandler capturer) = BuildClient(string.Empty);
        await client.GetAsync("http://localhost/v1/models");

        Assert.NotNull(capturer.CapturedRequest);
        Assert.False(capturer.CapturedRequest!.Headers.Contains("Authorization"));
    }
}
