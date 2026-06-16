namespace Dmon.Core.Config;

/// <summary>
/// Result of config file resolution. <see cref="ClaudeMdUsed"/> is true when
/// <c>{CWD}/CLAUDE.md</c> was used as a compatibility fallback instead of
/// <c>{CWD}/AGENTS.md</c>. The caller is responsible for emitting the
/// <c>system.notice</c> event when this flag is set.
/// </summary>
public sealed record AgentConfigResult
{
    /// <summary>
    /// Combined config text, or <see langword="null"/> if no config files were found.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// True when <c>{CWD}/CLAUDE.md</c> was consumed because no <c>{CWD}/AGENTS.md</c>
    /// was present.
    /// </summary>
    public bool ClaudeMdUsed { get; init; }

    public static AgentConfigResult Empty { get; } = new();
}
