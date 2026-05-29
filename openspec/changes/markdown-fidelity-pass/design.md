## Context

`MarkdownRenderer` was rewritten during Phase 5 of the `dmon-migration` change to emit `IReadOnlyList<Dcli.Line>` directly from a Markdig AST (replacing the previous Spectre-markup `string` output). The rewrite is comprehensive and tier-A tested with 22 fixtures covering headings, emphasis, inline code, fenced + indented code, lists, links, hard line breaks, and purity/determinism.

Two narrow fidelity gaps survived that pass — flagged by the Phase 5 reviewer as architectural notes, with explicit "in-spec for Phase 5; worth recording for a follow-up" rationale:

1. The `EmphasisInline` branch applies the outer emphasis Style to inner segments via *replacement* (`new Segment(s.Text, emphasisStyle)`) rather than *compounding* (`with Format = s.Style.Format | emphasisStyle.Format`). This is visible when LLM responses use nested emphasis like `**bold *italic***` — common in technical writing.
2. The `LinkInline` branch reads `link.FirstChild` as a `LiteralInline` directly, with a URL-string fallback if `FirstChild` isn't a literal. Emphasis-wrapped link labels (`[**bold**](url)`) hit the fallback because `FirstChild` is an `EmphasisInline`, not a `LiteralInline`. The user sees the URL string instead of the styled label.

Both gaps are real-world enough to be worth fixing. Neither is a crash or correctness bug — they're rendering fidelity. The fixes are mechanical (small recursion adjustments in `RenderInlines`) and the test surface is straightforward.

## Goals / Non-Goals

**Goals:**

- `**bold *italic***` markdown renders with the inner span carrying `Format.Bold | Format.Italic` (compound flags) — not just `Format.Bold`.
- `[**bold label**](url)` markdown renders with the link text carrying the link's underline+blue Style AND the inner emphasis's `Format.Bold` — not the URL string fallback.
- No regression to existing simple-case rendering (plain emphasis, plain links, plain inline code, etc.).
- Test surface expands to cover the nested cases as explicit regression guards.

**Non-Goals:**

