## Why

Two AST-recursion fidelity gaps in `MarkdownRenderer` were flagged as architectural notes during the Phase 5 reviewer audit of the `dmon-migration` change, and explicitly deferred ("opportunistic follow-ups") so the migration could ship. Both produce visibly-degraded LLM-output rendering for markdown that real-world responses occasionally include. Neither is user-blocking — they're fidelity gaps, not crashes — but they're worth closing now that the renderer is in active use.

The two items, with their origins in the archived `2026-05-28-dmon-migration/DEVLOG.md` (Phase 5 reviewer architectural notes 1 and 2):

1. **Nested-emphasis style compounding.** Markdown like `**bold *and italic***` should render as bold text where the inner span is *both* bold AND italic. The current `MarkdownRenderer` walks `EmphasisInline.RenderInlines` and applies the outer emphasis style to *every* segment of the inner-rendered lines — replacing any inner emphasis flag instead of OR-ing it. So the inner italic span loses its italic and renders as plain bold.
2. **`LinkInline` rich-label fidelity.** Markdown like `[**bold link**](https://example.com)` should render the visible link text as bold + underlined + blue (or however the renderer styles links). The current renderer grabs `link.FirstChild` as a `LiteralInline` only, falling back to the URL string if the first child isn't a literal. Emphasis-wrapped link labels (where `FirstChild` is an `EmphasisInline`, not a literal) skip the inline-recursion path entirely and the user sees the raw URL instead of the styled label.

Both fixes are narrow Markdig AST recursion adjustments to the same file (`src/Dmon.Terminal/MarkdownRenderer.cs`), with the same test pattern (markdown fixtures in / styled `Line[]` out / per-segment Style assertion). They share an OpenSpec change because the work is tightly coupled and the review surface is identical.

## What Changes

- **`src/Dmon.Terminal/MarkdownRenderer.cs` — Nested-emphasis fix.** In the `case EmphasisInline emphasis:` branch of `RenderInlines`, change the inner-style application so the outer emphasis's `Format` flag is OR-ed with the inner segment's existing `Style.Format` rather than replacing it. Concretely: `new Segment(s.Text, s.Style with { Format = s.Style.Format | emphasisStyle.Format })` instead of `new Segment(s.Text, emphasisStyle)`.
- **`src/Dmon.Terminal/MarkdownRenderer.cs` — `LinkInline` rich-label recursion.** Change the link-handling branch to treat `LinkInline` as a `ContainerInline` and recurse through its children using the existing `RenderInlines` accumulator. The link's underline+blue style is applied as a base, with inner-child Style flags OR-ed on top (mirroring the nested-emphasis pattern). The `Url` is dropped from the visible output (matching today's behaviour); only the styled label is rendered.
- **Tests** — add `MarkdownRendererTests` cases:
  - `Render_NestedEmphasis_BoldPlusItalic_Compounds` — `Render("**bold *and italic***")` produces a Line where the inner span's Segment has `Format.Bold | Format.Italic`.
  - `Render_NestedEmphasis_ItalicPlusBold_Compounds` — the same but with reversed nesting (`*italic **and bold***`), confirming order-independence.
  - `Render_Link_BoldLabel_StylePreserved` — `Render("[**bold link**](https://x)")` produces a Line where the link segment's Style has both link's flags (Underline + blue Foreground) AND the inner emphasis's `Format.Bold`.
  - `Render_Link_PlainLabel_UnchangedFromBaseline` — regression guard ensuring the existing `Render("[home](https://x)")` still renders underline+blue with no compounding side effects.
- **No behaviour change beyond the fidelity improvements above.** Existing `MarkdownRendererTests` covering simple emphasis, simple links, and the other markdown elements all still pass without modification.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `terminal-host` — adds two scenarios under "Settled markdown render on turn end" covering nested-emphasis compounding and rich-label link styling.
