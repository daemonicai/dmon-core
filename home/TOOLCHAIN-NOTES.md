# Toolchain notes — `home/`

External toolchain defects that this bucket's code works around. Not decisions
this project made, so not ADRs — ADRs record binding choices, and nothing here
was chosen. Each entry states the signature once, so a workaround comment in the
source can point here instead of restating it and drifting.

---

## Async task-context deallocation crash (Swift 6.3.3)

**Status: suspected toolchain defect, not confirmed.** The upstream Swift issue
tracker has **not been checked** — that is "we did not look", not "we looked and
found nothing". The evidence below is an exclusion argument, not a root cause
from the compiler side.

### Signature

```
malloc: *** error ... freed pointer was not the last allocation
```

**The message names `malloc`, and that is misleading — the fault is not in
libmalloc.** The crash report's faulting thread is:

```
__pthread_kill → pthread_kill → abort
→ swift::swift_Concurrency_fatalErrorv → swift::swift_Concurrency_fatalError
→ swift_task_dealloc
→ HostSupervisor.handleExit(id:)              (at `await sleep(delay)`)
→ closure #1 in HostSupervisor.apply(_:id:)   (the supervision `Task { [weak self] … }`)
→ partial apply / async thunk machinery (compiler-generated)
→ completeTaskWithClosure
```

`swift_task_dealloc` is Swift Concurrency's **own** stack-disciplined allocator
for async function context frames — separate from general-purpose
`malloc`/`free`. That it reuses libmalloc's wording when a task context is freed
out of the LIFO order its own discipline expects is inferred from the symbol name
and the observed message; Swift's runtime source was not read to confirm it.
Either way the frame is the fault site, which is the part that matters when
deciding where to look. Anyone who reads
the message and starts looking for a double-free in application pointer code is
looking in the wrong place; there is no unsafe pointer, no manual memory
management, anywhere in the crashing call chain.

### Implicated call shape

An unstructured `Task { [weak self] in … await self?.handleExit(id: id) }` — an
optional-chained weak-self call into an actor-isolated async function — whose
callee then suspends at `await sleep(delay)`.

### Known triggers

Both are declaration changes in `HostSupervisor` with no relationship to each
other, and neither is touched by any code path at the time of the crash:

1. A `var [Task<Void, Never>]` array field on `ChildState` — reproducible **even
   left entirely unpopulated**, with nothing ever writing to it. Worked around
   with two named `Task<Void, Never>?` fields (`stdoutReaderTask`,
   `stderrReaderTask`).
2. A stored `static let defaultLogDrainGrace: TimeInterval = 2` on
   `HostSupervisor` — a scalar constant with no bearing on instance layout.
   Worked around with a computed `static var` returning the same value.

**Both triggers produce the identical runtime function, fatal-error path, source
statement and caller.** That is the load-bearing observation: two semantically
disjoint triggers landing on the same line is what a compiler-side context-size
or lifetime miscalculation produces. A wild pointer write in application code
would surface wherever the shifted allocation layout happened to reuse a block,
not repeatedly at the same async-context deallocation in the same function.

### Causes excluded

Audited and cleared while investigating, so the next person need not repeat them:

- **`drainOutput`'s `defer { handle.closeFile() }` against the `DispatchSourceRead`
  watching that descriptor.** `ReadWaitBox.resumeFromReadability()` and `cancel()`
  both gate the only fd-touching call behind the same lock-protected `settled`
  check, so a late event-handler invocation returns before reaching `read(2)`;
  and the `defer` cannot run until `readChunk` has returned, which requires the
  continuation to have already resumed.
- **`Data` slicing in `drainOutput`'s line splitter.** `Data.SubSequence` is
  `Data` — real COW value semantics — and the bytes are copied out by
  `String(decoding:as:)` before `removeSubrange` mutates the buffer.
- **`ChildSpawner`'s `strdup`/`free` of `cArgs`/`cEnv`.** Each `defer` frees
  exactly the array it allocated, exactly once, after `posix_spawn` has returned —
  and the child holds its own independent copy of that memory, so the parent's
  later `free()` calls cannot reach it whatever the timing.

