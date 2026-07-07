## 1. MLX provider: concurrency-safe start (finding #3)

- [x] 1.1 Add an instance `SemaphoreSlim(1,1)` to `MlxProviderExtension` and guard the check-then-spawn critical section of `EnsureRunningAsync`, re-running the `IsRunningAsync` liveness check inside the gate so a caller that waited behind an in-progress spawn attaches instead of spawning a second server on the fixed port.
- [x] 1.2 Make `StopAsync` acquire the same gate so an idle teardown cannot interleave with a spawn (no kill-mid-spawn orphan); dispose the semaphore in `Dispose`. Preserve the existing attach-first/idempotent behavior and `KillServer`'s `Interlocked` kill-idempotency.
- [x] 1.3 Add a test in `test/Dmon.Providers.Mlx.Tests/MlxProviderExtensionTests.cs`: concurrent `EnsureRunningAsync` callers on a cold runtime spawn exactly once (assert a single spawn), the other callers attach, and no process is orphaned (the retained handle references the one live server, killable by `StopAsync`/`Dispose`). Cover single-flight venv provisioning.

## 2. MLX provider: request-path self-heal (finding #2)

- [ ] 2.1 Add an ensure-running delegating `IChatClient` in `Dmon.Providers.Mlx` that calls `EnsureRunningAsync` (attach-first, now gated) before delegating `GetResponseAsync`/`GetStreamingResponseAsync` to the inner OpenAI-compatible client.
- [ ] 2.2 Wire `MlxClient(key)` (`MlxClientExtensions.cs`) to return that wrapper for both the first-line and escalation runtimes; keep the existing eager `EnsureRunningAsync` at build time (warm-at-build) intact.
- [ ] 2.3 Add a test proving that after `StopAsync` (idle teardown) a subsequent request through the wrapper respawns the runtime (attach-first no-ops when already live; respawns when dead), asserting no dispatch to a dead endpoint.

## 3. TriageRouter: fault-recovering backend resolution (finding #1)

- [ ] 3.1 Replace the poisoning `Lazy<Task<IChatClient>>` per-backend fields in `TriageRouter` with a fault-recovering resolver: cache a successfully-completed `Task<IChatClient>`; on a null-or-faulted cached task, start a fresh resolution under a `SemaphoreSlim(1,1)` (single-flight) and cache only on success. Resolution stays lazy and I/O-free at construction (ADR-027 D1 / ADR-032 D3).
- [ ] 3.2 Update `Dispose` to dispose only a successfully-resolved client (a faulted/absent resolution has nothing to dispose) and the new gate(s); reconcile the stale `EscalationWarmingService` "self-heal path" comment (lines ~12–15) to describe the now-real request-path respawn.
- [ ] 3.3 Extend `test/Daemon.Routing.Tests/TriageRouterTests.cs`: a backend whose factory faults on first use then succeeds recovers on a later turn (faulted attempt not cached forever); keep the existing resolve-once-on-success and concurrent-first-turns-resolve-once guarantees green.

## 4. Gates and spec alignment

- [ ] 4.1 `make build` clean (TreatWarningsAsErrors on) and `dotnet build daemon/Daemon.cs -c Release` compiles (the file-based composition root is not built by `make`).
- [ ] 4.2 `env -u MEKO_API_KEY dotnet test test/Dmon.Providers.Mlx.Tests` and `env -u MEKO_API_KEY dotnet test test/Daemon.Routing.Tests` green, then a full `env -u MEKO_API_KEY make test` green (single run — concurrent full-suite runs collide/hang; pkill stale `Everything.slnx` testhost first).
- [ ] 4.3 `openspec validate mlx-escalation-resilience --strict` passes; the change's delta specs (`triage-routing` ADDED/MODIFIED, `mlx-provider` ADDED) match the implemented behavior.
