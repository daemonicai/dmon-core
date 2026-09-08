# First-run provisioning races the gateway's device-store reload

**Status:** open
**Where:** gateway — `frontends/Dmon.Network/NetworkConnectionEndpoint.cs` and
`DeviceKeyStoreWatcher`; client — `Sources/DeviceKeys/DeviceKeyProvisioner.swift` (`provision`)
and `Sources/DeviceKeys/AuthenticatedTransportFactory.swift`, **in the separate
`daemonicai/dmon-home` repository**
**Surfaced:** 2026-09-07, running Phase 1 of `dmon-home`'s `VERIFICATION-NOTES.md` for the first time
**Severity:** high — it is the *first* launch on a new device that fails, and the failure names nothing

## What

A device with no stored credential provisions one and connects immediately. Provisioning
appends a row to `~/.dmon/network/devices.json`; the gateway learns about that row only when
its `DeviceKeyStoreWatcher` notices the file changed. The client does not wait for that, and
the upgrade arrives while the gateway still holds the pre-provisioning credential set.

Observed ordering, from one gateway log:

```
GET /ws → 401   WebSocket upgrade rejected: missing or mismatched Authorization header.
                ↓ then, and only then
                Device-key store reloaded from '…/devices.json': 2 active credential(s).
```

The client makes **exactly one** `/ws` attempt and never retries, so the connection stays
failed even though the credential became valid moments later. In the app this renders as
`connect failed — NSURLErrorDomain Code=-1011 "There was a bad response from the server."`,
which is URLSession's report of a non-101 upgrade and says nothing about credentials.

Relaunching works, which is why the window is easy to miss: by then the watcher has caught up.

## Why it matters

This is the new-device path — the only path a genuinely new device can take. It fails on the
first attempt, recovers only by accident of the user trying again, and gives neither side's
log a line naming the real cause. The gateway's message is *accurate but misleading*: the
header was neither missing nor mismatched, it was for a `keyId` the gateway had not read yet.

No test on either side can see this. It needs both processes and a stopwatch, which is exactly
the class of gap `VERIFICATION-NOTES.md` exists to cover — and it is a gap that document did
not anticipate.

## Evidence, and its limits

**Verified:** the log ordering above; the row written at `2026-09-07T14:11:55Z`; a single
`/ws` attempt with no retry; and — the discriminating retest — that the *same* credential
produced a clean `101` on the next launch, once against a warm watcher and once against a
gateway that read the store fresh at startup. That excludes a credential-contract mismatch as
the cause, which was the competing hypothesis.

**Not verified:** reproducibility. There is only one cold-provisioning opportunity per
machine state, and it was spent. Clearing the Keychain item and the provisioned row would
create another. The mechanism is strongly supported but has been observed **once**.

## What to do

Two shapes, and they are not equivalent:

1. **Client retries once** after a 401 that follows its own provisioning. Cheap, local, but
   it encodes a timing assumption rather than removing one.
2. **Gateway re-reads the store before rejecting an unknown `keyId`.** Removes the assumption
   entirely and fixes it for every future client, not just this one. But it touches the
   fail-closed auth path, so it needs care: a re-read must not become a way to make the
   gateway do filesystem work on any unauthenticated request.

(2) is the better fix and the more dangerous one. Worth an explicit decision rather than a
drive-by.

**Decided 2026-09-08 by the Product Owner, as ADR-038's first open question: (2), the
gateway-side fix.** That is why this note stayed in `dmon-core` when the rest of the `home/`
register moved to `daemonicai/dmon-home` — the repository that owns the fix owns the note.
The subject still straddles the boundary, so whoever takes it should expect to *verify*
across both repositories even though they will only *edit* in this one.

## Related

The same run turned up a separate, non-defect footgun — a `Dmon.cs` in the gateway's working
directory silently switches core resolution to compile-from-source and times out the
handshake. That one is recorded in `dmon-home`'s `VERIFICATION-NOTES.md`, not here, because the code
behaves as designed.
