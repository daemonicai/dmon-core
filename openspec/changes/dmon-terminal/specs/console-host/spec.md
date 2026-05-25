## MODIFIED Requirements

### Requirement: Console host is a thin RPC client
The console host SHALL communicate with the agent core exclusively via the JSONL-over-stdio RPC protocol (ADR-003). No agent logic, session management, or tool execution SHALL be implemented in the host process. The host SHALL use Spectre.Console for output rendering and a `Console.ReadKey` async loop for input; it SHALL NOT use Spectre.Console's synchronous prompt or `AnsiConsole.Prompt` APIs.

#### Scenario: Host spawns core process on startup
- **WHEN** the user launches `dmon` from the terminal
- **THEN** the terminal host spawns the agent core process and connects via stdio

#### Scenario: Host receives and renders streaming events
- **WHEN** the core emits `messageDelta` events
- **THEN** the host renders incremental text to the terminal in real time without blocking the input loop
