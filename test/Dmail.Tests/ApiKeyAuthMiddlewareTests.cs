using System.Text.Json;
using Dmail.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmail.Tests;

public class ApiKeyAuthMiddlewareTests
{
    private const string ApiKey = "test-key-123";

    private static IServiceProvider BuildServices(string apiKey = ApiKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DMAIL_API_KEY"] = apiKey })
            .Build();
        var apiKeyService = new ApiKeyService(config, NullLogger<ApiKeyService>.Instance);

        return new ServiceCollection()
            .AddSingleton(apiKeyService)
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
    }

    private static DefaultHttpContext BuildContext(string path, string? apiKey, IServiceProvider services)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
        context.Request.Path = path;
        if (apiKey is not null)
        {
            context.Request.Headers["X-Api-Key"] = apiKey;
        }

        return context;
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/accounts")]
    [InlineData("/api/accounts/user%40example.com/sync")]
    [InlineData("/api/auth/google/login")]
    public async Task NoKey_ApiPath_Returns401AndDoesNotCallNext(string path)
    {
        var services = BuildServices();
        var context = BuildContext(path, apiKey: null, services);
        var nextCalled = false;

        await ApiKeyAuthExtensions.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task WrongKey_ApiPath_Returns401AndDoesNotCallNext()
    {
        var services = BuildServices();
        var context = BuildContext("/api/status", apiKey: "wrong-key", services);
        var nextCalled = false;

        await ApiKeyAuthExtensions.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/accounts")]
    [InlineData("/api/accounts/user%40example.com/sync")]
    [InlineData("/api/auth/google/login")]
    [InlineData("/api/auth/google/callback")]
    public async Task CorrectKey_ApiPath_CallsNext(string path)
    {
        var services = BuildServices();
        var context = BuildContext(path, apiKey: ApiKey, services);
        var nextCalled = false;

        await ApiKeyAuthExtensions.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/js/dashboard.js")]
    public async Task NoKey_NonApiPath_CallsNext(string path)
    {
        var services = BuildServices();
        var context = BuildContext(path, apiKey: null, services);
        var nextCalled = false;

        await ApiKeyAuthExtensions.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public void HealthResponse_SerializesExactWireNames_AndOmitsIdleConnections()
    {
        var json = JsonSerializer.Serialize(new HealthResponse("healthy", true, true));

        Assert.Contains("\"status\":\"healthy\"", json);
        Assert.Contains("\"model_loaded\":true", json);
        Assert.Contains("\"database_ok\":true", json);
        Assert.DoesNotContain("idle_connections", json);
        Assert.DoesNotContain("account", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthResponse_Degraded_SerializesExpectedStatus()
    {
        var json = JsonSerializer.Serialize(new HealthResponse("degraded", false, true));

        Assert.Contains("\"status\":\"degraded\"", json);
        Assert.Contains("\"model_loaded\":false", json);
        Assert.DoesNotContain("idle_connections", json);
    }
}
