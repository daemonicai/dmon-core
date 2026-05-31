using Dmon.Abstractions.Profiles;

namespace Dmon.Core.Profiles;

/// <summary>
/// Built-in agent profiles that exist independent of any config file.
/// </summary>
/// <remarks>
/// The <see cref="CodingPersona"/> constant is the single source of truth for the
/// built-in coding persona. <see cref="SystemPromptBuilder"/> currently holds an
/// identical copy (Group 4 will remove it and point at this constant).
/// </remarks>
internal static class BuiltInProfiles
{
    internal const string CodingProfileName = "coding";

    // Byte-for-byte copy of SystemPromptBuilder.StaticCore — do not retype.
    // Group 4 will delete the copy in SystemPromptBuilder and reference this constant.
    internal static readonly string CodingPersona = """
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

    internal static readonly AgentProfile Coding = new(
        Name: CodingProfileName,
        Persona: CodingPersona,
        Assets: false,
        PermissionMode: PermissionMode.Coding);
}