### Reproducing it

Restore either trigger and run:

```sh
swift test --package-path home --filter HostSupervisorTests
```

Deterministic in both directions — roughly 3–5 runs of each configuration is
enough to distinguish them. The crash report lands in
`~/Library/Logs/DiagnosticReports/swiftpm-testing-helper-*.ips`; read the
faulting thread's backtrace rather than trusting the console message.

**Address Sanitizer appears not to be useful here.** `swift test --sanitize=address`
ran clean with a trigger restored, presumably because its instrumentation
perturbs allocation size and timing past whatever layout the defect needs. Note
the asymmetry: that was **a single run**, against every other claim here being
established over 3–5. Worth re-running a few times before concluding ASan is a
dead end. The plain debug build is what reproduces reliably.

### If you are here because it happened again

Re-run the reproduction above on the current toolchain first: if it no longer
reproduces, the workarounds can be retired together and this note deleted. If it
does, this is occurrence three, and the evidence above is close to a filable
Swift issue — runtime function, fatal-error path, source statement, caller,
toolchain version, two independent triggers, three excluded causes.

---

## `AsyncThrowingStream` can rethrow the same error across more than one `next()` call (Swift 6.3.3)

**Status: suspected toolchain quirk, not confirmed.** The upstream Swift issue
tracker has **not been checked** — this is "we did not look", not "we looked
and found nothing", the same standard the entry above holds itself to. Distinct
from that entry: a different type (`AsyncThrowingStream.Continuation` here, vs.
`Task` async-context frames there), a different signature (a repeated non-nil
rethrow, not a fatal `abort`) — not the same defect, only adjacent in this
file.

### Signature

A consumer task already suspended inside a `next()` call at the moment
`AsyncThrowingStream.Continuation.finish(throwing:)` runs can have that
awaited call rethrow the same `Error` again on a later, separate `next()`
call, rather than that later call returning `nil` as the stream's terminal
state would suggest it must. Observed on `Apple Swift version 6.3.3
(swiftlang-6.3.3.1.3 clang-2100.1.1.101)`, `arm64-apple-macosx26.0`.

### Observed

- Once, incidentally, in this codebase, while writing
  `aMismatchedWireVersionRefusesTheConnectionAndNeverDeliversTheFrameThatFollows`
  in `home/Tests/GatewayClientTests/GatewayConnectionTests.swift`: a second
  `iterator.next()` call, made after a first `next()` call already threw the
  wire-version mismatch error, threw that same error again instead of
  returning `nil`.
- Independently and deterministically, 20/20 runs, in a standalone
  reproduction outside this codebase entirely, sharing none of this
  project's code — run by the engineer reviewing this block.

Two independent reproductions, on two different pieces of code, is what earns
this entry a place here rather than being dismissed as one test's fluke.
Neither reproduction was run as a formal multi-trial series inside this
repository the way the entry above was (3–5 runs per configuration); the
20/20 figure belongs to the standalone reproduction, not to anything run
against this codebase's own tests.

### Cost

A test that only asserts *a* `next()` call throws the expected error would
still pass if a later `next()` call went on to deliver a frame that should
never have been surfaced — the exact failure
`aMismatchedWireVersionRefusesTheConnectionAndNeverDeliversTheFrameThatFollows`
exists to catch. Because a second `next()` call cannot be trusted to settle
that either way on this toolchain, that test does not make one: it inspects
`InMemoryGatewayTransport` directly instead, checking that the event enqueued
behind the bad `attached` frame is still sitting there unconsumed — proof the
read loop tore itself down without calling `receive()` again, which is what
"never delivers" actually depends on. See that test's own comment for the
mechanics; it points here rather than restating this.

### If you are here because it happened again

Re-run `aMismatchedWireVersionRefusesTheConnectionAndNeverDeliversTheFrameThatFollows`
a few times on the current toolchain, and if you have it, the standalone
reproduction that produced the 20/20 figure above. If neither reproduces, this
note and the caveat above may be retirable — check first whether anything
still depends on the transport-inspection style this entry justifies before
simplifying that test back to a plain second `next()` assertion.
