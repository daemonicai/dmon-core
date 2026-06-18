using Dmon.Protocol.Enums;

namespace Dmon.Desktop;

// ---------------------------------------------------------------------------
// Tool-confirmation interaction types (6.2)
// Protocol types are mapped at the edge; these are dmon-Desktop-owned records.
// ---------------------------------------------------------------------------

/// <summary>
/// Input to the tool-confirmation interaction. Carries display data only — no protocol types.
/// </summary>
public sealed record ToolConfirmRequest(string Name, string Args, RiskLevel Risk, string ConfirmId);

/// <summary>
/// The user's choice in the tool-confirmation dialog.
/// </summary>
public enum ToolConfirmChoice
{
    AllowOnce,
    AllowProject,
    AllowGlobal,
    Deny,
    Cancelled
}

/// <summary>
/// Result from the tool-confirmation dialog.
/// </summary>
public sealed record ToolConfirmResult(ToolConfirmChoice Choice);

// ---------------------------------------------------------------------------
// UI-input interaction types (6.3)
// ---------------------------------------------------------------------------

/// <summary>
/// Input to the UI-input interaction.
/// </summary>
public sealed record UiInputRequest(string Prompt, UiInputKind Kind, IReadOnlyList<string>? Options, string EventId);

/// <summary>
/// Result from the UI-input dialog.
/// </summary>
public sealed record UiInputResult(string? Value, bool Cancelled);
