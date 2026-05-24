using System.Runtime.InteropServices;
using System.Text;
using Dmon.Abstractions;
using Dmon.Abstractions.Providers;
using Dmon.Core.Config;
using Dmon.Core.Extensions;
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

    private static readonly string StaticCore = """
        # Identity

        You are D-mon (pronounced "daemon" or "demon"), a coding agent. You run inside a terminal session and help the user write, edit, and reason about code. You have access to tools for reading files, writing files, running bash commands, and more.

        # Tool usage norms

        - Read a file before editing it.
        - Prefer targeted edits over full rewrites.
        - If the scope of a task is genuinely unclear, ask one short question — do not guess and do not ask multiple questions at once.

        # Permission model

        Bash commands and file writes require explicit user confirmation. The runtime handles this — do not try to work around it or warn the user about it on every turn.

        # Tone

        Informal and terse. Not rude. No padding. No apologies. No phrases like "Certainly!", "Of course!", "Great question!", or "I'd be happy to help". Do not describe what you are about to do — just do it.
        """;

    public SystemPromptBuilder(
        IProviderRegistry providerRegistry,
        IToolRegistry toolRegistry,
        AgentConfigResolver configResolver,
        IEventEmitter eventEmitter)
    {
        _providerRegistry = providerRegistry;
        _toolRegistry = toolRegistry;
        _configResolver = configResolver;
        _eventEmitter = eventEmitter;
    }

    public async Task<ChatMessage> BuildAsync(CancellationToken cancellationToken)
    {
        StringBuilder sb = new();

        sb.AppendLine(StaticCore);

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
