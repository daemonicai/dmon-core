## Purpose

Define how dmon resolves, stores, and manages provider API credentials — including environment variable precedence, the user-global credentials file format, the `/login` and `/logout` commands, and optional API key support for locally-hosted providers.
## Requirements
### Requirement: API key credential resolution
The system SHALL resolve provider credentials in the following order: environment variable → credentials file (`~/.daemon/credentials/<provider>.json`) → interactive prompt. Credentials SHALL always be stored in user-global scope; never in the project-local `.daemon/` directory.

#### Scenario: Environment variable takes precedence
- **WHEN** `ANTHROPIC_API_KEY` is set in the environment and a credentials file also exists
- **THEN** the environment variable value is used

#### Scenario: Credentials file used when no env var
- **WHEN** no environment variable is set but `~/.daemon/credentials/anthropic.json` exists
- **THEN** the key from the credentials file is used

#### Scenario: Interactive prompt when no credential found
- **WHEN** neither an environment variable nor a credentials file provides a key for the active provider
- **THEN** the core emits `ui.inputRequest {id, kind: "secret", prompt}` asking for the API key before the first turn, and waits for `ui.inputResponse {id, value}`

### Requirement: Credentials file format
Credentials files SHALL be JSON at `~/.daemon/credentials/<provider>.json`. The credentials directory SHALL be created with mode `0700` on POSIX and equivalent user-restricted ACL on Windows; each file SHALL be created with mode `0600` on POSIX.

Each file SHALL contain at minimum: `provider`, `type` (`apiKey` for V1), `apiKey`, `headerStyle` (`x-api-key` or `bearer`), `createdAt`, `updatedAt`. Readers SHALL ignore unknown fields.

#### Scenario: New credentials file written with restrictive permissions
- **WHEN** `/login` writes a credentials file on POSIX
- **THEN** the file has mode `0600` and the parent `credentials/` directory has mode `0700`

#### Scenario: Unknown fields tolerated
- **WHEN** a credentials file contains fields beyond the documented schema
- **THEN** the loader reads the known fields and ignores the rest without error

### Requirement: /login command
The system SHALL provide a `/login <provider>` command that initiates the credential setup flow for the named provider.

#### Scenario: Login stores API key
- **WHEN** the user runs `/login anthropic` and enters a valid API key
- **THEN** the key is stored in `~/.daemon/credentials/anthropic.json` and used for subsequent calls

#### Scenario: Login for unknown provider fails gracefully
- **WHEN** the user runs `/login unknownprovider`
- **THEN** the system responds with a list of configured providers and does not attempt a flow

### Requirement: /logout command
The system SHALL provide a `/logout <provider>` command that removes stored credentials for the named provider.

#### Scenario: Logout removes credentials file
- **WHEN** the user runs `/logout anthropic`
- **THEN** `~/.daemon/credentials/anthropic.json` is deleted and the core emits `auth.logoutComplete`

#### Scenario: Logout when not logged in is a no-op
- **WHEN** the user runs `/logout anthropic` and no credentials file exists
- **THEN** the system responds without error

### Requirement: auth.status query
The system SHALL respond to `auth.status` with the authentication state of all configured providers, as a typed `auth.statusResult` event derived from the `ResultEvent` correlation base and carrying the originating command's `id` (serialized as `id`). The response SHALL NOT use the generic `{type:"response", data}` envelope.

#### Scenario: Status shows which providers have credentials
- **WHEN** the host sends `auth.status` with `id` `"req-1"`
- **THEN** the core emits an `auth.statusResult` event with `id` = `"req-1"` whose `providers` field lists all configured providers, each with `authenticated: true/false`

### Requirement: Local providers support optional API key
Local providers (Ollama, llama.cpp, LM Studio) SHALL support an optional `apiKey` field in their config entry, used when the provider is exposed over a network rather than localhost.

#### Scenario: Local provider with API key authenticates correctly
- **WHEN** a networked local provider (e.g. llama.cpp) is configured with an `apiKey`
- **THEN** the `IChatClient` sends the key in the appropriate header on every request

#### Scenario: Local provider without API key connects unauthenticated
- **WHEN** an Ollama provider is configured with no `apiKey`
- **THEN** the `IChatClient` makes requests without an authentication header

