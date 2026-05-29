## ADDED Requirements

### Requirement: Provider SDK ABI consistency

The agent core's resolved runtime dependency closure SHALL be binary-compatible with the active provider SDK, such that a turn can be initiated against the configured provider without a runtime `MissingMethodException` / `MethodAccessException`. Specifically, the `Microsoft.Extensions.AI` family version SHALL match the `Microsoft.Extensions.AI.Abstractions` version that the bundled Anthropic provider SDK was compiled against. This version pin SHALL be documented at the dependency declaration so it is not bumped without re-validating provider SDK compatibility.

#### Scenario: First Anthropic turn does not fault on a missing SDK member

- **WHEN** the core executes the first `turn.submit` of a session with the Anthropic provider active
- **THEN** the provider call completes the turn-execution loop (emitting `turnStart` … `turnEnd`) without throwing a runtime `MissingMethodException` originating from a `Microsoft.Extensions.AI` type version mismatch

#### Scenario: M.E.AI version stays aligned with the Anthropic SDK

- **WHEN** the solution is built
- **THEN** the resolved `Microsoft.Extensions.AI.Abstractions` version equals the version declared by the bundled Anthropic provider SDK package, so no member referenced by the SDK is absent at runtime
