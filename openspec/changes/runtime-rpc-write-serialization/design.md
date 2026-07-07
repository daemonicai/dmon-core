## Context

`CoreProcessRpcTransport` (`core/Dmon.Runtime/CoreProcessRpcTransport.cs`) is the single real `IRpcTransport` implementation. It is `sealed`, holds `TextReader _reader` / `TextWriter _writer` (borrowed from the core process's stdio — it does not own or close them), and an optional `Action<string>? _onParseError`. It is not currently `IDisposable`. `RpcClient.SendAsync` (`RpcClient.cs:73-77`) is a thin pass-through to `_transport.SendAsync` and adds no serialization, so any write gate must live in the transport.

`SendAsync` (lines 57-65) serializes the `Command` to JSON, then does three separate awaits: `WriteAsync(json)`, `WriteAsync("\n")`, `FlushAsync`. `TextWriter`/`StreamWriter` is not thread-safe, and the split writes mean two concurrent callers on the same `_writer` can interleave bytes and terminators, corrupting ADR-003 strict-LF framing. The read side (`Events`/`ReadEventsAsync`) is a cold single-consumer iterator, single-pump by convention across Terminal, Desktop, and the gateway — not a concurrency concern.

The only other `IRpcTransport` implementation is the test-only `FeedableTransport` (`test/Dmon.Runtime.Tests/RpcClientTests.cs:26`), which appends to a list and has no stream framing to corrupt.

The gateway already ships the target pattern for its own core-stdin writes: `SessionHandler.WriteToCoreAsync` (`SessionHandler.cs:356-368`) — `SemaphoreSlim(1,1) _stdinLock`, acquire → single `WriteAsync((frame + "\n").AsMemory(), ct)` → flush → release. This change ports that into the shared transport.

## Goals / Non-Goals

**Goals:**
- Concurrent `SendAsync` calls on one `CoreProcessRpcTransport` produce whole, un-interleaved JSONL frames — each frame's JSON + single `\n` + flush is atomic under a write gate.
- Honour the `CancellationToken` while awaiting the gate.
- Release the gate deterministically (`IDisposable`), without changing stream ownership.
- Regression test proving no interleave under concurrency; all existing framing tests stay green.

**Non-Goals:**
- No change to the wire protocol, `IRpcTransport`/`IRpcClient`/`RpcClient` signatures, or `SendAsync`'s observable single-call byte output (still exactly `json` + one `\n`, no `\r`).
- No serialization of the read/event path (single-pump by design).
- No new ADR (conforms to ADR-003).
- Not touching the other audit findings.

## Decisions

**D1 — `SemaphoreSlim(1,1)` write gate, single atomic write.** Add `private readonly SemaphoreSlim _writeGate = new(1, 1);`. In `SendAsync`, serialize the command outside the gate (pure CPU, no shared state), then `await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false)`; inside a `try`/`finally` that releases the gate, write the frame and its terminator as **one** concatenated write — `await _writer.WriteAsync((json + "\n").AsMemory(), cancellationToken).ConfigureAwait(false)` — then `await _writer.FlushAsync(cancellationToken).ConfigureAwait(false)`. Collapsing the two writes into one string keeps a single failure point and mirrors `SessionHandler.WriteToCoreAsync`. The observable byte output for a single call is unchanged (JSON then exactly one `\n`, never `\r`). Prefer building the string with concatenation/interpolation over `AsMemory()` on two segments so the whole frame reaches the writer in one call.

**D2 — Serialize *outside* the gate, write *inside*.** `JsonSerializer.Serialize` touches no shared state and can run concurrently; only the `_writer` interaction must be serialized. Keeping serialization outside the gate minimizes the critical section (shorter hold, better throughput) while still guaranteeing atomic framing.

**D3 — `IDisposable` to release the semaphore.** The transport becomes `IDisposable`; `Dispose()` calls `_writeGate.Dispose()` and nothing else — it must **not** close `_reader`/`_writer` (it borrows the process streams; the process owner closes them). Audit `CoreProcessRpcTransport` construction sites (Terminal `Program.cs:99-100`, gateway handshake `NetworkConnectionEndpoint.cs:489`, tests) to confirm none rely on it being non-disposable and that adding `IDisposable` introduces no double-dispose of streams. If a caller's lifetime makes disposal awkward, the semaphore leak is benign (finalizer-free, process-scoped) — but wire `Dispose` correctly where the transport has a clear owner. This is a stop-and-ask only if a construction site cannot own disposal without a larger refactor.

**D4 — Cancellation semantics.** `WaitAsync(cancellationToken)` throws `OperationCanceledException` if cancelled while queued — consistent with the existing method already taking `cancellationToken` and callers expecting cancellation to surface. A cancelled caller that never entered the gate performs no partial write. A caller cancelled mid-write behaves exactly as today (the write/flush already took the token).

**D5 — Spec delta.** Modify the `host-rpc-transport` "Framed JSONL transport over a core's stdio" requirement to state that `SendAsync` writes each frame atomically and serializes concurrent callers so frames never interleave; add a scenario for concurrent sends producing whole, ordered-per-caller, non-interleaved frames. Keep the two existing scenarios.

## Risks / Trade-offs

- **Throughput.** A single-permit gate serializes all sends on one transport. This is correct and matches the gateway; send volume per core is low (interactive command frames), so contention is negligible. Serializing outside the gate (D2) keeps the held section to just the write+flush.
- **Disposal wiring (D3).** The main real change beyond the gate. Mitigation: `Dispose` only disposes the semaphore, never the borrowed streams; verify each construction site. Not disposing leaks only a process-scoped semaphore.
- **Concurrency test flakiness.** A concurrency regression test that asserts "no interleave" must be deterministic. Mitigation: fire N concurrent `SendAsync` against the injected-writer test constructor, await all, then assert every `\n`-delimited line parses as a valid frame and the multiset of frames equals what was sent — a property that holds deterministically once writes are serialized, and reliably fails (garbled/short lines) without the fix. The capturing `TextWriter` must itself be thread-safe (lock around its append) so the harness doesn't add its own races.

## Open Questions

None blocking. The only thing the worker must verify during investigation is D3 — that every `CoreProcessRpcTransport` construction site can either own `Dispose` or safely leave the (benign) semaphore undisposed; if a site needs a lifetime refactor beyond this change's scope, that is a stop-and-ask.
