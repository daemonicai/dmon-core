# dmon-home — live verifications owed

Things that **cannot** be proven by `swift test` and need a person at a keyboard.
Work top to bottom; each phase depends on the one before it.

Deferred with the Product Owner's agreement during `dmon-home-foundations` §6, slipped to §7,
then §8 (which finally gave them a call site), and still unrun when that change shipped.
Nothing here blocks the merged change — but the auth path is **unproven end to end** until
this is run.

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

- [ ] `make dmon-home-app` — builds to `home/.build-xcode/Build/Products/Release/DmonHomeApp.app`
- [ ] `ndmon` present at `~/.dotnet/tools/ndmon` (else `make network`)
- [ ] `~/.dmon/network/devices.json` exists and is **non-empty** — an empty store on a loopback
      bind connects without a key, which skips the entire path under test
- [ ] Clear any existing credential so this tests provisioning rather than a warm cache:

      security delete-generic-password -a default -s ai.daemonic.dmon-home.device-credential

      (Errors harmlessly if no item exists.)

## Phase 1 — provision and connect  → proves ②, and the *write* half of ①

- [ ] Start `ndmon`
- [ ] Launch the app: `open home/.build-xcode/Build/Products/Release/DmonHomeApp.app`
      It connects on its own once the gateway child first reports healthy — there is no button.

**Expect:** a Keychain prompt · a **new row appended** to `~/.dmon/network/devices.json` · the
gateway row reaching connected.

**Failure looks like:** a 401 with nothing naming the cause. That is proof ② failing — the
two-language contract disagrees. Check field names and the hex digest first; they are the
likeliest divergence and the ones no test compares.

## Phase 2 — the restart proof  → proves ①

- [ ] Quit the app
- [ ] Relaunch it **without** clearing the Keychain

**Expect:** **no** new row in `devices.json`, and it connects reusing the stored secret.

This is the whole point of ①: the write happened in a process that is now dead, the read in a
live one. A pass here is the only evidence the Keychain path works at all.

**Failure looks like:** a second row appended (the read found nothing, so it re-provisioned) —
the round trip is broken even though every unit test passes.

## Phase 3 — revocation refuses by name

- [ ] Remove or deactivate that `keyId` in `~/.dmon/network/devices.json`
- [ ] Relaunch

**Expect:** a refusal that **names the reason**, not a bare 401.

## Phase 4 — optional, and out of scope for the above

The **`secretHash` half** of the credential-mismatch gap is still open. A credential whose
`keyId` is *active* but whose `secretHash` in the file has been **rotated or restored** still
returns `.presentCredential` and 401s at connect with nothing naming the cause — the exact
failure the five outcomes exist to prevent. §6's B8 closed the **`keyId`** half only; do not let
the record read as though it finished the job.

- [ ] Rotate the `secretHash` in `devices.json` while leaving the `keyId` active; relaunch

Closing it is *a comparison, not a design*: `status(ofKeyId:)` already has `entry.secretHash` in
hand and `DeviceCredential.secretHash` is available.

---

## Three things that read as bugs and are not

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
