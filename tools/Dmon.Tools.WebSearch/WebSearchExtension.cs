using System.ComponentModel;
using System.Text;
using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Models;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.WebSearch;

/// <summary>
/// dmon extension exposing a <c>web_search</c> tool that delegates web search and synthesis
/// to a hosted, search-capable model via <see cref="HostedWebSearchTool"/>. The driving agent
/// makes no direct network requests — the hosted provider handles search internally.
/// </summary>
/// <remarks>
/// Wire it into a composition root via the <c>AddAgentWebSearch</c> verb from
/// <c>Dmon.Hosting</c>, which supplies the <see cref="IChatClientFactory"/> for the
/// sub-agent brain. The factory is resolved lazily: a missing API key does not block
/// startup; it surfaces as an error string on the first tool invocation.
/// </remarks>
public sealed class WebSearchExtension : IToolExtension
{
    private readonly IChatClientFactory _factory;
    private readonly AIFunction[] _tools;

    /// <summary>Creates the extension with the given sub-agent client factory.</summary>
    /// <param name="factory">
    /// Produces the <see cref="IChatClient"/> used to run the hosted web-search call.
    /// Captured at construction time; <see cref="IChatClientFactory.CreateAsync"/> is
    /// called on each tool invocation.
    /// </param>
    public WebSearchExtension(IChatClientFactory factory)
    {
        _factory = factory;
        _tools =
        [
            DmonAIFunctionFactory.Create(
                SearchAsync,
                "web_search",
                "Ask the web a question and get a synthesised, cited answer. Delegates to a " +
                "hosted search-capable model that fetches and reads relevant pages, then " +
                "returns a concise answer with source citations. Use for current events, " +
                "factual look-ups, or any question that requires information beyond the " +
                "agent's training cutoff."),
        ];
    }

    /// <inheritdoc />
    public string Name => "WebSearch";

    /// <inheritdoc />
    public string Description =>
        "Web search tool: delegates search and synthesis to a hosted model and returns a " +
        "structured answer with source citations.";

    /// <inheritdoc />
    public IEnumerable<AIFunction> Tools => _tools;

    /// <summary>
    /// Returns <see cref="PermissionResult.Prompt"/> for <c>web_search</c> because the
    /// query leaves the device to a hosted provider (network egress).
    /// </summary>
    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => PermissionResult.Prompt;

    /// <summary>
    /// Assigns <see cref="RiskLevel.Medium"/> to reflect network egress: the query is
    /// sent to a third-party hosted model outside the local machine.
    /// </summary>
    public ToolConfirmRequest CreateConfirmRequest(FunctionCallContent call)
        => new()
        {
            Id = call.CallId,
            Name = call.Name,
            Args = call.Arguments is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(call.Arguments),
            Risk = RiskLevel.Medium,
        };

    private async Task<string> SearchAsync(
        [Description("The question or search query to send to the web.")]
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Provide a non-empty query to search for.";

        try
        {
            IChatClient client = await _factory.CreateAsync(cancellationToken).ConfigureAwait(false);

            ChatResponse response = await client.GetResponseAsync(
                query,
                new ChatOptions { Tools = [new HostedWebSearchTool()] },
                cancellationToken).ConfigureAwait(false);

            return FormatResult(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Could not run web search: {ex.Message}";
        }
    }

    private static string FormatResult(ChatResponse response)
    {
        string answer = response.Text ?? string.Empty;

        List<(string title, Uri uri)> sources = [];
        foreach (ChatMessage message in response.Messages)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is not WebSearchToolResultContent searchResult)
                    continue;

                foreach (AIContent output in searchResult.Outputs ?? [])
                {
                    if (output is not UriContent uriContent)
                        continue;

                    string title = uriContent.AdditionalProperties is { } props
                        && props.TryGetValue("title", out object? titleValue)
                        && titleValue is string titleString
                        && titleString.Length > 0
                        ? titleString
                        : uriContent.Uri.Host;

                    sources.Add((title, uriContent.Uri));
                }
            }
        }

        if (sources.Count == 0)
            return answer;

        var sb = new StringBuilder(answer);
        sb.AppendLine();
        sb.AppendLine();
        sb.Append("Sources:");
        for (int i = 0; i < sources.Count; i++)
        {
            (string title, Uri uri) = sources[i];
            sb.AppendLine();
            sb.Append($"[{i + 1}] {title} — {uri}");
        }

        return sb.ToString();
    }
}
