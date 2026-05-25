## ADDED Requirements

### Requirement: Turn output is modelled as a list of blocks
The host SHALL model conversation output as an ordered list of `TurnBlock` records, each carrying the speaker role, accumulated raw text, and a rendered flag. The `ChatOutputView` SHALL redraw from this list on any mutation.

#### Scenario: User turn appended
- **WHEN** the user submits a message
- **THEN** a new `TurnBlock` with `Role = User` and the message text is appended to the block list and displayed immediately

#### Scenario: Assistant turn begins
- **WHEN** the host receives a `TurnStartEvent`
- **THEN** a new `TurnBlock` with `Role = Assistant` and empty text is appended to the block list

### Requirement: Streaming tokens are appended in real time
The host SHALL append each `MessageDeltaEvent` token to the current assistant `TurnBlock.RawText` and update the display immediately, so the user sees output as it arrives.

#### Scenario: Token appended during streaming
- **WHEN** the host receives a `MessageDeltaEvent` while a turn is active
- **THEN** the token is appended to the current block's raw text and the view is redrawn within one render frame

#### Scenario: Inline code span coloured during streaming
- **WHEN** a completed backtick-delimited inline code span is detected in the tail of the accumulated buffer
- **THEN** the span is rendered with monospace colour attribute without waiting for turn end

### Requirement: Settled rendering applied on turn completion
The host SHALL re-render the completed assistant turn using Markdig on `TurnEndEvent`, replacing the raw streamed text with fully styled output.

#### Scenario: Code block rendered on settle
- **WHEN** a `TurnEndEvent` is received and the completed turn buffer contains a fenced code block
- **THEN** the code block is rendered with a distinct border and monospace colour attribute

#### Scenario: Bullet list rendered on settle
- **WHEN** a `TurnEndEvent` is received and the completed turn buffer contains a Markdown list
- **THEN** each list item is rendered with an indent and bullet character

#### Scenario: Bold and italic rendered on settle
- **WHEN** a `TurnEndEvent` is received and the completed turn buffer contains bold or italic emphasis
- **THEN** the emphasised text is rendered with the appropriate Terminal.Gui colour attribute

#### Scenario: Plain prose unchanged on settle
- **WHEN** a `TurnEndEvent` is received and the completed turn buffer contains only plain prose
- **THEN** the displayed text is visually identical before and after settle

### Requirement: Output view scrolls to show new content
The `ChatOutputView` SHALL scroll to the bottom whenever new content is appended during streaming.

#### Scenario: View scrolled on token append
- **WHEN** a token is appended to the current block
- **THEN** the view scrolls so the new content is visible