- Not addressing other deferred fidelity items (heading-level distinction, code-block background tint — both were explicit Phase 5 brief decisions, not unintentional gaps).
- Not extending to OSC-8 hyperlink support (terminal-emulator-specific; out of scope for the renderer's contract).
- No changes outside `MarkdownRenderer.cs` (and its test file). Specifically, no changes to `TerminalRenderer.SettleTurn` or `ConsoleEventHandler.TurnEndEvent` — they consume `IReadOnlyList<Line>` and don't care about what's inside.
- Not a place to extend Markdig coverage (tables, blockquotes, strikethrough, etc.) — that's a separate feature change.

## Decisions

### 1. Nested emphasis: OR the Format flags, don't replace

Current code in `RenderInlines`'s `case EmphasisInline emphasis:` arm produces something like:

```csharp
List<Line> innerLines = RenderInlinesToLines(emphasis);
foreach (Line l in innerLines)
    foreach (Segment s in l.Segments)
        // ↓ THIS REPLACES the inner style with the outer
        accumulator.AppendSegment(new Segment(s.Text, emphasisStyle));
```

Where `emphasisStyle` is `new Style(Format: Format.Bold)` (for `**`) or `new Style(Format: Format.Italic)` (for `*`).

Fix: OR the outer Format with the inner Format and preserve other Style fields:

```csharp
accumulator.AppendSegment(new Segment(
    s.Text,
    s.Style with { Format = s.Style.Format | emphasisStyle.Format }));
```

This compounds `Bold | Italic` for nested cases and is a no-op for simple cases (where the inner `s.Style.Format` is `Format.None`).

Rejected alternatives:
- A dedicated "compound style" helper — overkill for a single-line fix.
- Computing the style on the recursive descent (rather than at flush-time) — requires threading a `currentStyle` parameter through the inline traversal; more invasive than needed.

### 2. Link rich-label: recurse through `LinkInline` as a `ContainerInline`

Current code in the link branch likely reads `link.FirstChild` as a literal:

```csharp
case LinkInline link:
{
    string label = (link.FirstChild as LiteralInline)?.Content.ToString()
                ?? link.Url
                ?? string.Empty;
    accumulator.AppendSegment(new Segment(label, LinkStyle));
    break;
}
```

Fix: treat `LinkInline` as a `ContainerInline` and recurse through its children, applying the link's Style as a base with inner Style flags OR-ed on top (same pattern as the nested-emphasis fix):

```csharp
case LinkInline link:
{
    List<Line> innerLines = RenderInlinesToLines(link);
    foreach (Line l in innerLines)
        foreach (Segment s in l.Segments)
            accumulator.AppendSegment(new Segment(
                s.Text,
                s.Style with
                {
                    Format = s.Style.Format | LinkStyle.Format,
                    Foreground = LinkStyle.Foreground,
                }));
    break;
}
```

If `link` has no children (e.g., empty `[](url)`), the recursion yields no segments and the link contributes nothing visible. The URL is intentionally dropped — terminals can't auto-link arbitrary text, and OSC-8 hyperlink support is out of scope.

### 3. Helper consolidation

If the same "apply outer Style, OR-ing inner Format" pattern shows up in both branches (it does), extract a small private helper:

```csharp
private static void AppendWithOverlayStyle(
    LineAccumulator accumulator,
    IEnumerable<Line> innerLines,
    Style overlayStyle)
{
    foreach (Line l in innerLines)
        foreach (Segment s in l.Segments)
            accumulator.AppendSegment(new Segment(
                s.Text,
                ComposeStyle(s.Style, overlayStyle)));
}

private static Style ComposeStyle(Style inner, Style overlay) =>
    new(
        Foreground: overlay.Foreground ?? inner.Foreground,
        Background: overlay.Background ?? inner.Background,
        Format: inner.Format | overlay.Format);
```

The exact extraction is a worker call; the helper isn't load-bearing for the spec. **Recommendation:** extract if both branches end up nearly identical; inline if one is meaningfully different.

## Risks / Trade-offs

- **Risk: existing tests that asserted the old (replacing) behaviour may now fail.** *Mitigation:* read every existing `MarkdownRendererTests` case before touching code. Simple-emphasis tests should be unchanged (the OR with `Format.None` is identity). Any test asserting *replacement* of an inner Format flag is wrong-by-design and needs updating.
- **Risk: the link-recursion change drops the URL-fallback path entirely.** Markdown like `[](https://x)` (empty label) currently renders the URL as the visible text. After the change, it renders nothing. *Mitigation:* check the existing `Render_Link_*` tests; if the empty-label fallback is exercised, decide whether to preserve it (special-case empty children → render URL) or accept the loss. **Recommendation:** preserve the fallback — empty-label links are rare but harmless. The fix is a two-line check: `if (innerLines is empty) renderUrlAsFallback`.
- **Risk: Markdig's `LinkInline` may carry a `Title` attribute** (the third quoted argument in `[label](url "title")` syntax). Today's renderer ignores it. *Mitigation:* keep ignoring it — out of scope.
- **Risk: triple-emphasis (`***triple-bold-italic***`) — Markdig parses this as nested `EmphasisInline` with `DelimiterCount == 2` containing one with `DelimiterCount == 1`.** The fix should handle this correctly because each level's emphasisStyle is OR-ed into the inner Format. *Mitigation:* add a tier-A test case covering `***triple***` to lock down the behaviour.

## Migration Plan

Single-section change. Two tasks groups (one per fix) plus a final gates-and-archive group. Branch `change/markdown-fidelity-pass` off `main`. No version coupling, no dcli dependencies beyond what `dmon-migration` already established.

Rollback: each fix is independently revertable; if one fix breaks a corner case the other doesn't, revert just the affected branch in `MarkdownRenderer.RenderInlines`.

## Open Questions

1. Should we preserve the URL-string fallback for empty-label links (`[](url)` → render URL) or accept the loss? *Tentative answer:* preserve. Cheap to add, surprises users less.
2. Should the link's `Title` attribute (`[label](url "title")`) ever surface? *Tentative answer:* no — terminal can't tooltip; out of scope.
3. The proposal scopes the fix to `MarkdownRenderer.cs` only. If the `ComposeStyle` helper turns out useful elsewhere (e.g., other Style-merging in the terminal layer), should it move to a shared utility? *Tentative answer:* keep it private in `MarkdownRenderer.cs` for now; promote only if a second caller appears.
