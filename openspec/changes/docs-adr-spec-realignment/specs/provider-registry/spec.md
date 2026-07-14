## MODIFIED Requirements

### Requirement: Supported provider adapters
The system SHALL support the following adapter types via `IProviderFactory` implementations in `Dmon.Providers`:

- `openai` — `Microsoft.Extensions.AI.OpenAI`; supports custom `baseUrl` for Ollama, llama.cpp, and other OpenAI-compatible local runtimes
- `anthropic` — `Anthropic.SDK` community package
- `gemini` — `GeminiDotnet.Extensions.AI`

#### Scenario: OpenAI-compatible local provider
- **WHEN** a provider is configured with adapter `openai` and a `baseUrl` of `http://localhost:11434/v1`
- **THEN** the registry creates an `IChatClient` pointing at that endpoint

#### Scenario: Anthropic-compatible local endpoint via baseUrl
- **WHEN** a provider is configured with adapter `anthropic` and a `baseUrl` pointing to a local Anthropic-compatible endpoint
- **THEN** the registry creates an Anthropic `IChatClient` targeting that endpoint
