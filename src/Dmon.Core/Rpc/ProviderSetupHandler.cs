using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

public sealed class ProviderSetupHandler : IProviderSetupHandler
{
    private readonly IEventEmitter _emitter;

    public ProviderSetupHandler(IEventEmitter emitter)
    {
        _emitter = emitter;
    }

    public async Task ConfigureAsync(ProviderConfigureCommand command, CancellationToken cancellationToken)
    {
        string configPath;
        if (command.Scope == "global")
        {
            configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dmon",
                "config.yaml");
        }
        else if (command.Scope == "local")
        {
            configPath = Path.Combine(Directory.GetCurrentDirectory(), ".dmon", "config.yaml");
        }
        else
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "provider.configure.failed",
                Message = $"Unknown scope '{command.Scope}'. Expected 'global' or 'local'.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(command.Adapter))
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "provider.configure.failed",
                Message = "Adapter must not be empty.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(command.ModelId))
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "provider.configure.failed",
                Message = "ModelId must not be empty.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(command.EnvVar))
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "provider.configure.failed",
                Message = "EnvVar must not be empty.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            string directory = Path.GetDirectoryName(configPath)!;
            Directory.CreateDirectory(directory);

            string stanzaBody = BuildStanzaBody(command.Adapter, command.ModelId, command.EnvVar);

            string content;
            if (!File.Exists(configPath))
            {
                content = "providers:\n" + stanzaBody;
            }
            else
            {
                string existing = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
                content = InsertProviderStanza(existing, stanzaBody);
            }

            await File.WriteAllTextAsync(configPath, content, cancellationToken).ConfigureAwait(false);

            await _emitter.EmitAsync(new ProviderConfiguredEvent
            {
                Adapter = command.Adapter,
                ModelId = command.ModelId,
                Scope = command.Scope
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "provider.configure.failed",
                Message = ex.Message,
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // The provider stanza body: the indented child lines that live under the
    // top-level `providers:` mapping. The provider key is the adapter name
    // (e.g. "anthropic"), which is also the value of the nested `adapter` field.
    private static string BuildStanzaBody(string adapter, string modelId, string envVar)
    {
        return $"  {adapter}:\n    adapter: {adapter}\n    defaultModelId: {modelId}\n    auth:\n      type: envVar\n      envVar: {envVar}\n";
    }

    // Splices the stanza directly beneath the top-level `providers:` line so the
    // new provider is a child of that mapping. A naive end-of-file append would
    // orphan the indented block under whatever trailing content exists (comments,
    // other top-level keys), producing YAML the config loader cannot read.
    private static string InsertProviderStanza(string existing, string stanzaBody)
    {
        string[] lines = existing.Split('\n');
        int providersIndex = Array.FindIndex(lines, IsTopLevelProvidersLine);

        if (providersIndex < 0)
        {
            return existing.TrimEnd() + "\n\nproviders:\n" + stanzaBody;
        }

        IEnumerable<string> head = lines.Take(providersIndex + 1);
        IEnumerable<string> tail = lines.Skip(providersIndex + 1);
        return string.Join('\n', head) + "\n" + stanzaBody + string.Join('\n', tail);
    }

    // A top-level `providers:` mapping key: at column zero (no leading whitespace)
    // and not a comment.
    private static bool IsTopLevelProvidersLine(string line)
    {
        if (line.StartsWith('#') || line.Length == 0 || char.IsWhiteSpace(line[0]))
        {
            return false;
        }

        return line.TrimEnd() == "providers:";
    }
}
