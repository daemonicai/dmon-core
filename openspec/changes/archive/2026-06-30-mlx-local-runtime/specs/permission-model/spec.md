## ADDED Requirements

### Requirement: Composition-declared backend standing consent
A local model runtime that is declared as a backend in the composition root SHALL be treated as carrying the author's standing consent to be (re)spawned by the agent, without an interactive confirmation prompt on each start, warm, or respawn. This applies only to backends explicitly declared in the composition; interactive/ad-hoc provider use SHALL continue to require the normal confirmation gate.

#### Scenario: Composition-declared runtime spawns without prompting
- **WHEN** a runtime declared as a composition backend is started, warmed, or respawned (e.g. after an idle teardown)
- **THEN** it spawns without an interactive confirmation prompt

#### Scenario: Undeclared interactive provider use still prompts
- **WHEN** a provider not declared as a composition backend is asked to start a server interactively
- **THEN** the normal confirmation gate still applies

#### Scenario: Repeated warms do not re-prompt
- **WHEN** the warming policy issues repeated `EnsureRunningAsync` calls for a declared backend across many sessions
- **THEN** no confirmation prompt is raised for any of them
