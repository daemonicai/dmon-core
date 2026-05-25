using Microsoft.Extensions.AI;

namespace Dmon.Tui;

/// <summary>
/// Represents one turn in the conversation — either a user message or an assistant response.
/// Mutable during streaming; <see cref="Rendered"/> is set to true after settled rendering.
/// </summary>
internal sealed class TurnBlock
{
    public TurnBlock(ChatRole role, string rawText = "")
    {
        Role = role;
        RawText = rawText;
    }

    public ChatRole Role { get; }

    public string RawText { get; private set; }

    /// <summary>
    /// True once the turn has been through settled (Markdig) rendering.
    /// Set by <c>ChatOutputView.SettleTurn</c> in Group 3.
    /// </summary>
    public bool Rendered { get; set; }

    /// <summary>
    /// Character ranges within <see cref="RawText"/> that are complete inline code spans.
    /// Each entry is (start, length) — 0-based, inclusive start.
    /// Populated incrementally by <c>ChatOutputView.AppendToken</c>.
    /// </summary>
    public List<(int Start, int Length)> CodeSpanRanges { get; } = [];

    /// <summary>
    /// Number of characters in <see cref="RawText"/> already scanned for code spans.
    /// Tracks the scan position so re-scanning already-processed text is avoided.
    /// </summary>
    public int ScannedUpTo { get; set; }

    public void AppendText(string token) => RawText += token;
}
