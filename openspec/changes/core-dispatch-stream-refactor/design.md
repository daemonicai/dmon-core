## Context

`Dmon.Core` reads host commands as JSONL over stdin. Today the read path is split between `RpcHostedService.ExecuteAsync` (a `while` loop calling `Console.In.ReadLineAsync`) and `CommandDispatcher.DispatchAsync`, which:

1. `JsonDocument.Parse(line)` — malformed JSON → `malformedCommand` error event.
2. Manually reads the `"type"` string — missing → `missingType`.
3. Routes through a ~35-arm `switch` on that string (`RouteAsync`), deserializing the matching `JsonElement` to a concrete command only at the routing site.
4. Special-cases `turn.submit`/`wizard.start`: because routing happens *before* deserialization and a `JsonElement` is a view into the soon-to-be-disposed `JsonDocument`, it eagerly deserializes to a POCO inside the `using(doc)` block (`BuildBackgroundWork`/`DeserializeAndBind`) before handing the work to a tracked background task.
5. Wraps errors in two near-identical `catch` ladders — one inline (`DispatchAsync`), one for background work (`RunBackgroundAsync`).

Three liabilities follow: the string `switch` duplicates the `[JsonPolymorphic]`/`[JsonDerivedType]` discriminator table on `Command` (two maps that can drift); the `JsonDocument`-lifetime dance exists *only* because routing precedes deserialization; and error handling is duplicated. The wire contract (ADR-003) and the host (which already consumes events via `Channel<Event>`) are unaffected by how the core internally shapes its read path.

Constraints that bind this change:
- **ADR-003** — JSONL/stdio, strict LF framing, strip trailing `\r`, commands fire-and-forget. Wire-only; it does not constrain the internal dispatch mechanism.
- **`agent-core` spec — "Dispatch loop does not block on long-running commands"** (lines 150-167). Its four scenarios (`DispatchLoopIntegrationTests.cs`) are the behavioral contract and the regression net.
- **CLAUDE.md** — `TreatWarningsAsErrors`; no new heavyweight deps; `cancellationToken` last param.

## Goals / Non-Goals

**Goals:**
- Read path as a small pipeline of *total* stages: `IAsyncEnumerable<string>` source → pure `ParseCommand` (errors become values, never thrown) → typed sink routing on CLR type.
- Single routing source of truth: the `[JsonDerivedType]` table; delete the duplicate string `switch`.
- Delete the `JsonDocument`-lifetime apparatus by materializing POCOs before routing.
- One shared error guard for inline and background paths.
- Byte-identical wire behavior and identical error codes/recoverable flags (one documented nuance).
- Zero new dependencies.

**Non-Goals:**
- No change to the JSONL wire format, discriminators, or `agentReady` handshake.
- No change to the `turn.submit`↔`tool.confirmResponse` / `wizard.start`↔`wizard.answer` TCS suspension model. (A future change may replace the TCS with a shared inbound-command stream; explicitly out of scope here.)
- No change to the Terminal host (already `Channel<Event>`).
- No `System.Reactive`. The event side already realizes the reactive/observer pattern via `System.Threading.Channels`; the command side is request handling, not stream composition, and `IObservable`'s `OnError`-terminates-the-sequence semantics actively conflict with the "reader must survive bad input" requirement.

## Decisions

### D1 — Total parse stage over throwing-then-catching
`ParseCommand(string) → CommandParse` is pure and total, returning a closed result:
```
abstract record CommandParse;
record ParsedCommand(Command Command) : CommandParse;
record ParseFault(ErrorEvent Error) : CommandParse;
```
The sink matches on the result; errors are data, not exceptions. **Why:** makes the failure modes exhaustive and unit-testable without stdio, and keeps the reader loop trivially alive on bad input. **Alternative considered:** keep throwing and catch at the loop — rejected; it's what we have, and it scatters the error contract across `catch` sites.

### D2 — Polymorphic deserialize against base `Command`, not a string switch
`ParseCommand` calls `RootElement.Deserialize<Command>(options)`; STJ dispatches on the `"type"` discriminator via the existing `[JsonDerivedType]` attributes. The sink routes on the concrete CLR type (`cmd switch { TurnSubmitCommand c => ..., ... }`). **Why:** eliminates the duplicate discriminator map; adding a command is now a one-place edit (the attribute) plus a route arm whose absence is a localized, obvious gap rather than a silent `unknownCommand`. **Alternative considered:** keep manual string routing — rejected; it's the source of the drift liability.

### D3 — Preserve all five error codes via parse → peek → deserialize
To keep `malformedCommand` / `missingType` / `unknownCommand` distinct (polymorphic deserialize alone would collapse them), `ParseCommand`:
1. `JsonDocument.Parse` in `try` → `JsonException` ⇒ `malformedCommand` (recoverable).
2. `TryGetProperty("type")` absent ⇒ `missingType` (recoverable).
3. `Deserialize<Command>` in `try` → `JsonException` ⇒ `unknownCommand` (recoverable).
Handler-raised `NotImplementedException` ⇒ `notImplemented` (recoverable); any other handler exception ⇒ `internalError` (non-recoverable). The POCO is materialized inside the `using(doc)`, so no `JsonElement` outlives the document — the lifetime dance is structurally unnecessary, not merely relocated.

### D4 — One error guard for both dispatch paths
`RunGuardedAsync(Command, ct)` wraps `Route(...)` with the `OperationCanceledException` / `NotImplementedException` / `Exception` ladder. Inline commands `await` it; long-running commands (`turn.submit`, `wizard.start`) add it to the existing `ConcurrentBag<Task>` for shutdown observability. **Why:** collapses the two duplicated `catch` ladders; background-error-surfacing (spec scenario) is preserved by construction.

### D5 — Source stage owns framing; loop stays single-reader
`ReadLinesAsync(TextReader, [EnumeratorCancellation] ct)` yields trimmed, non-blank lines and `yield break`s on stdin EOF or cancellation. `ExecuteAsync` becomes `await foreach (line ...) await dispatcher.DispatchAsync(line, ct);` then `DrainAsync()`. **Why:** isolates framing, keeps the loop strictly sequential (no concurrency added), and makes EOF/cancel handling testable. Hand-written iterator (no `System.Linq.Async`) is the deliberate dep-free cost.

## Risks / Trade-offs

- **[Polymorphic deserialize behavioral parity]** STJ may differ from manual `element.Deserialize<TConcrete>` on edge cases (non-first discriminator, unknown discriminator, missing `required` member, extra members). → Mitigation: dedicated unit tests for each, plus the four existing real-loop integration tests must pass unchanged.
- **[Accepted error nuance]** A malformed payload of a *known* command type (e.g. `turn.submit` missing `message`) shifts from `internalError`(recoverable:false) — today it throws during routing-site deserialize — to `unknownCommand`(recoverable:true), because deserialize now happens in the parse stage. → Mitigation: this is strictly more correct (a client-fixable bad request is recoverable, not a core fault) and is documented here and asserted by a test. No spec pins the old behavior.
- **[Missing-route regression]** Forgetting a route arm for a newly added command would now surface at the typed sink rather than the string switch. → Mitigation: the `_ =>` arm throws `InvalidOperationException("No route for ...")`, which the guard turns into a loud `internalError` — fail-loud, not silent-drop.
- **[Scope creep toward the TCS rework]** The shared-inbound-stream idea is tempting to fold in. → Mitigation: explicit Non-Goal; this change leaves `TurnHandler` suspension untouched and the integration tests prove the round-trips still work.
