## ADDED Requirements

### Requirement: Core diagnostic stderr is surfaced

The console host SHALL surface the core process's standard-error stream rather than discarding it. Core logs are structured diagnostic lines on stderr; the host SHALL forward each line to a host-side diagnostic sink (the scrollback diagnostics surface and/or the host's own stderr) so that core launch failures, provider errors, and tool-call faults are visible to the user and to automated tests, instead of being swallowed. Forwarding SHALL NOT interleave into the conversational scrollback as model output, and SHALL NOT block the RPC event loop.

#### Scenario: Core stderr line is forwarded
- **WHEN** the core writes a diagnostic line to its standard error
- **THEN** the host emits that line to its diagnostic sink rather than discarding it

#### Scenario: Core failure on startup is visible
- **WHEN** the core fails during startup and writes the cause to stderr
- **THEN** the failure text is surfaced through the host's diagnostic sink rather than being silently swallowed
