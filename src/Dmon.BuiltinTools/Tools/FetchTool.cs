using Dmon.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Models;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.BuiltinTools.Tools;

public sealed class FetchTool : IDmonExtension
{
    private readonly HttpClient _httpClient;
    private readonly AIFunction _function;

    public FetchTool(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _function = AIFunctionFactory.Create(
            ExecuteAsync,
            "fetch",
            "Fetch the body of an HTTP URL via GET.");
    }

    public string Name => "Fetch Tool";
    public string Description => "HTTP GET requests.";
    public IEnumerable<AIFunction> Tools => [_function];

    private async Task<string> ExecuteAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"Error: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            }
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
    {
        if (call.Arguments is null || !call.Arguments.TryGetValue("url", out object? urlArg))
            return PermissionResult.Prompt;

        string url = urlArg?.ToString() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return PermissionResult.Prompt;

        string domain = uri.Host;
        foreach (string allowed in project.Settings.Http.Allow)
        {
            if (string.Equals(domain, allowed, StringComparison.OrdinalIgnoreCase))
                return PermissionResult.Allow;
        }
        return PermissionResult.Prompt;
    }

    public ToolConfirmRequest CreateConfirmRequest(FunctionCallContent call)
        => new()
        {
            Id = call.CallId,
            Name = call.Name,
            Args = call.Arguments is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(call.Arguments),
            Risk = RiskLevel.Medium
        };
}
