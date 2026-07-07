# DEVLOG — runtime-rpc-write-serialization

Narrative companion to `tasks.md`. Newest block last.

## Block 1 — tasks 1.1–2.3 (+ Group 3 gates 3.1–3.3) — "Write-serialized, atomic-framed, disposable RPC transport + concurrency regression test"

Single-block change (one production method + one field/`IDisposable` + one private disposal wiring + one gateway `using` + one test file). Group 3 folded into this block's gate run.

**Implementation (D1/D2/D4):** `CoreProcessRpcTransport.SendAsync` now serializes the command to JSON **outside** a new `private readonly SemaphoreSlim _writeGate = new(1, 1)`, then `await _writeGate.WaitAsync(cancellationToken)`; inside `try`/`finally` (release) it writes `(json + "\n")` as **one** `WriteAsync` + `FlushAsync`. Single-call byte output unchanged (json + exactly one `\n`, never `\r`). Ports the already-shipped gateway pattern `SessionHandler.WriteToCoreAsync`. Cancellation: `WaitAsync(ct)` throws before the `try`, so a caller cancelled while queued does no partial write and never over-releases.

**Disposal (D3):** class is now `: IRpcTransport, IDisposable` with `Dispose() => _writeGate.Dispose()` — disposes **only** the semaphore, never the borrowed `_reader`/`_writer` streams. **`IDisposable` was NOT added to the `IRpcTransport` interface** (proposal forbids interface signature changes). Disposal wired via the concrete type at two owner classes:
- Gateway handshake `NetworkConnectionEndpoint.cs:489` → `using CoreProcessRpcTransport transport = new(process);` (method-local, safe because `Dispose` doesn't touch the process's reused streams).
- `RpcClient.DisposeAsync` gained `if (_transport is IDisposable d) d.Dispose();` — releases the semaphore for Terminal AND Desktop (the live-trigger host) without a contract change. `FeedableTransport` (test-only) isn't `IDisposable`, so the probe skips it. `SemaphoreSlim.Dispose()` is idempotent + finalizer-free, so no double-dispose hazard across the five construction sites (all audited: Terminal, Desktop ×2, gateway, tests).

**Test (2.1–2.3):** `SendAsync_ConcurrentCallers_FramesNotInterleaved` in `RpcTransportTests.cs` + a nested `ThreadSafeCapturingWriter` whose `WriteAsync`/`FlushAsync` `await Task.Yield()` then append under a lock — the yield forces a scheduling gap between the pre-fix code's two separate writes so 50 concurrent callers reliably interleave without the fix. Asserts: no `\r`, frame count == 50, each line deserializes to a non-null `TurnSubmitCommand`, and the multiset of `Id`s equals what was sent. **Worker confirmed it fails against the pre-fix two-await form** (`JsonException: '{' is invalid after a single JSON value`) then restored the fix. Post-fix pass is deterministic (whole frame appended under one lock acquisition). Existing framing tests (`SendAsync_WritesJsonPlusLineFeed_ExactBytes`, `SendAsync_ExactlyOneLf_NoCrLf`) stay green.

**Reviewer nits (non-blocking, not actioned):** (1) `ThreadSafeCapturingWriter.WriteAsync`/`FlushAsync` accept a `cancellationToken` they don't observe — fine for a test double; (2) a one-line comment noting the harness proves the fix (post-fix pass deterministic) rather than deterministically reproducing the bug would be nice. Neither affects correctness; left as-is.

**Gates:** `make build` clean (0 warnings, TWAE on); `Dmon.Runtime.Tests` 40/40; full `env -u MEKO_API_KEY make test` green (Core 606/1-skip, Terminal 187, Network 212, Providers 32, Dcal 27, LlamaCpp 29, …); `openspec validate --strict` valid. Reviewer signed off. Change is code-complete.
