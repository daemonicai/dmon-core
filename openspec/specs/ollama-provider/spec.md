# Ollama Provider Spec

## Purpose

Define the `OllamaProviderExtension` and `OllamaProviderFactory` components that integrate an Ollama inference server into dmon as a first-class local provider, covering platform applicability, server reachability, wizard-driven configuration, capability heuristics derived from model IDs, and project isolation constraints.

## Requirements

### Requirement: OllamaProviderExtension implements IProviderExtension
The system SHALL provide `OllamaProviderExtension` in project `Dmon.Providers.Ollama`, implementing `IProviderExtension` with: `ProviderName = "Ollama"`, `IsApplicable()` returning `true` on all platforms, `IsRunningAsync` pinging the configured base URL via OllamaSharp within a 2-second timeout, `EnsureRunningAsync` throwing `NotSupportedException`, `ListModelsAsync` delegating to `OllamaApiClient`, and `CreateFactory` returning an `OllamaProviderFactory`.

#### Scenario: IsApplicable always true
- **WHEN** `IsApplicable()` is called on `OllamaProviderExtension`
- **THEN** it returns `true` regardless of platform or hardware

#### Scenario: IsRunningAsync returns true when Ollama is reachable
- **WHEN** `IsRunningAsync` is called and an Ollama server is reachable at the configured base URL
- **THEN** it returns `true` within 2 seconds

#### Scenario: IsRunningAsync returns false when Ollama is not reachable
- **WHEN** `IsRunningAsync` is called and no server is reachable at the configured base URL
- **THEN** it returns `false` without throwing

#### Scenario: EnsureRunningAsync throws NotSupportedException
- **WHEN** `EnsureRunningAsync` is called
- **THEN** it throws `NotSupportedException` with a message indicating the user must start Ollama manually

#### Scenario: ListModelsAsync returns models from running server
- **WHEN** `ListModelsAsync` is called and an Ollama server is running
- **THEN** it returns a non-empty list of `ModelInfo` with IDs matching those in the server's model library

### Requirement: OllamaProviderFactory implements IProviderFactory
The system SHALL provide `OllamaProviderFactory` in `Dmon.Providers.Ollama`, implementing `IProviderFactory` with: `AdapterName = "ollama"`, `DisplayName = "Ollama"`, `DefaultModelId = "llama3.2"`, `DefaultEnvVar = "OLLAMA_HOST"`. `CreateAsync` SHALL construct an `OllamaApiClient` using the base URL from `ProviderConfig.BaseUrl` (falling back to `http://localhost:11434`) and wrap it in `CapabilitiesDecorator`. `GetAvailableModelsAsync` SHALL query the Ollama server at the given base URL and return the model list; if unreachable it SHALL return an empty list without throwing.

#### Scenario: CreateAsync returns a working IChatClient
- **WHEN** `CreateAsync` is called with a `ProviderConfig` containing a valid `BaseUrl` and `DefaultModelId`
- **THEN** it returns an `IChatClient` that is non-null and exposes `ChatClientCapabilities` via `GetService`

#### Scenario: CreateAsync falls back to localhost when BaseUrl is null
- **WHEN** `CreateAsync` is called with a `ProviderConfig` where `BaseUrl` is null
- **THEN** the underlying `OllamaApiClient` connects to `http://localhost:11434`

#### Scenario: GetAvailableModelsAsync returns empty list when server unreachable
- **WHEN** `GetAvailableModelsAsync` is called and the Ollama server is not running
- **THEN** it returns an empty list without throwing

### Requirement: OllamaProviderFactory wizard flow
`OllamaProviderFactory.GetNextStepAsync` SHALL drive a three-step wizard: (1) a `ChooseOneStep` with id `"deployment"` offering "Local (localhost)" and "Cloud (ollama.com)"; (2) a `TextInputStep` with id `"base-url"` whose default is `http://localhost:11434/api` for local or `https://ollama.com/api` for cloud, overridden by `OLLAMA_HOST` env var when set; (3) a `ChooseOneStep` with id `"model"` whose options are fetched live from the base URL via `GetAvailableModelsAsync`, showing a not-reachable hint when the list is empty; completing all steps returns a `WizardCompletedStep`.

