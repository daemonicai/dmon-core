## Purpose

Define the standing contract for `TriageRouter` (`daemon/Daemon.Routing`, ADR-027/028): the per-turn classify-dispatch flow (egress, or first-line with `think_harder` self-escalation to the escalation client), the privacy invariant that personal-scope turns never expose world-scope tools, the personal-bias override on low-confidence classification, the no-cross-turn-caching rule, the streaming ack/buffer contract, and the Personal→World misclassification metric.

## Requirements

### Requirement: Per-turn classify-dispatch flow
`TriageRouter.GetResponseAsync` SHALL classify the incoming message list with a structured-output call on the classifier client, then dispatch as follows. When `Scope == "world"` and `Impersonal` and `Confidence > EgressThreshold`, the turn SHALL dispatch to the egress client. Otherwise the turn SHALL dispatch to the **first-line** client, built from the effective-scope tool manifest plus the `think_harder` tool; if the first-line response indicates escalation (see the escalation requirement), the turn SHALL be re-dispatched to the **escalation** client. There is no upfront reasoner/tier dispatch — the larger local model is reached only by first-line escalation, never by classification.

The classifier's structured output is `RouteDecision { Scope, Impersonal, Confidence }`; it carries no tier.

#### Scenario: Personal turn dispatches to the first-line client
- **WHEN** a turn is classified as scope `"personal"`
- **THEN** the turn is dispatched to the first-line client with the `"personal"` manifest plus `think_harder`

#### Scenario: World impersonal turn with high confidence dispatches to egress
- **WHEN** a turn is classified as scope `"world"`, `Impersonal=true`, `Confidence > EgressThreshold`
- **THEN** the turn is dispatched to the egress client

#### Scenario: A turn that does not meet the egress condition runs on the first-line client
- **WHEN** a turn does not meet the egress condition (not world, or not impersonal, or confidence at/below threshold)
- **THEN** the turn is dispatched to the first-line client (not to any reasoner)

#### Scenario: First-line answer without escalation is returned directly
- **WHEN** the first-line client produces a final answer without calling `think_harder`
- **THEN** that response is returned to the caller and the escalation client is never invoked

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

### Requirement: Streaming path buffers the first-line and emits an ack before the committed backend
`TriageRouter.GetStreamingResponseAsync` SHALL yield at least one `ChatResponseUpdate` (an ack) before any backend output. Because a first-line turn may escalate and discard its draft, the router SHALL NOT stream first-line tokens directly to the caller; it SHALL buffer the first-line generation and stream only the **committed** backend's output. The ack SHALL NOT claim the final route, since escalation is not known until the first-line completes. When a handoff to the escalation client occurs, the router SHALL yield a distinct escalation marker before forwarding the escalation client's stream.

#### Scenario: Streaming response begins with an ack
- **WHEN** `GetStreamingResponseAsync` is called
- **THEN** the first yielded `ChatResponseUpdate` is emitted before any backend produces caller-visible output

#### Scenario: No false-start tokens when escalation occurs
- **WHEN** the first-line client calls `think_harder` after emitting draft tokens
- **THEN** none of the first-line draft tokens are streamed to the caller; only the escalation client's output is streamed

#### Scenario: Escalation marker precedes the escalation stream
- **WHEN** a turn is handed off to the escalation client on the streaming path
- **THEN** a distinct escalation marker `ChatResponseUpdate` is yielded before the escalation client's first token

---

### Requirement: First-line self-escalation via `think_harder`
The router SHALL offer a `think_harder` tool in the first-line client's tool manifest, in addition to the effective-scope abilities, and SHALL NOT offer `think_harder` to the escalation client (the escalation client is the top rung). When the first-line model calls `think_harder`, the first-line generation loop SHALL terminate without producing a final answer (via `FunctionInvocationContext.Terminate`), and the router SHALL re-dispatch the turn to the escalation client. `think_harder` is a no-op signal tool: its invocation SHALL NOT perform external work. `think_harder` SHALL be specified to the model as a tool to be called alone (not in parallel with other tool calls in the same iteration), since termination may drop other same-iteration tool calls.

#### Scenario: think_harder hands the turn to the escalation client
- **WHEN** the first-line client calls `think_harder`
- **THEN** the first-line loop terminates and the turn is re-dispatched to the escalation client

#### Scenario: Escalation manifest excludes think_harder
- **WHEN** a turn is dispatched to the escalation client
- **THEN** the escalation client's tool manifest contains the effective-scope abilities but not `think_harder`

---

### Requirement: Escalation continues with the inherited message list
When a turn escalates, the escalation client SHALL receive the inherited conversation — the original messages together with the first-line client's intermediate assistant tool-call messages and their tool results — so it continues the work rather than restarting it. The `think_harder` function call and its corresponding function result SHALL be removed from the inherited message list before it is passed to the escalation client, so the escalation client never sees a tool it was not offered.

