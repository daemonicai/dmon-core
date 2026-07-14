using Dmail.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmail.Tests;

/// <summary>
/// Boot-level proof that the X-Api-Key middleware is wired into the REAL pipeline
/// (Program.cs) at the right position — the unit tests in
/// ApiKeyAuthMiddlewareTests only prove InvokeAsync's logic in isolation, not that
/// a future middleware reorder in Program.cs can't silently reopen the /api boundary.
/// </summary>
public sealed class ApiKeyAuthIntegrationTests : IDisposable
{
    private const string ApiKey = "integration-test-key";

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;

    public ApiKeyAuthIntegrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"dmail-apikey-it-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dataDir);

        Environment.SetEnvironmentVariable("DMAIL_API_KEY", ApiKey);
        Environment.SetEnvironmentVariable("DMAIL_DATA_DIR", _dataDir);
        // Public OAuth client id (no secret) so the login handler can build the
        // Google authorization redirect deterministically — proves the exempt
        // GET path reaches the handler and issues a 302 rather than a config 500.
        Environment.SetEnvironmentVariable("DMAIL_GOOGLE_CLIENT_ID", "test-google-client-id");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Real ONNX/embedding registration requires the bundled model files;
                // replace it with a stub so Program.cs's top-level
                // `await embedding.ValidateModelAsync()` runs against an in-memory
                // fake instead of loading the real ONNX model. Unlike
                // FtsSnippetIntegrationTests's StubEmbeddingGenerator (which returns a
                // zero-length vector to keep IsModelReady=false), this one returns a
                // correctly-dimensioned vector so ValidateModelAsync succeeds and
                // /health reports "healthy" end-to-end.
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                    new ReadyStubEmbeddingGenerator());
            });
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DMAIL_API_KEY", null);
        Environment.SetEnvironmentVariable("DMAIL_DATA_DIR", null);
        Environment.SetEnvironmentVariable("DMAIL_GOOGLE_CLIENT_ID", null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ApiStatus_NoKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/status");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiStatus_CorrectKey_ReachesHandler()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        var response = await client.GetAsync("/api/status");

        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_NoKey_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OAuthCallback_NoKey_ReachesHandlerAndRejectsUnknownState()
    {
        var client = _factory.CreateClient();

        // Both code and state are required query params — pass both so the model
        // binder succeeds and we exercise the handler's state fence (not a binding
        // 400). Unknown state => the handler's own 400 invalid_state, which proves
        // the middleware let the request through (401 would mean the exemption
        // failed to wire) AND the state/PKCE fence is intact.
        var response = await client.GetAsync("/api/auth/google/callback?code=x&state=does-not-exist");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_state", body);
    }

    [Fact]
    public async Task OAuthLogin_NoKey_ReachesHandlerAndRedirects()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/auth/google/login");

        // Not 401 (exemption wired) and the handler issued the Google redirect.
        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task OtherApiAuthPath_NoKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/other");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// Test double: returns a correctly-dimensioned (384) embedding so
/// EmbeddingService.ValidateModelAsync succeeds and IsModelReady becomes true,
/// letting /health report "healthy" without a real ONNX model.
/// </summary>
internal sealed class ReadyStubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } =
        new EmbeddingGeneratorMetadata("ready-stub", new Uri("http://stub"), "ready-stub");

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var embeddings = values
            .Select(_ => new Embedding<float>(new float[EmbeddingService.EmbeddingDimensions]))
            .ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
