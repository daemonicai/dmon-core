## Why

The shared host→core RPC transport `CoreProcessRpcTransport` (`core/Dmon.Runtime/CoreProcessRpcTransport.cs:57-65`) writes a JSONL command frame to the core process's stdin with **no write serialization**. Its `SendAsync` emits the JSON body and the terminating `\n` in two separate `await _writer.WriteAsync(...)` calls (lines 61, 63) plus a separate `FlushAsync` (line 64) against a shared `TextWriter`, which is not thread-safe. Two concurrent `SendAsync` calls can therefore interleave as `json_A`, `json_B`, `\n`, `\n` (or shred a single write), corrupting the ADR-003 strict-LF JSONL framing on the wire and desynchronizing the core's line reader.

This is not hypothetical. The Avalonia Desktop host (`frontends/Dmon.Desktop`, recently shipped) dispatches interactions through **`async void` fire-and-forget** handlers — `SessionViewModel.HandleToolConfirmAsync` and `HandleUiInputAsync` — each of which `await`s `CoreSessionService.SendAsync` → `RpcClient.SendAsync` → `CoreProcessRpcTransport.SendAsync` on the **same** `_writer`. Two such handlers (e.g. a tool-confirm racing a queued UI input) can be in flight simultaneously, so Desktop is a live trigger for interleaved frames. Terminal (`frontends/Dmon.Terminal`) drives all sends sequentially from one loop and so is single-writer today, which is why the flaw has not surfaced there.

The gateway already solved exactly this for its own stdin path: `SessionHandler.WriteToCoreAsync` (`frontends/Dmon.Network/Sessions/SessionHandler.cs:356-368`) guards writes with a `SemaphoreSlim(1,1)` and emits `frame + "\n"` as a **single** concatenated write, with the doc-comment "Writes are serialized because StreamWriter is not thread-safe; the frame and its terminating LF are emitted in a single write so framing cannot interleave." The shared runtime transport that all `IRpcClient`-based hosts sit on has no such guard.

## What Changes

- **Serialize writes in `CoreProcessRpcTransport.SendAsync`.** Add a `SemaphoreSlim(1,1)` write gate and emit each frame's JSON and its single trailing `\n` as one atomic write (concatenated) followed by a flush, all inside the gate — porting the already-shipped `SessionHandler.WriteToCoreAsync` pattern. Concurrent callers then produce whole, un-interleaved frames regardless of host concurrency. The `CancellationToken` is honoured on the wait.
- **Dispose the gate.** `CoreProcessRpcTransport` becomes `IDisposable` to release the `SemaphoreSlim` (it does not own the process streams, so it disposes only the semaphore).
- **Reads are unchanged.** The inbound event stream is a single-consumer cold iterator (`ReadEventsAsync`), single-pump by design across all hosts; only writes need serialization.
- **Regression test.** Add a test that fires many concurrent `SendAsync` calls through the transport's injected-writer test constructor and asserts every `\n`-delimited line parses back as a whole, valid frame (no interleave), with a thread-safe capturing writer.

No wire-protocol shape change, no `IRpcTransport`/`IRpcClient` signature change, no public-verb change, no host behavior change beyond frames no longer interleaving. Honours ADR-003 (strict-LF JSONL framing must not be corrupted).

## Capabilities

### New Capabilities

_None — this is a correctness/concurrency fix to the existing host RPC transport._

### Modified Capabilities

- `host-rpc-transport`: **MODIFIED** — the "Framed JSONL transport over a core's stdio" requirement is tightened so that `SendAsync` is safe under concurrent callers: each frame (JSON + single `\n` + flush) is written atomically and writes are serialized, so concurrent sends can never interleave or corrupt the JSONL framing.

## Impact

- **Code:** `core/Dmon.Runtime/CoreProcessRpcTransport.cs` (add semaphore write gate + single atomic write + `IDisposable`).
- **Tests:** `test/Dmon.Runtime.Tests/RpcTransportTests.cs` (concurrent-send regression test + a thread-safe capturing writer).
- **ADRs:** conforms to ADR-003 as written — **no amending/superseding ADR**.
- **No impact:** wire protocol shape, `IRpcTransport`/`IRpcClient`/`RpcClient` signatures, the read/event path, session storage, providers, the agent core, host behavior. Out of scope: the other audit findings (`session-store-partial-line-tolerance`, `ci-hardening`, build-hygiene, docs drift).