#### Scenario: Partial work is carried forward
- **WHEN** the first-line client invokes one or more real ability tools, gathers their results, then calls `think_harder`
- **THEN** the escalation client receives those tool calls and results in its input messages

#### Scenario: think_harder call and result are stripped
- **WHEN** the inherited message list is built for the escalation client
- **THEN** it contains no `FunctionCallContent` or `FunctionResultContent` for `think_harder`

---

### Requirement: Misclassification metric for the Personal→World direction
`TriageRouter` SHALL emit the counter `dmon.triage.misclassify.personal_to_world` (via `DaemonTelemetry`) exactly once for any turn where the raw classifier output indicated scope `"world"` and the confidence gate overrode the effective scope to `"personal"`.

#### Scenario: Confidence-gated world classification increments the metric
- **WHEN** the classifier returns scope `"world"` and the confidence gate overrides the effective scope to `"personal"`
- **THEN** `dmon.triage.misclassify.personal_to_world` is incremented by 1

#### Scenario: A confident personal classification does not increment the metric
- **WHEN** the classifier returns scope `"personal"` with `Confidence > EgressThreshold`
- **THEN** `dmon.triage.misclassify.personal_to_world` is not incremented

---

### Requirement: Backend resolution recovers from a transient fault
`TriageRouter` SHALL cache each backend client's **successful** resolution once, but SHALL NOT permanently cache a **faulted** resolution attempt. When a backend's resolution (the ADR-032 factory delegate, which performs the provider `EnsureRunningAsync` I/O) throws or faults on a given turn, a subsequent turn requiring that backend SHALL re-attempt resolution rather than re-throwing the previously cached fault. Resolution SHALL remain lazy and I/O-free at construction (ADR-027 D1 / ADR-032 D3): it is triggered by the first turn that needs the backend, not at build time. Concurrent first uses of the same backend SHALL still resolve it at most once while it is succeeding.

#### Scenario: Transient first-use resolution fault recovers on a later turn
- **WHEN** a backend's resolution faults on its first use (for example, the provider's `EnsureRunningAsync` throws a transient error) and a later turn needs that backend
- **THEN** the router re-attempts resolution for that backend and, if it now succeeds, dispatches the turn to it

#### Scenario: Successful resolution is cached
- **WHEN** a backend resolves successfully
- **THEN** subsequent turns reuse the cached client without re-resolving it

#### Scenario: Construction performs no I/O
- **WHEN** the `TriageRouter` is constructed
- **THEN** no backend factory is invoked and no provider I/O occurs until the first turn that needs a backend

---

### Requirement: Escalation backend is a fixed-port mlx runtime
The daemon's escalation backend SHALL be the mlx escalation runtime served on a fixed port. The escalation backend's lazily-resolved client MAY be cached for the daemon's lifetime; because the runtime always returns to the same fixed port after a teardown/respawn cycle, a cached escalation client SHALL remain valid across idle teardown and subsequent respawn. Before the escalation client produces output for a turn, the escalation runtime SHALL be ensured running (respawned on demand if it was torn down for idle or is otherwise not listening), rather than the turn being dispatched to a dead endpoint. This ensure-running check SHALL be attach-first, so it adds no meaningful latency when the runtime is already healthy. The requirement constrains the effect, not the seam: the ensure-running MAY be realized by the escalation client itself or by the request path that dispatches to it.

#### Scenario: Cached escalation client survives a respawn
- **WHEN** the escalation runtime is torn down for idle and later respawned on its fixed port
- **THEN** the previously resolved escalation client connects to the respawned server without being rebuilt

#### Scenario: Escalation request after idle teardown respawns the runtime
- **WHEN** the escalation runtime has been torn down for idle and an escalation is decided on the request path
- **THEN** the runtime is ensured running (respawned) before the escalation client produces output, and the turn completes against the respawned server rather than failing against a dead endpoint

#### Scenario: Healthy escalation runtime adds no respawn latency
- **WHEN** an escalation is decided and the escalation runtime is already running
- **THEN** the ensure-running check attaches to the live runtime without spawning a new process

---

### Requirement: Warming integrates without changing routing semantics
Activity-driven warming of the escalation runtime SHALL be additive to routing: it pre-starts the escalation runtime but SHALL NOT change when escalation is decided or which backend handles a turn. The escalation request path SHALL continue to ensure the runtime is running, so routing remains correct whether or not warming has completed.

#### Scenario: Warming does not alter escalation decisions
- **WHEN** warming has pre-started the escalation runtime
- **THEN** the first-line→`think_harder`→escalation decision flow is unchanged; warming only removes start-up latency at hand-off

#### Scenario: Routing correct without warming
- **WHEN** warming has not run and an escalation is decided
- **THEN** the escalation path ensures the runtime is running and the turn completes correctly
