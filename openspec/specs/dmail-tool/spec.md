## Purpose

Define the `Dmon.Tools.Dmail` tool extension: the behavioural contract of the Dmail tool extension that exposes the Dmail HTTP API to the agent. It implements `IToolExtension` (`Dmon.Abstractions.Extensions`) and surfaces exactly three agent tools — `search_email`, `check_new_messages`, and `get_email` — over the Dmail HTTP API; it is configured from environment variables (`DMAIL_BASE_URL`/`DMAIL_API_KEY`); it applies a permission policy that allows the metadata-only tools without prompting and prompts for `get_email` (which returns full private message bodies); and it degrades gracefully to a friendly message when Dmail is unreachable.

## Requirements

### Requirement: Dmail tools exposed to the agent

The Dmail tool extension SHALL implement `IToolExtension` (from `Dmon.Abstractions.Extensions`) and expose exactly three AI-callable tools — `search_email`, `check_new_messages`, and `get_email` — each with a non-empty model-facing description. The extension SHALL report a human-readable `Name` and `Description`.

#### Scenario: The three tools are present

- **WHEN** the extension's `Tools` are enumerated
- **THEN** exactly the tools `search_email`, `check_new_messages`, and `get_email` are returned

#### Scenario: Every tool is described for the model

- **WHEN** the extension's `Tools` are enumerated
- **THEN** every tool has a non-empty `Description`

### Requirement: Endpoint configuration

The extension SHALL be constructable from environment configuration — `DMAIL_BASE_URL` (default `http://localhost:8080`) and `DMAIL_API_KEY` (sent as the `X-Api-Key` header; absent when Dmail runs without auth) — via a parameterless constructor, so it can be registered with `builder.AddToolExtension<DmailExtension>()`. The extension SHALL also be constructable against an explicit base URL and API key.

#### Scenario: Parameterless construction reads the environment

- **WHEN** the extension is constructed with no arguments
- **THEN** it targets the `DMAIL_BASE_URL` endpoint, falling back to `http://localhost:8080` when the variable is unset

### Requirement: Permission policy

The extension SHALL allow the metadata-only tools (`search_email`, `check_new_messages`) without prompting, and SHALL prompt for `get_email` because it returns the complete private body of a message.

#### Scenario: Metadata tools are allowed

- **WHEN** `Evaluate` is called for `search_email` or `check_new_messages`
- **THEN** it returns `PermissionResult.Allow`

#### Scenario: Full-message retrieval prompts

- **WHEN** `Evaluate` is called for `get_email`
- **THEN** it returns `PermissionResult.Prompt`

### Requirement: Graceful degradation when Dmail is unreachable

When the Dmail HTTP API cannot be reached or returns an error, each tool SHALL return a human-readable message rather than throwing, so the agent turn continues.

#### Scenario: Search against an unreachable endpoint

- **WHEN** `search_email` is invoked while the Dmail endpoint refuses connections
- **THEN** the tool returns a message containing "Could not search email" instead of throwing
