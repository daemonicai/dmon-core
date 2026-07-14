using System.Net;
using System.Net.Sockets;
using Dmon.Abstractions.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Models;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Builtin.Tools;

public sealed class FetchTool : IToolExtension
{
    private readonly HttpClient _httpClient;
    private readonly IPermissionSettings _permissionSettings;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;
    private readonly AIFunction _function;

    public FetchTool(HttpClient httpClient, IPermissionSettings permissionSettings)
        : this(httpClient, permissionSettings, Dns.GetHostAddressesAsync)
    {
    }

    internal FetchTool(
        HttpClient httpClient,
        IPermissionSettings permissionSettings,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveHost)
    {
        _httpClient = httpClient;
        _permissionSettings = permissionSettings;
        _resolveHost = resolveHost;
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
            string? refusal = await CheckSsrfAsync(url, cancellationToken);
            if (refusal is not null)
                return refusal;

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

    /// <summary>
    /// Execute-time SSRF guard: resolves the target host and refuses the request when a
    /// resolved address falls in a refused range, unless the host is on the HTTP allowlist.
    /// This is the authoritative check (DNS may rebind between <see cref="Evaluate"/> and
    /// the request). Returns an <c>"Error:"</c>-prefixed string to refuse, or
    /// <see langword="null"/> to proceed. Never throws.
    /// </summary>
    private async Task<string?> CheckSsrfAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return null;

        if (uri.Scheme is not ("http" or "https"))
            return $"Error: refusing to fetch '{url}': only http and https URLs are supported";

        // Allowlist matches against uri.Host (same host string as Evaluate) — an IPv6 literal
        // must therefore be allowlisted in its bracketed form (e.g. "[fc00::1]").
        if (IsAllowlisted(uri.Host))
            return null;

        if (IPAddress.TryParse(uri.DnsSafeHost, out IPAddress? literal))
        {
            return SsrfGuard.IsRefused(literal)
                ? $"Error: refusing to fetch '{url}': host resolves to a loopback or private-network address"
                : null;
        }

        IPAddress[] resolved;
        try
        {
            resolved = await _resolveHost(uri.DnsSafeHost, cancellationToken);
        }
        catch (SocketException ex)
        {
            return $"Error: failed to resolve host '{uri.DnsSafeHost}': {ex.Message}";
        }
        catch (ArgumentException ex)
        {
            return $"Error: failed to resolve host '{uri.DnsSafeHost}': {ex.Message}";
        }

        return SsrfGuard.AnyRefused(resolved)
            ? $"Error: refusing to fetch '{url}': host resolves to a loopback or private-network address"
            : null;
    }

    private bool IsAllowlisted(string host)
    {
        foreach (string allowed in _permissionSettings.Settings.Http.Allow)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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

        // A refused literal-IP host reaches Allow only via an exact allowlist match below;
        // everything else (including any non-allowlisted refused IP) falls through to Prompt.
        // The execute-time guard in CheckSsrfAsync is the authoritative SSRF enforcement.
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