#### Scenario: First step requests deployment type
- **WHEN** `GetNextStepAsync` is called with an empty `WizardState`
- **THEN** it returns a `ChooseOneStep` with id `"deployment"` and exactly two options: "Local (localhost)" and "Cloud (ollama.com)"

#### Scenario: Second step defaults to local base URL
- **WHEN** `GetNextStepAsync` is called with an answered `"deployment"` step selecting "Local"
- **THEN** it returns a `TextInputStep` with id `"base-url"` and default value `http://localhost:11434/api`

#### Scenario: Second step defaults to cloud base URL
- **WHEN** `GetNextStepAsync` is called with an answered `"deployment"` step selecting "Cloud"
- **THEN** it returns a `TextInputStep` with id `"base-url"` and default value `https://ollama.com/api`

#### Scenario: Second step uses OLLAMA_HOST when set
- **WHEN** `GetNextStepAsync` is called for the `"base-url"` step and `OLLAMA_HOST` is set in the environment
- **THEN** the returned `TextInputStep` has `Default` equal to the `OLLAMA_HOST` value and `Required = false`

#### Scenario: Third step lists models from server
- **WHEN** `GetNextStepAsync` is called with answered `"deployment"` and `"base-url"` steps and Ollama is running
- **THEN** it returns a `ChooseOneStep` with id `"model"` whose options come from the live model list

#### Scenario: Third step shows hint when server unreachable
- **WHEN** `GetNextStepAsync` is called for the model step and Ollama is not reachable
- **THEN** it returns a `ChooseOneStep` with id `"model"` containing a single non-selectable option indicating Ollama is not running

#### Scenario: Wizard completes after model selection
- **WHEN** `GetNextStepAsync` is called with answered `"deployment"`, `"base-url"`, and `"model"` steps
- **THEN** it returns a `WizardCompletedStep` with a non-empty confirmation message

### Requirement: Capability heuristic for Ollama models
`OllamaProviderFactory.GetCapabilities(string modelId)` SHALL derive `ChatClientCapabilities` from the model ID using the pattern table defined in the `provider-extension` spec: tool-calling patterns (`*-it-*`, `*-instruct*`, `*-chat*`), reasoning patterns (`*reason*`, `qwen3*`, `*thinking*`, `*r1*`), embedding/reranker patterns (no tool calling, no reasoning). Unrecognised model IDs SHALL return `SupportsToolCalling = false`, `SupportsReasoning = false`.

#### Scenario: Instruct model infers tool calling
- **WHEN** `GetCapabilities("llama3.2:3b-instruct-q4_0")` is called
- **THEN** `SupportsToolCalling = true` and `SupportsReasoning = false`

#### Scenario: Reasoning model infers reasoning support
- **WHEN** `GetCapabilities("qwen3:14b")` is called
- **THEN** `SupportsReasoning = true`

#### Scenario: Unknown model returns conservative defaults
- **WHEN** `GetCapabilities("somemodel:latest")` is called for an unrecognised name
- **THEN** `SupportsToolCalling = false` and `SupportsReasoning = false`

### Requirement: Dmon.Providers.Ollama project isolation
`Dmon.Providers.Ollama` SHALL reference only `Dmon.Abstractions` and `OllamaSharp`. It SHALL NOT reference `Dmon.Core`, `Dmon.Providers`, or any other internal project. `Dmon.Core` SHALL reference `Dmon.Providers.Ollama` at startup registration only.

#### Scenario: Project dependency graph is clean
- **WHEN** the `Dmon.Providers.Ollama.csproj` is inspected
- **THEN** it contains `PackageReference` for `OllamaSharp` and `ProjectReference` for `Dmon.Abstractions` only

#### Scenario: OllamaProviderExtension registered at startup
- **WHEN** the daemon host starts
- **THEN** `OllamaProviderExtension` is registered with `IProviderRegistry` before the first turn
