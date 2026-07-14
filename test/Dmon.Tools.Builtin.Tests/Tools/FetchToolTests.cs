using System.Net;
using System.Net.Sockets;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Dmon.Tools.Builtin.Tools;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Builtin.Tests.Tools;

public sealed class FetchToolTests
{
    private static IPermissionSettings MakeSettings(params string[] httpAllow)
        => new StubPermissionSettings(new PermissionSettings
        {
            Http = new TierSettings { Allow = httpAllow }
        });

    private sealed class StubPermissionSettings(PermissionSettings settings) : IPermissionSettings
    {
        public PermissionSettings Settings => settings;
        public Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingHandler(HttpResponseMessage? response = null) : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(response ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("body")
            });
        }
    }

    private static async Task<string> InvokeFetchAsync(FetchTool tool, string url)
    {
        AIFunction function = tool.Tools.Single();
        object? result = await function.InvokeAsync(new AIFunctionArguments { ["url"] = url });
        return result?.ToString() ?? string.Empty;
    }

    private static IPAddress[] Ips(params string[] addresses)
        => addresses.Select(IPAddress.Parse).ToArray();

    [Theory]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    public async Task Execute_RefusedLiteralHost_ReturnsErrorAndNeverIssuesRequest(string url)
    {
        RecordingHandler handler = new();
        FetchTool tool = new(new HttpClient(handler), MakeSettings());

        string result = await InvokeFetchAsync(tool, url);

        Assert.StartsWith("Error:", result);
        Assert.False(handler.WasCalled);
    }

    [Theory]
    [InlineData("http://172.15.0.1/")]
    [InlineData("http://172.32.0.1/")]
    public async Task Execute_PublicLiteralHostNearPrivateBoundary_IssuesRequest(string url)
    {
        RecordingHandler handler = new();
        FetchTool tool = new(new HttpClient(handler), MakeSettings());

        string result = await InvokeFetchAsync(tool, url);

        Assert.True(handler.WasCalled);
        Assert.Equal("body", result);
    }

    [Fact]
    public async Task Execute_HostnameResolvingToPrivate_IsRefused()
    {
        RecordingHandler handler = new();
        FetchTool tool = new(
            new HttpClient(handler),
            MakeSettings(),
            (_, _) => Task.FromResult(Ips("10.0.0.5")));

        string result = await InvokeFetchAsync(tool, "http://internal.example.com/");

        Assert.StartsWith("Error:", result);
        Assert.False(handler.WasCalled);
    }

    [Fact]
    public async Task Execute_HostnameResolvingToMixedPublicAndPrivate_IsRefused()
    {
        RecordingHandler handler = new();
        FetchTool tool = new(
            new HttpClient(handler),
            MakeSettings(),
            (_, _) => Task.FromResult(Ips("93.184.216.34", "192.168.0.10")));

        string result = await InvokeFetchAsync(tool, "http://rebind.example.com/");

        Assert.StartsWith("Error:", result);
        Assert.False(handler.WasCalled);
    }

    [Fact]
    public async Task Execute_HostnameResolvingToAllPublic_IsPermitted()
    {
        RecordingHandler handler = new();
        FetchTool tool = new(
            new HttpClient(handler),
            MakeSettings(),
            (_, _) => Task.FromResult(Ips("93.184.216.34", "93.184.216.35")));

        string result = await InvokeFetchAsync(tool, "http://public.example.com/");

        Assert.True(handler.WasCalled);
        Assert.Equal("body", result);
    }

    [Fact]
    public async Task Execute_HostnameResolutionFails_ReturnsErrorAndDoesNotThrow()
    {
        RecordingHandler handler = new();
        FetchTool tool = new(
            new HttpClient(handler),
            MakeSettings(),
            (_, _) => throw new SocketException((int)SocketError.HostNotFound));

        string result = await InvokeFetchAsync(tool, "http://nxdomain.example.com/");

        Assert.StartsWith("Error:", result);
        Assert.False(handler.WasCalled);
    }

    [Fact]
    public async Task Execute_NonHttpScheme_ReturnsErrorAndNeverIssuesRequest()
    {
        RecordingHandler handler = new();
        FetchTool tool = new(new HttpClient(handler), MakeSettings());

        string result = await InvokeFetchAsync(tool, "file:///etc/passwd");

        Assert.StartsWith("Error:", result);
        Assert.False(handler.WasCalled);
    }

    [Fact]
    public async Task Execute_AllowlistedPrivateHost_IssuesRequestAndReturnsBody()
    {
        RecordingHandler handler = new();
        FetchTool tool = new(new HttpClient(handler), MakeSettings("10.0.0.1"));

        string result = await InvokeFetchAsync(tool, "http://10.0.0.1/");

        Assert.True(handler.WasCalled);
        Assert.Equal("body", result);
    }

    [Fact]
    public async Task Execute_PublicLiteralHost_IssuesRequestAndReturnsBody()
    {
        RecordingHandler handler = new();
        FetchTool tool = new(new HttpClient(handler), MakeSettings());

        string result = await InvokeFetchAsync(tool, "http://192.0.2.1/");

        Assert.True(handler.WasCalled);
        Assert.Equal("body", result);
    }

    [Fact]
    public void Evaluate_LiteralPrivateHost_EmptyAllowlist_ReturnsPrompt()
    {
        FetchTool tool = new(new HttpClient(new RecordingHandler()), MakeSettings());
        FunctionCallContent call = new("call-1", "fetch",
            new Dictionary<string, object?> { ["url"] = "http://10.0.0.1/" });

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Prompt, result);
    }

    [Fact]
    public void Evaluate_LiteralPrivateHost_Allowlisted_ReturnsAllow()
    {
        FetchTool tool = new(new HttpClient(new RecordingHandler()), MakeSettings());
        IPermissionSettings project = MakeSettings("10.0.0.1");
        FunctionCallContent call = new("call-1", "fetch",
            new Dictionary<string, object?> { ["url"] = "http://10.0.0.1/" });

        PermissionResult result = tool.Evaluate(call, project, null);

        Assert.Equal(PermissionResult.Allow, result);
    }
}
