## Purpose

Define the standing contract for `TriageRouter` (`daemon/Daemon.Routing`, ADR-027/028): the per-turn classify-dispatch flow over three backends (egress, local reasoner, e2b-with-tools), the privacy invariant that personal-scope turns never expose world-scope tools, the personal-bias override on low-confidence classification, the no-cross-turn-caching rule, the streaming ack contract, and the Personal→World misclassification metric.

## Requirements

### Requirement: Per-turn classify-dispatch flow
`TriageRouter.GetResponseAsync` SHALL classify the incoming message list with a structured-output call on the classifier client, then dispatch to one of three backends based on the resulting `RouteDecision`. The dispatch order SHALL be: egress when `Scope == "world"` and `Impersonal` and `Confidence > EgressThreshold`; otherwise the local reasoner when `Tier == Tier.Reasoner`; otherwise e2b-with-tools.

#### Scenario: Personal direct turn dispatches to e2b-with-tools
- **WHEN** a turn is classified as scope `"personal"`, `Tier.Direct`
- **THEN** the turn is dispatched to the e2b-with-tools client

#### Scenario: World direct turn with high confidence dispatches to egress
- **WHEN** a turn is classified as scope `"world"`, `Impersonal=true`, `Confidence > EgressThreshold`
- **THEN** the turn is dispatched to the egress client

#### Scenario: Reasoner tier dispatches to local reasoner regardless of scope
- **WHEN** a turn is classified as `Tier.Reasoner` and does not meet the egress condition
- **THEN** the turn is dispatched to the local reasoner client (whatever its scope)

#### Scenario: All other turns default to e2b-with-tools
- **WHEN** a turn matches neither the egress nor the reasoner condition
- **THEN** the turn is dispatched to the e2b-with-tools client

---

### Requirement: Privacy invariant — egress tools absent from personal-scope manifests
For any turn whose effective scope is `"personal"`, the `ChatOptions.Tools` passed to the dispatched client SHALL be exactly `AbilityRegistry.ForScope("personal")` and SHALL contain no tool whose `IAbilityProvider` declared a different scope. The invariant holds independently of which backend is selected.

#### Scenario: Personal turn has no world tools
- **WHEN** a turn is dispatched with effective scope `"personal"`
- **THEN** `ChatOptions.Tools` contains no tool whose ability provider declared scope `"world"`

#### Scenario: World turn has no personal tools
- **WHEN** a turn is dispatched with scope `"world"`
- **THEN** `ChatOptions.Tools` contains no tool whose ability provider declared scope `"personal"`

---

### Requirement: Personal bias on low-confidence classification
When the classifier returns a `RouteDecision` with `Confidence < EgressThreshold`, `TriageRouter` SHALL override the effective scope to `"personal"` before building the manifest and dispatching.

#### Scenario: Low-confidence world classification overridden to personal
- **WHEN** the classifier returns scope `"world"` with `Confidence < EgressThreshold`
- **THEN** the router dispatches with effective scope `"personal"` (not to egress) and a `"personal"` manifest

---

### Requirement: Per-turn routing — classification is not cached across turns
Each call to `GetResponseAsync` or `GetStreamingResponseAsync` SHALL perform a fresh classify pass and a fresh manifest build. A previous turn's route decision SHALL have no effect on the current turn.

#### Scenario: Consecutive turns with different intents route independently
- **WHEN** turn N classifies as `"personal"` and turn N+1 classifies as `"world"`
- **THEN** each turn is dispatched according to its own classification

---

### Requirement: Streaming path emits ack before the target's first token
`TriageRouter.GetStreamingResponseAsync` SHALL yield at least one `ChatResponseUpdate` (a fixed, route-dependent ack) before forwarding the target client's stream.

#### Scenario: Streaming response begins with ack update
- **WHEN** `GetStreamingResponseAsync` is called
- **THEN** the first yielded `ChatResponseUpdate` is emitted before the target client produces any output

---

### Requirement: Misclassification metric for the Personal→World direction
`TriageRouter` SHALL emit the counter `dmon.triage.misclassify.personal_to_world` (via `DaemonTelemetry`) exactly once for any turn where the raw classifier output indicated scope `"world"` and the confidence gate overrode the effective scope to `"personal"`.

#### Scenario: Confidence-gated world classification increments the metric
- **WHEN** the classifier returns scope `"world"` and the confidence gate overrides the effective scope to `"personal"`
- **THEN** `dmon.triage.misclassify.personal_to_world` is incremented by 1

#### Scenario: A confident personal classification does not increment the metric
- **WHEN** the classifier returns scope `"personal"` with `Confidence > EgressThreshold`
- **THEN** `dmon.triage.misclassify.personal_to_world` is not incremented
