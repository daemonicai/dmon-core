using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;

namespace Dmon.Terminal;

/// <summary>
/// Parses user input into either a regular message (returned as-is) or a
/// slash command mapped to the corresponding protocol command.
/// </summary>
public static class SlashCommandParser
{
    /// <summary>Client-side dispatch tags — not sent to the core process.</summary>
    public enum ClientCommandKind { None, AddProvider, Reload }

    public sealed class ParseResult
    {
        public Command? Command { get; init; }

        /// <summary>
        /// A client-side-only dispatch tag. Check this before <see cref="Command"/>.
        /// </summary>
        public ClientCommandKind ClientCommand { get; init; }

        public string? Error { get; init; }

        public bool IsExit { get; init; }

        public bool IsSlashCommand { get; init; }
    }

    /// <summary>
    /// Parses the user input line.
    /// If the input starts with '/', it is interpreted as a slash command.
    /// Otherwise, it is treated as a regular message.
    /// </summary>
    public static ParseResult Parse(string input)
    {
        input = input.Trim();
        if (!input.StartsWith('/'))
        {
            return new ParseResult { IsSlashCommand = false };
        }

        string[] parts = input[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return new ParseResult { IsSlashCommand = true, Error = "Empty slash command." };
        }

        string cmd = parts[0].ToLowerInvariant();
        string[] args = parts.Skip(1).ToArray();
        string id = Guid.NewGuid().ToString("N");

        return cmd switch
        {
            "quit" or "exit" => new ParseResult { IsSlashCommand = true, IsExit = true },

            "add-provider" => ParseAddProvider(args),

            "new" => new ParseResult
            {
                IsSlashCommand = true,
                Command = new SessionCreateCommand { Id = id }
            },

            "fork" => new ParseResult
            {
                IsSlashCommand = true,
                Command = new SessionForkCommand { Id = id, EntryId = "" } // filled in by host
            },

            "clone" => new ParseResult
            {
                IsSlashCommand = true,
                Command = new SessionCloneCommand { Id = id }
            },

            "model" => ParseModel(id, args),

            "login" => ParseLogin(id, args),
            "logout" => ParseLogout(id, args),

            "load" => ParseLoad(id, args),
            "unload" => ParseUnload(id, args),
            "promote" => ParsePromote(id, args),

            "thinking" => ParseThinking(id, args),

            "reload" => ParseReload(args),

            _ => new ParseResult
            {
                IsSlashCommand = true,
                Error = $"Unknown command: /{cmd}"
            }
        };
    }

    private static ParseResult ParseModel(string id, string[] args)
    {
        if (args.Length == 0)
            return new ParseResult { IsSlashCommand = true, Command = new ModelListCommand { Id = id } };

        if (args.Length == 1)
            return new ParseResult { IsSlashCommand = true, Error = "Usage: /model <provider> <modelId>" };

        return new ParseResult
        {
            IsSlashCommand = true,
            Command = new ModelSetCommand
            {
                Id = id,
                Provider = args[0],
                ModelId = args[1]
            }
        };
    }

    private static ParseResult ParseLogin(string id, string[] args)
    {
        if (args.Length == 0)
            return new ParseResult { IsSlashCommand = true, Error = "Usage: /login <provider>" };

        return new ParseResult
        {
            IsSlashCommand = true,
            Command = new AuthLoginCommand { Id = id, Provider = args[0] }
        };
    }

    private static ParseResult ParseLogout(string id, string[] args)
    {
        if (args.Length == 0)
            return new ParseResult { IsSlashCommand = true, Error = "Usage: /logout <provider>" };

        return new ParseResult
        {
            IsSlashCommand = true,
            Command = new AuthLogoutCommand { Id = id, Provider = args[0] }
        };
    }

    private static ParseResult ParseLoad(string id, string[] args)
    {
        if (args.Length == 0)
            return new ParseResult { IsSlashCommand = true, Error = "Usage: /load <source> [project|user]" };

        return new ParseResult
        {
            IsSlashCommand = true,
            Command = new ExtensionLoadCommand { Id = id, Source = args[0], Scope = args.Length > 1 ? args[1] : null }
        };
    }

    private static ParseResult ParseUnload(string id, string[] args)
    {
        if (args.Length == 0)
            return new ParseResult { IsSlashCommand = true, Error = "Usage: /unload <name>" };

        return new ParseResult
        {
            IsSlashCommand = true,
            Command = new ExtensionUnloadCommand { Id = id, Name = args[0] }
        };
    }

    private static ParseResult ParsePromote(string id, string[] args)
    {
        if (args.Length == 0)
            return new ParseResult { IsSlashCommand = true, Error = "Usage: /promote <name>" };

        return new ParseResult
        {
            IsSlashCommand = true,
            Command = new ExtensionPromoteCommand { Id = id, Name = args[0] }
        };
    }

    private static ParseResult ParseThinking(string id, string[] args)
    {
        if (args.Length == 0)
            return new ParseResult { IsSlashCommand = true, Command = new ThinkingCycleCommand { Id = id } };

        ThinkingLevel? level = args[0].ToLowerInvariant() switch
        {
            "off" => ThinkingLevel.Off,
            "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            _ => null
        };

        if (level is null)
            return new ParseResult { IsSlashCommand = true, Error = "Level must be: off, low, medium, or high" };

        return new ParseResult
        {
            IsSlashCommand = true,
            Command = new ThinkingSetCommand { Id = id, Level = level.Value }
        };
    }

    private static ParseResult ParseReload(string[] args)
    {
        if (args.Length > 0)
            return new ParseResult { IsSlashCommand = true, Error = "Usage: /reload (no arguments)" };

        return new ParseResult
        {
            IsSlashCommand = true,
            ClientCommand = ClientCommandKind.Reload
        };
    }

    private static ParseResult ParseAddProvider(string[] args)
    {
        if (args.Length > 0)
            return new ParseResult { IsSlashCommand = true, Error = "Usage: /add-provider (no arguments)" };

        return new ParseResult
        {
            IsSlashCommand = true,
            ClientCommand = ClientCommandKind.AddProvider
        };
    }
}
