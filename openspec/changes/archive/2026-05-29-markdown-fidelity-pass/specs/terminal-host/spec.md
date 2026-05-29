## MODIFIED Requirements

### Requirement: Settled markdown render on turn end

The terminal host SHALL re-render the completed turn with full Markdig markdown styling when `TurnEndEvent` is received, by producing a settled `IReadOnlyList<Line>` from the markdown source via a pure markdown-to-`Line[]` transform and appending those lines via `ITerminal.Scrollback.Append`. The transform SHALL compound inline styles for nested emphasis and SHALL recurse through link-label children rather than reading only the first literal child.

#### Scenario: Fenced code block rendered with border

- **WHEN** a completed turn contains a fenced code block
- **THEN** the settled render presents the code block with the conventional styled border (background tint or border characters), produced as a sequence of `Line`s from the markdown transform

#### Scenario: Settled lines replace the live block

- **WHEN** the live streamed block has been committed and the settled markdown render begins
- **THEN** the settled lines are appended to the scrollback in order, succeeding the streamed (now-committed) tokens

#### Scenario: Markdown transform is a pure function

- **WHEN** a unit test invokes the markdown renderer with a markdown source string
- **THEN** the renderer returns an `IReadOnlyList<Line>` deterministically, with no I/O, no `ITerminal` reference, and no global console state

#### Scenario: Nested emphasis compounds Format flags

- **WHEN** the markdown source contains nested emphasis (e.g. `**bold *and italic***`)
- **THEN** segments inside the inner emphasis SHALL render with BOTH the outer and inner Format flags set (`Format.Bold | Format.Italic`), produced by OR-ing the outer emphasis's Format with each inner segment's existing Style.Format rather than replacing the inner Style

#### Scenario: Link with rich-text label preserves label styling

- **WHEN** the markdown source contains a link whose label uses inline styling (e.g. `[**bold label**](https://example.com)`)
- **THEN** the rendered link segments SHALL carry BOTH the link's Style (underline + blue Foreground) AND the label's inner Format flags (e.g. `Format.Bold | Format.Underline`), produced by recursing through the `LinkInline` as a container of inlines rather than reading only `LinkInline.FirstChild` as a literal

#### Scenario: Link with empty label falls back to URL text

- **WHEN** the markdown source contains a link with no visible label (e.g. `[](https://example.com)`)
- **THEN** the rendered link segment SHALL display the URL string as the visible text with the link Style applied, preserving the prior renderer's empty-label fallback
