## ADDED Requirements

### Requirement: Platform applicability check
`OmlxProviderExtension.IsApplicable()` SHALL return `true` if and only if the current process is running on macOS (`OSPlatform.OSX`) on an ARM64 architecture (Apple Silicon). It SHALL return `false` on all other platforms and CPU architectures. It SHALL NOT throw; exceptions are caught and treated as `false`.

#### Scenario: Apple Silicon Mac
- **WHEN** `IsApplicable()` is called on macOS ARM64
- **THEN** it returns `true`

#### Scenario: Intel Mac
- **WHEN** `IsApplicable()` is called on macOS x64
- **THEN** it returns `false`

#### Scenario: Non-macOS platform
- **WHEN** `IsApplicable()` is called on Linux or Windows
- **THEN** it returns `false`

---

### Requirement: Server identity probe
`OmlxProviderExtension.IsRunningAsync()` SHALL call `GET {baseUrl}/v1/models` with the configured `x-api-key` header and return `true` if and only if the response is HTTP 200 and at least one model entry has `"owned_by": "omlx"`. It SHALL return `false` on any network error, non-200 response, or if no entry has the expected `owned_by`. It SHALL use an internal timeout of ≤ 2 seconds. It SHALL NOT throw.

#### Scenario: oMLX running and responding
- **WHEN** `IsRunningAsync()` is called and the server returns a 200 response containing `"owned_by": "omlx"`
- **THEN** it returns `true`

#### Scenario: Port occupied by a different server
- **WHEN** `IsRunningAsync()` is called and the server returns a 200 response but no entry has `"owned_by": "omlx"`
- **THEN** it returns `false`

#### Scenario: Server not running
- **WHEN** `IsRunningAsync()` is called and the connection is refused or times out
- **THEN** it returns `false`

---

### Requirement: Server lifecycle management
`OmlxProviderExtension.EnsureRunningAsync()` SHALL launch the oMLX application via `open -a oMLX` if `IsRunningAsync()` returns `false`, then poll `IsRunningAsync()` at 1-second intervals until it returns `true` or a configurable timeout elapses (default: 30 seconds). It SHALL be a no-op if `IsRunningAsync()` already returns `true`. It SHALL throw `TimeoutException` if the server does not become reachable within the timeout.

#### Scenario: Server already running
- **WHEN** `EnsureRunningAsync()` is called and the server is already reachable
- **THEN** it returns without launching the app or waiting

#### Scenario: Server starts within timeout
- **WHEN** `EnsureRunningAsync()` is called and the server is not running but starts within the timeout period
- **THEN** it launches oMLX via `open -a oMLX` and returns once the server responds

#### Scenario: Server fails to start within timeout
- **WHEN** `EnsureRunningAsync()` is called and the server does not become reachable within the configured timeout
- **THEN** it throws `TimeoutException`

---

### Requirement: Model listing with capability heuristic
`OmlxProviderExtension.ListModelsAsync()` SHALL call `GET {baseUrl}/v1/models` with the configured `x-api-key` header and return one `ModelInfo` per entry in the response `data` array. Each `ModelInfo.Id` SHALL be the `id` field from the response. Each `ModelInfo.Capabilities` SHALL be derived from the model ID using the capability heuristic (see below). If the server is not running or returns an error, it SHALL return an empty list.

**Capability heuristic** (case-insensitive, evaluated in order, first match wins):

| Model ID pattern | SupportsToolCalling | SupportsReasoning |
|-----------------|---------------------|-------------------|
| contains `embed`, `-e-`, or `rerank` | false | false |
| starts with `qwen3`, or contains `thinking`, `-r1`, or `reason` | true | true |
| contains `-it-`, `instruct`, or `-chat` | true | false |
| contains `vlm`, `vision`, or `-vl-` | true | false |
| (unrecognised) | false | false |

#### Scenario: Models returned successfully
- **WHEN** `ListModelsAsync()` is called and the server returns a list of models
- **THEN** it returns one `ModelInfo` per model, with `Id` set to the model's `id` field

#### Scenario: Instruction-tuned model ID
- **WHEN** `ListModelsAsync()` is called and the server returns a model with id `gemma-4-e4b-it-4bit`
- **THEN** the corresponding `ModelInfo.Capabilities` has `SupportsToolCalling = true` and `SupportsReasoning = false`

#### Scenario: Reasoning model ID
- **WHEN** `ListModelsAsync()` is called and the server returns a model with id `qwen3-8b-4bit`
- **THEN** the corresponding `ModelInfo.Capabilities` has `SupportsToolCalling = true` and `SupportsReasoning = true`

#### Scenario: Server not running
- **WHEN** `ListModelsAsync()` is called and the server is not reachable
- **THEN** it returns an empty list without throwing

---

### Requirement: Custom auth header injection
`OmlxProviderFactory` SHALL inject the configured API key as an `x-api-key` HTTP request header on every request to the oMLX server. When the API key is null or empty, the header SHALL be omitted. The factory SHALL NOT send an `Authorization: Bearer` header.

#### Scenario: API key configured
- **WHEN** a request is made to the oMLX server with a non-empty API key
- **THEN** the HTTP request contains `x-api-key: <key>` and no `Authorization` header

#### Scenario: No API key configured
- **WHEN** a request is made to the oMLX server with a null or empty API key
- **THEN** the HTTP request contains neither `x-api-key` nor `Authorization` headers

---

### Requirement: Configuration from environment variables
`OmlxProviderExtension` and `OmlxProviderFactory` SHALL resolve configuration in the following priority order:
1. Explicit values passed via constructor (for testing)
2. `OMLX_BASE_URL` environment variable (full URL, e.g. `http://localhost:8666`)
3. `OMLX_API_KEY` environment variable
4. Defaults: base URL `http://localhost:8666`, API key empty string

#### Scenario: Environment variable overrides default
- **WHEN** `OMLX_BASE_URL` is set to `http://localhost:9000`
- **THEN** all requests are sent to `http://localhost:9000`

#### Scenario: No environment variables set
- **WHEN** no environment variables are set and no constructor values provided
- **THEN** the base URL defaults to `http://localhost:8666` and the API key defaults to empty string
