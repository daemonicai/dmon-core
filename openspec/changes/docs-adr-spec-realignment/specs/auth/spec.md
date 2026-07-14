## MODIFIED Requirements

### Requirement: Local providers support optional API key
Local providers (Ollama, llama.cpp, LM Studio) SHALL support an optional `apiKey` field in their config entry, used when the provider is exposed over a network rather than localhost.

#### Scenario: Local provider with API key authenticates correctly
- **WHEN** a networked local provider (e.g. llama.cpp) is configured with an `apiKey`
- **THEN** the `IChatClient` sends the key in the appropriate header on every request

#### Scenario: Local provider without API key connects unauthenticated
- **WHEN** an Ollama provider is configured with no `apiKey`
- **THEN** the `IChatClient` makes requests without an authentication header
