# dmon-home — live verifications owed

Things that **cannot** be proven by `swift test` and need a person at a keyboard.
Work top to bottom; each phase depends on the one before it.

Deferred with the Product Owner's agreement during `dmon-home-foundations` §6, slipped to §7,
then §8 (which finally gave them a call site), and still unrun when that change shipped.
Nothing here blocks the merged change — but the auth path is **unproven end to end** until
this is run.

---

## Outcome — 2026-09-07

**All four phases are done. ① and ② are both proven, and Phase 4's gap turned out to be already
closed.** Nothing in this file is now owed.

The gateway logged `Request finished GET /ws — 101` and
`Session 87eef247-2c85-44f3-a6f3-d4bc20370054 created and registered`, with the core attaching
in 410ms. `devices.json` never grew past the one provisioned row across three app processes
and two gateway processes, so the Keychain secret was read back by processes that did not
write it. ② held across two independent gateway starts — one that learned the credential via
the file watcher, one that read the store fresh at startup.

Two things the checklist did not anticipate:

- **Both binaries were stale and the checklist did not catch it.** `ndmon` was six weeks
  behind (`0.2.0-alpha.0.259`, predating the change's own gateway edits) and the app bundle
  predated the merge. "`ndmon` present … else `make network`" reads as satisfied when the
  binary is merely *there*. **Rebuild both before running any of this**, regardless of what
  is on disk.
- **Phase 1 failed the first time, and the failure was real.** Provisioning races the
  gateway's device-store reload: the client writes its row, connects before the watcher has
  reloaded, gets a 401, and never retries. Recorded as
  [`tech-debt/provisioning-races-device-store-reload.md`](../tech-debt/provisioning-races-device-store-reload.md).
  Phase 1 was only re-provable because the relaunch in Phase 2 doubled as the discriminating
  retest — the same credential succeeding once the store was loaded is what excluded a
  contract mismatch.

Also worth knowing: the *absence* of a Keychain prompt in Phase 1 is correct, not a skipped
step. `SecItemAdd` by the app that owns the item does not prompt; only a cross-binary read
does.

---

## Why these two, and why no test closes them

**① The real Keychain path.** `SecItemAdd` / `SecItemCopyMatching` / `SecItemUpdate` and the
`AnyObject → Data` cast are never executed by `swift test` — deliberately, because CI must not
touch the Keychain. What is untested is not the logic but the **round trip across a process
restart**: written by one process, read back by a later one. No in-memory conformer stands in
for that.

**② Whether `Dmon.Network` accepts a credential this client provisioned.** The whole
two-language contract — hex digest, exact field names, timestamp format, header shape. **No
Swift test can close it**, because the party that must agree is a .NET process. Both sides were
traced independently and believed compatible; that is reasoning from reading, not evidence. A
golden-file test was considered and **rejected as a false guard** — nothing would regenerate it,
so a Swift-side rename would leave it passing against a file no longer representative.

---

## Setup

- [x] `make dmon-home-app` — builds to `home/.build-xcode/Build/Products/Release/DmonHomeApp.app`
- [x] `ndmon` present at `~/.dotnet/tools/ndmon` (else `make network`)
- [x] `~/.dmon/network/devices.json` exists and is **non-empty** — an empty store on a loopback
      bind connects without a key, which skips the entire path under test
- [x] Clear any existing credential so this tests provisioning rather than a warm cache:

      security delete-generic-password -a default -s ai.daemonic.dmon-home.device-credential

      (Errors harmlessly if no item exists.)

## Phase 1 — provision and connect  → proves ②, and the *write* half of ①

- [x] Start `ndmon`
- [x] Launch the app: `open home/.build-xcode/Build/Products/Release/DmonHomeApp.app`
      It connects on its own once the gateway child first reports healthy — there is no button.

**Expect:** a Keychain prompt · a **new row appended** to `~/.dmon/network/devices.json` · the
gateway row reaching connected.

**Failure looks like:** a 401 with nothing naming the cause. That is proof ② failing — the
two-language contract disagrees. Check field names and the hex digest first; they are the
likeliest divergence and the ones no test compares.

## Phase 2 — the restart proof  → proves ①

- [x] Quit the app
- [x] Relaunch it **without** clearing the Keychain

**Expect:** **no** new row in `devices.json`, and it connects reusing the stored secret.

This is the whole point of ①: the write happened in a process that is now dead, the read in a
live one. A pass here is the only evidence the Keychain path works at all.

**Failure looks like:** a second row appended (the read found nothing, so it re-provisioned) —
the round trip is broken even though every unit test passes.

## Phase 3 — revocation refuses by name

- [x] Remove or deactivate that `keyId` in `~/.dmon/network/devices.json`
- [x] Relaunch

**Expect:** a refusal that **names the reason**, not a bare 401.

**Ran 2026-09-07 — passes.** The app showed *"this device's stored key (…) has been revoked by
the network host"*. The gateway log recorded **no `/ws` attempt at all**: `DeviceAuthPolicy`
reads `devices.json` itself and refuses pre-flight, which is *why* it can name the reason —
over the wire the gateway could only ever have returned a bare 401.

**Read this before running it again:** "remove or deactivate" is not a free choice. `IsEmpty`
on the gateway's `DeviceKeySet` is computed over **active** entries, and an empty set means
*auth disabled — authorize every connection*. Revoking or deleting the only row therefore
**connects** instead of refusing, and reads as a pass when nothing was tested. Keep a second
active filler row in the store for the duration of this phase, and delete it afterwards.

## Phase 4 — the `secretHash` half  → **verified closed, 2026-09-07**

- [x] Rotate the `secretHash` in `devices.json` while leaving the `keyId` active; relaunch

**This phase's premise was stale, and the run is what settled it.** The text below described
the gap as open; it is not. `DeviceAuthPolicy`'s `.active(let storedSecretHash)` branch guards
on `storedSecretHash == secret.secretHash` and returns `.secretMismatch` with a message naming
both rotation and backup-restore as causes.

Verified live, not by reading: with the `keyId` active and the `secretHash` set to a rotated
value, the app refused **pre-flight** with the `.secretMismatch` message. The gateway log
recorded **no `/ws` attempt**, and no new row was provisioned. A `.presentCredential` outcome —
what the old text predicted — would have dialled out and returned a bare 401, which the gateway
log would have shown.

> ~~The **`secretHash` half** of the credential-mismatch gap is still open. A credential whose
> `keyId` is *active* but whose `secretHash` in the file has been **rotated or restored** still
> returns `.presentCredential` and 401s at connect with nothing naming the cause — the exact
> failure the five outcomes exist to prevent. §6's B8 closed the **`keyId`** half only; do not
> let the record read as though it finished the job.~~
>
> ~~Closing it is *a comparison, not a design*: `status(ofKeyId:)` already has
> `entry.secretHash` in hand and `DeviceCredential.secretHash` is available.~~

Struck rather than deleted: the provenance is the point. Someone closed this and the note that
called it open was never updated, which is exactly the failure mode this file exists to avoid.

**But the message does not reach the user.** The UI truncates it to its first line, cutting it
mid-sentence and discarding the remedy — the `security delete-generic-password` command that
tells the operator how to recover. All three outcome messages carry that remedy in their tail.
Filed as
[`tech-debt/auth-failure-messages-truncated-in-ui.md`](../tech-debt/auth-failure-messages-truncated-in-ui.md).

---

## Four things that read as bugs and are not

0. **Never start `ndmon` from a directory containing a `Dmon.cs`** — the repo root has one.
   `CoreResolver` tier 1 treats a `Dmon.cs` in the working directory as the core and switches
   to **compile-from-source**, which cannot finish inside the 30s session handshake. It
   surfaces as `rejected (core_timeout): The core did not complete the session handshake
   within 30s`, which names nothing about why, and it cost a real detour on 2026-09-07. Start
   it from a neutral cwd — or set `DMON_CORE_PATH` at a prebuilt core:

       cd ~ && DMON_CORE_PATH=<repo>/build/dmoncore/dmoncore.dll ndmon

   With the prebuilt core the same create attached in 410ms. Note this bites only when *you*
   start the gateway by hand: the app's own `.adoptOrSpawn` gives it a cwd of `/`, where tier
   1 never fires. The code is behaving as designed — this is a footgun, not a defect.


1. **A Keychain prompt on every rebuild is expected.** `KeychainDeviceKeySecretStore` sets
   neither `kSecAttrAccessible` nor `kSecUseDataProtectionKeychain`, and the app is **ad-hoc
   signed**, so the ACL does not survive a new binary.
2. **Rebuilding also discards the TCC microphone grant** — TCC keys on bundle id **plus**
   cdhash — so a fresh microphone prompt is expected too.
3. **A GUI-launched app does not inherit your shell environment.** If you need
   `DMON_NETWORK_PATH`, launch the binary *inside* the bundle directly
   (`…/DmonHomeApp.app/Contents/MacOS/DmonHomeApp`) — but that costs the TCC grant, which keys
   on the bundle, not the binary.

## Also unresolved, and unreachable until there is a concurrent caller

Whether the eventual caller must **serialise provisioning attempts**. `provision()`'s two
precondition checks and `appendEntry`'s read-then-replace are **three separate points in time
against one file with no locking**; a concurrent writer's change would be silently overwritten
and nothing would detect it. Written down because it is invisible while there is only one
caller.

---

*Recorded 2026-08-11, from `dmon-home-foundations`' DEVLOG before it archives. This file lives
in `home/` deliberately: it travels with `dmon-home` when it moves to its own repo.*
