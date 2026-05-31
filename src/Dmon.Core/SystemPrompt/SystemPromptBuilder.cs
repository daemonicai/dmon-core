using System.Runtime.InteropServices;
using System.Text;
using Dmon.Abstractions;
using Dmon.Abstractions.Providers;
using Dmon.Core.Config;
using Dmon.Core.Extensions;
using Dmon.Core.Profiles;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;

namespace Dmon.Core.SystemPrompt;

public sealed class SystemPromptBuilder : ISystemPromptBuilder
{
    private readonly IProviderRegistry _providerRegistry;
    private readonly IToolRegistry _toolRegistry;
    private readonly AgentConfigResolver _configResolver;
    private readonly IEventEmitter _eventEmitter;
    private readonly AgentProfileContext _profileContext;
    private readonly ISessionHandler _sessionHandler;

    public SystemPromptBuilder(
        IProviderRegistry providerRegistry,
        IToolRegistry toolRegistry,
        AgentConfigResolver configResolver,
        IEventEmitter eventEmitter,
        AgentProfileContext profileContext,
        ISessionHandler sessionHandler)
    {
        _providerRegistry = providerRegistry;
        _toolRegistry = toolRegistry;
        _configResolver = configResolver;
        _eventEmitter = eventEmitter;
        _profileContext = profileContext;
        _sessionHandler = sessionHandler;
    }

    public async Task<ChatMessage> BuildAsync(CancellationToken cancellationToken)
    {
        StringBuilder sb = new();

        sb.AppendLine(_profileContext.Profile.Persona);

        AppendDynamicContext(sb);

        AgentConfigResult config = await _configResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (config.ClaudeMdUsed)
        {
            await _eventEmitter.EmitAsync(new SystemNoticeEvent
            {
                Message = "Found CLAUDE.md — using it as project config. Rename to AGENTS.md to suppress this notice."
            }, cancellationToken).ConfigureAwait(false);
        }

        if (config.Text is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Project configuration");
            sb.AppendLine();
            sb.Append(config.Text);
        }

        return new ChatMessage(ChatRole.System, sb.ToString());
    }

    private void AppendDynamicContext(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("## Session context");
        sb.AppendLine();

        string cwd = Directory.GetCurrentDirectory();
        sb.AppendLine($"- **Working directory:** {cwd}");

        string os = RuntimeInformation.OSDescription;
        string platform = RuntimeInformation.RuntimeIdentifier;
        sb.AppendLine($"- **OS:** {os} ({platform})");

        ProviderConfig providerConfig = _providerRegistry.GetCurrentConfig();
        string modelId = providerConfig.DefaultModelId ?? "(unknown)";
        sb.AppendLine($"- **Provider:** {providerConfig.Name} / {modelId}");

        if (_profileContext.Profile.Assets)
        {
            string? sessionId = _sessionHandler.CurrentSession?.Id;
            if (sessionId is not null)
            {
                string assetDir = ProfileAssetPath.Compute(cwd, sessionId);
                sb.AppendLine($"- **Asset directory:** {assetDir}");
            }
        }

        IReadOnlyList<RegisteredExtensionSnapshot> extensions = _toolRegistry.GetSnapshot();
        if (extensions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Loaded extensions");
            sb.AppendLine();
            foreach (RegisteredExtensionSnapshot ext in extensions)
            {
                sb.AppendLine($"- {ext.Name} ({ext.ToolCount} tool{(ext.ToolCount == 1 ? "" : "s")})");
            }
        }
    }
}
