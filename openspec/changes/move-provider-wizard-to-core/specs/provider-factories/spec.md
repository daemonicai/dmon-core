## ADDED Requirements

### Requirement: Wizard types live in the reference-free protocol leaf
The serialisable wizard types (`WizardStep` and its subtypes, and `WizardOption`) SHALL be defined in `Dmon.Protocol`. `Dmon.Protocol` SHALL remain a reference-free leaf assembly (no project or package references), so that the wire contract stays free of provider SDKs and `Microsoft.Extensions.AI`. `Dmon.Abstractions` MAY reference `Dmon.Protocol` (the dependency edge flows `Dmon.Abstractions → Dmon.Protocol`, never the reverse), allowing `IProviderFactory.GetNextStepAsync` to return a `WizardStep` that is simultaneously the in-process factory output and the RPC payload, with no DTO mapping. `WizardState` SHALL remain defined in `Dmon.Abstractions` as in-process session state.

#### Scenario: Protocol has no references
- **WHEN** `Dmon.Protocol.csproj` is inspected
- **THEN** it contains no `ProjectReference` and no `PackageReference`

#### Scenario: Abstractions references Protocol, not the reverse
- **WHEN** the solution dependency graph is inspected
- **THEN** `Dmon.Abstractions` references `Dmon.Protocol` and `Dmon.Protocol` does not reference `Dmon.Abstractions`

#### Scenario: Factory output is the wire type
- **WHEN** `IProviderFactory.GetNextStepAsync` returns a `WizardStep`
- **THEN** that same `WizardStep` instance can be embedded in a `WizardStepEvent` and serialised without conversion to a separate DTO
