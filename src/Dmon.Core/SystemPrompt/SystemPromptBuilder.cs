using System.Runtime.InteropServices;
using System.Text;
using Dmon.Abstractions;
using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Core.Config;
using Dmon.Core.Extensions;
using Dmon.Core.Session;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Hosting;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Dmon.Core.SystemPrompt;

public sealed class SystemPromptBuilder : ISystemPromptBuilder
{
    private readonly IProviderRegistry _providerRegistry;
    private readonly IToolRegistry _toolRegistry;
    private readonly AgentConfigResolver _configResolver;
    private readonly IEventEmitter _eventEmitter;
    private readonly AssetsOptions? _assetsOptions;
    private readonly ISessionHandler _sessionHandler;
    private readonly IConfiguration _configuration;
    private readonly IEnumerable<SystemPromptAppend> _appends;

    public SystemPromptBuilder(
        IProviderRegistry providerRegistry,
        IToolRegistry toolRegistry,
        AgentConfigResolver configResolver,
        IEventEmitter eventEmitter,
        ISessionHandler sessionHandler,
        IConfiguration configuration,
        IEnumerable<SystemPromptAppend> appends,
        AssetsOptions? assetsOptions = null)
    {
        _providerRegistry = providerRegistry;
        _toolRegistry = toolRegistry;
        _configResolver = configResolver;
        _eventEmitter = eventEmitter;
        _assetsOptions = assetsOptions;
        _sessionHandler = sessionHandler;
        _configuration = configuration;
        _appends = appends;
    }

    public async Task<ChatMessage> BuildAsync(CancellationToken cancellationToken)
    {
        StringBuilder sb = new();

        string promptBase = ResolveBase();
        sb.Append(promptBase);

        foreach (SystemPromptAppend append in _appends)
        {
            sb.Append(append.Text);
        }

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

    /// <summary>
    /// Resolves the base string: <c>UseSystemPrompt</c> (config key written by the verb)
    /// beats <c>IConfiguration["systemPrompt"]</c> from YAML/env; both beat the built-in default.
    /// Because <c>UseSystemPrompt</c> writes via <c>AddInMemoryCollection</c> (last-wins in
    /// the configuration pipeline), reading the config key returns the highest-priority value
    /// among YAML layers and verb overrides in a single read.
    /// </summary>
    private string ResolveBase()
        => _configuration[ConfigurationKeys.SystemPrompt] ?? DefaultSystemPrompt.Text;

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

        if (_assetsOptions is not null)
        {
            string? sessionId = _sessionHandler.CurrentSession?.Id;
            if (sessionId is not null)
            {
                string root = _assetsOptions.Path ?? cwd;
                string assetDir = SessionAssetPath.Compute(root, sessionId);
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
