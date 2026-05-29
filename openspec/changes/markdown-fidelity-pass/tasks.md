## 1. Nested-emphasis style compounding

- [x] 1.1 In `src/Dmon.Terminal/MarkdownRenderer.cs`, change the `case EmphasisInline emphasis:` branch of `RenderInlines` so that the outer emphasis's `Format` is OR-ed with each inner segment's existing `Style.Format` instead of replacing the entire `Style`. Concretely: `s.Style with { Format = s.Style.Format | emphasisStyle.Format }`.
- [x] 1.2 Tier-A tests in `test/Dmon.Terminal.Tests/MarkdownRendererTests.cs`:
  - `Render_NestedEmphasis_BoldPlusItalic_Compounds` — `Render("**bold *and italic***")` produces Segments where the inner span has `Format.Bold | Format.Italic`, while the outer-only spans have just `Format.Bold`.
  - `Render_NestedEmphasis_ItalicPlusBold_Compounds` — `Render("*italic **and bold***")` produces compounding in the reverse order.
  - `Render_TripleEmphasis_BoldAndItalic` — `Render("***triple***")` (Markdig parses as nested emphasis) produces a single Segment with `Format.Bold | Format.Italic`.
  - Verify existing `Render_BoldEmphasis_*` and `Render_ItalicEmphasis_*` tests still pass without modification.
- [x] 1.3 Standard gates: `dotnet build`, `dotnet test`, `openspec validate markdown-fidelity-pass --strict`; reviewer audit; commit.

## 2. `LinkInline` rich-label recursion

- [ ] 2.1 In `src/Dmon.Terminal/MarkdownRenderer.cs`, change the `case LinkInline link:` branch to treat the link as a `ContainerInline` and recurse through its children via the existing inline-rendering accumulator. Apply the link's Style (underline + blue Foreground) as a base, OR-ing the inner Style's `Format` flags on top and preserving non-null `Foreground`/`Background` from the overlay.
- [ ] 2.2 Handle the empty-label edge case (`[](url)` — `link.FirstChild` is null or the recursive descent yields no segments): preserve the existing URL-string fallback. Render the URL as the visible text with the link Style applied.
- [ ] 2.3 If both this section and §1 produced near-identical "apply overlay Style, OR-ing Format" loops, extract a private `ComposeStyle(Style inner, Style overlay)` helper (and optionally an `AppendWithOverlayStyle` helper) within `MarkdownRenderer.cs`. Skip the helper if the two branches end up shape-different enough that abstraction adds noise.
- [ ] 2.4 Tier-A tests in `MarkdownRendererTests.cs`:
  - `Render_Link_BoldLabel_StylePreserved` — `Render("[**bold link**](https://x)")` produces a Segment whose Style has `Format.Bold | Format.Underline` AND `Foreground == Color.Named(Color.AnsiColor.Blue)`.
  - `Render_Link_PlainLabel_UnchangedFromBaseline` — regression guard: `Render("[home](https://x)")` still renders underline+blue (no behavioural drift from §1's change).
  - `Render_Link_EmptyLabel_RendersUrl` — `Render("[](https://x)")` renders `https://x` as the visible text with link Style.
  - `Render_Link_MultiInlineLabel_RecursesAll` — `Render("[a **b** c](https://x)")` produces three Segments: `"a "` underline+blue, `"b"` bold+underline+blue, `" c"` underline+blue.
- [ ] 2.5 Standard gates + reviewer audit + commit.

## 3. Final gates + archive

- [ ] 3.1 Re-run the full `MarkdownRendererTests` suite and confirm all 22+ tests pass (the new ones from §1 and §2 plus the existing baseline).
- [ ] 3.2 Manual smoke if available (gated by the `Dmon.Core` MCP/M.E.AI crash unblock + API keys configured): trigger an LLM response that uses nested emphasis and a richly-labeled link; visually confirm the rendering matches the assertions. If not yet available, the tier-A tests are the verification.
- [ ] 3.3 Standard gates: build, test, `openspec validate markdown-fidelity-pass --strict`.
- [ ] 3.4 Propose `/opsx:archive markdown-fidelity-pass` and wait for user confirmation. Do not archive automatically.
