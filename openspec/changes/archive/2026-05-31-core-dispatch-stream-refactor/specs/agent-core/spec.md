## ADDED Requirements

### Requirement: Malformed and unrecognized commands are rejected without killing the reader
The agent core's command dispatch SHALL treat malformed input, structurally invalid commands, and unrecognized command types as recoverable, per-line failures: for each such line it SHALL emit an `error` event and continue reading subsequent lines. A single bad command line SHALL NOT terminate the dispatch loop, drop the line silently, or prevent later valid commands from being processed. The dispatch loop SHALL remain a single sequential reader. Specifically:

- A line that is not parseable JSON SHALL produce `error {code: "malformedCommand", recoverable: true}`.
- A JSON object with no `type` field SHALL produce `error {code: "missingType", recoverable: true}`.
- A JSON object whose `type` does not correspond to a known command, or whose payload cannot be bound to that command type, SHALL produce `error {code: "unknownCommand", recoverable: true}`.
- A command whose handler raises a not-implemented condition SHALL produce `error {code: "notImplemented", recoverable: true}`.
- A command whose handler raises any other unexpected error SHALL produce `error {code: "internalError", recoverable: false}`.

Command routing SHALL be driven by the command type's polymorphic discriminator (the single `Command` type-discriminator table), not by a separately maintained mapping.

#### Scenario: Malformed JSON line does not stop the loop
- **WHEN** the host sends a line that is not valid JSON, then sends a valid command on the next line
- **THEN** the core emits `error {code: "malformedCommand", recoverable: true}` for the bad line and still processes the following valid command

#### Scenario: Command missing the type field
- **WHEN** the host sends a JSON object that has no `type` field
- **THEN** the core emits `error {code: "missingType", recoverable: true}` and continues reading

#### Scenario: Unknown command type
- **WHEN** the host sends a JSON object whose `type` is not a recognized command
- **THEN** the core emits `error {code: "unknownCommand", recoverable: true}` and continues reading

#### Scenario: Handler failure is surfaced as a non-recoverable error
- **WHEN** a recognized command's handler raises an unexpected exception
- **THEN** the core emits `error {code: "internalError", recoverable: false}` rather than terminating the reader, and the reader continues processing subsequent commands
