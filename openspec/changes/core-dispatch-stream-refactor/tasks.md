## 1. Total parse stage

- [ ] 1.1 Add internal closed result types in `Dmon.Core/Rpc`: `abstract record CommandParse`, `record ParsedCommand(Command Command)`, `record ParseFault(ErrorEvent Error)`
- [ ] 1.2 Implement `static CommandParse ParseCommand(string line)` per design D3: `JsonDocument.Parse` (catch → `malformedCommand`) → `TryGetProperty("type")` absent → `missingType` → `RootElement.Deserialize<Command>(options)` inside `using(doc)` (catch `JsonException` → `unknownCommand`); never throws; materialize the POCO before the document is disposed
- [ ] 1.3 Unit-test `ParseCommand`: a valid command of a representative type returns `ParsedCommand`; malformed JSON → `malformedCommand`; no `type` → `missingType`; unknown `type` → `unknownCommand`; and the documented nuance — a known type with an invalid payload (e.g. `turn.submit` missing `message`) → `unknownCommand` (recoverable)

## 2. Typed sink and single error guard

- [ ] 2.1 Add `Task Route(Command cmd, CancellationToken cancellationToken)` — a pattern `switch` on the concrete command type mirroring every arm of the current `RouteAsync`; `_ =>` throws `InvalidOperationException($"No route for {cmd.GetType().Name}.")`
- [ ] 2.2 Add `RunGuardedAsync(Command cmd, CancellationToken cancellationToken)` wrapping `Route` with the `OperationCanceledException` (swallow) / `NotImplementedException` (→ `notImplemented`, recoverable) / `Exception` (→ `internalError`, non-recoverable) ladder
- [ ] 2.3 Rewrite `DispatchAsync` to `HandleAsync(ParseCommand(line), ct)`: `ParseFault` → emit the error event; `ParsedCommand` of `TurnSubmitCommand`/`WizardStartCommand` → add `RunGuardedAsync(...)` to `_backgroundTasks`; otherwise `await RunGuardedAsync(...)` inline
- [ ] 2.4 Delete the now-dead code: the string-keyed `RouteAsync`, `BuildBackgroundWork`, `DeserializeAndBind`, `RunBackgroundAsync`, and the `Deserialize<T>(JsonElement)` helper — confirm no remaining references

## 3. Source stage in RpcHostedService

- [ ] 3.1 Add `private static async IAsyncEnumerable<string> ReadLinesAsync(TextReader input, [EnumeratorCancellation] CancellationToken cancellationToken)` — trims trailing `\r`, skips blank lines, `yield break`s on stdin EOF (`null`) and on `OperationCanceledException`
- [ ] 3.2 Replace the `while` loop in `ExecuteAsync` with `await foreach (string line in ReadLinesAsync(Console.In, stoppingToken)) await _dispatcher.DispatchAsync(line, stoppingToken);` — preserve `agentReady`-before-first-command ordering and the trailing `_dispatcher.DrainAsync()`
- [ ] 3.3 Confirm the reader remains strictly single-threaded and sequential (no `Task.Run`/parallel consumption introduced)

## 4. Verification and gates

- [ ] 4.1 `DispatchLoopIntegrationTests` (wizard round-trip, tool-confirm round-trip, backgrounded-error-surfaced, abort-during-turn) pass unchanged
- [ ] 4.2 Add dispatch-level coverage for the new `agent-core` requirement scenarios: malformed JSON does not stop the loop, missing `type`, unknown `type`, and handler failure → `internalError` with the reader still alive
- [ ] 4.3 `make build` clean under `TreatWarningsAsErrors`; `make test` (or `dotnet test -c Release`) green
- [ ] 4.4 `openspec validate core-dispatch-stream-refactor --strict` passes
