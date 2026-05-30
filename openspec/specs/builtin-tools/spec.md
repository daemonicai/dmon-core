## Purpose

Define the six built-in tools (`read_file`, `write_file`, `edit_file`, `glob`, `fetch`, `bash`) that are registered into `IToolRegistry` at startup, their argument contracts, error handling behaviour, and the architectural constraint that `Daemon.BuiltinTools` carries no dependency on `Daemon.Core`.

## Requirements

### Requirement: Built-in tool suite available at startup
The system SHALL provide six built-in tools — `read_file`, `write_file`, `edit_file`, `glob`, `fetch`, and `bash` — registered into `IToolRegistry` before the first turn. Each tool SHALL be implemented as an `IDaemonExtension` in the `Daemon.BuiltinTools` project.

#### Scenario: Tools available on first turn
- **WHEN** a session starts with no user extensions loaded
- **THEN** `IToolRegistry.GetAll()` returns at least six `AIFunction` entries with names `read_file`, `write_file`, `edit_file`, `glob`, `fetch`, and `bash`

#### Scenario: Tool names use snake_case
- **WHEN** the LLM receives the tool list in `ChatOptions.Tools`
- **THEN** each built-in `AIFunction.Name` matches the snake_case convention in the design (`read_file`, `write_file`, `edit_file`, `glob`, `fetch`, `bash`)

### Requirement: ReadFileTool reads a file by path
`read_file` SHALL accept a `path` argument and return the full text content of the file at that path relative to the current working directory. If the file does not exist or cannot be read, the tool SHALL return an error string rather than throwing.

#### Scenario: File exists
- **WHEN** the LLM calls `read_file` with `path` pointing to an existing readable file
- **THEN** the tool returns the file's text content as a string

#### Scenario: File not found
- **WHEN** the LLM calls `read_file` with `path` pointing to a non-existent file
- **THEN** the tool returns an error string beginning with `"Error:"` and does not throw an exception

### Requirement: WriteFileTool writes content to a file by path
`write_file` SHALL accept `path` and `content` arguments and write the content to the specified path, creating parent directories as needed. Existing files are overwritten. On failure the tool returns an error string.

#### Scenario: Successful write
- **WHEN** the LLM calls `write_file` with a valid `path` and `content`
- **THEN** the file is created or overwritten with the supplied content and the tool returns a short confirmation string

#### Scenario: Write failure (e.g. permission denied)
- **WHEN** the LLM calls `write_file` to a path where the process lacks write permission
- **THEN** the tool returns an error string beginning with `"Error:"` and does not throw

### Requirement: EditFileTool applies a string replacement to a file
`edit_file` SHALL accept `path`, `old_string`, and `new_string` arguments. It replaces the first occurrence of `old_string` in the file with `new_string`. If `old_string` is not found, the tool SHALL return an error string. If the file does not exist, the tool returns an error string.

#### Scenario: Successful edit
- **WHEN** the LLM calls `edit_file` and `old_string` appears exactly once in the file
- **THEN** the first occurrence is replaced with `new_string` and the tool returns a confirmation string

#### Scenario: String not found
- **WHEN** the LLM calls `edit_file` and `old_string` does not appear in the file
- **THEN** the tool returns an error string beginning with `"Error:"` without modifying the file

### Requirement: GlobTool returns paths matching a glob pattern
`glob` SHALL accept a `pattern` argument and return a newline-separated list of matching file paths relative to the current working directory. An empty result is valid and returns an empty string.

#### Scenario: Pattern matches files
- **WHEN** the LLM calls `glob` with a pattern like `"**/*.cs"`
- **THEN** the tool returns a newline-separated list of matching relative paths

#### Scenario: Pattern matches nothing
- **WHEN** the LLM calls `glob` with a pattern that matches no files
- **THEN** the tool returns an empty string

### Requirement: FetchTool retrieves the body of an HTTP URL
`fetch` SHALL accept a `url` argument and return the response body as a string. HTTP errors (4xx, 5xx) SHALL be reported as error strings. The tool SHALL use the default `HttpClient` with no custom authentication headers.

#### Scenario: Successful GET
- **WHEN** the LLM calls `fetch` with a reachable URL that returns 200
- **THEN** the tool returns the response body as a string

#### Scenario: HTTP error response
- **WHEN** the LLM calls `fetch` and the server responds with a 4xx or 5xx status
- **THEN** the tool returns an error string that includes the status code

#### Scenario: Network failure
- **WHEN** the LLM calls `fetch` and the host is unreachable
- **THEN** the tool returns an error string beginning with `"Error:"` and does not throw

### Requirement: BashTool executes a shell command and returns combined output
`bash` SHALL accept a `command` argument. On POSIX it executes via `/bin/sh -c <command>`; on Windows via `cmd.exe /c <command>`. Both stdout and stderr are captured and returned as a single string. The process is killed and an error string is returned if the command exceeds the configured timeout (default 30 s, configurable via `Daemon:Tools:Bash:TimeoutSeconds`).

#### Scenario: Command succeeds
- **WHEN** the LLM calls `bash` with a command that exits 0
- **THEN** the combined stdout+stderr output is returned as a string

#### Scenario: Command fails (non-zero exit)
- **WHEN** the LLM calls `bash` with a command that exits non-zero
- **THEN** the combined output is returned prefixed with `"Exit <code>: "`

#### Scenario: Command times out
- **WHEN** the LLM calls `bash` with a command that runs longer than the configured timeout
- **THEN** the process is killed and the tool returns an error string beginning with `"Error: timed out"`

#### Scenario: Denylist command blocked
- **WHEN** the LLM attempts to call `bash` with a command matching the hardcoded denylist (e.g. `rm -rf /`)
- **THEN** `BashTool.Evaluate` returns `PermissionResult.Deny` and the command is never executed

### Requirement: Daemon.BuiltinTools has no dependency on Daemon.Core
The `Daemon.BuiltinTools` project SHALL reference only `Microsoft.Extensions.AI` and `Daemon.Protocol`. It SHALL NOT reference `Daemon.Core` or any project that references `Daemon.Core`.

#### Scenario: Project graph is acyclic
- **WHEN** the solution dependency graph is inspected
- **THEN** there is no path from `Daemon.BuiltinTools` back to `Daemon.Core`
