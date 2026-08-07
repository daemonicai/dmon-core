import Foundation
import GatewayClient
import Security
#if canImport(Darwin)
import Darwin
#endif

/// The action half of task 6.5's self-provisioning (design D13): generates a token, stores
/// it in the Keychain, then appends `{keyId, name, secretHash, createdAt}` to
/// `devices.json`. `DeviceAuthPolicy` (B8) is the decision half — it names
/// `.keyRequiredButMissing` as the one state that warrants this; this type is the
/// thing that acts on it.
///
/// **Provisioning is unreachable except from that one state, and this is checked here, not
/// merely documented.** `provision()` takes no `DeviceAuthDecision` — `DeviceAuthDecision` is
/// plain data whose cases (`.keyRequiredButMissing` included) any caller can construct
/// without ever running `DeviceAuthPolicy.decide()`, so accepting one as "evidence" would
/// prove nothing on its own. Instead `provision()` re-derives both halves of the precondition
/// itself, against the same `directory` and `secretStore` it was given —
/// `DevicesFileReader.hasActiveEntries()` and `secretStore.load()` — and refuses
/// with `.notRequired`, touching neither the Keychain nor `devices.json`, when either does not
/// hold. This is what makes writing into an empty or absent store — which would switch
/// device-key auth on for every client, as a side effect of this host booting — unreachable
/// through this type: an empty or absent store always fails `hasActiveEntries()`, regardless
/// of what any caller passes or believes the current state to be.
///
/// **Single-writer assumption.** `provision()`'s two precondition checks
/// (`hasActiveEntries()`, `load()`) and `appendEntry`'s own read-then-replace are
/// three separate points in time against `devices.json`, with no locking or compare-and-swap
/// tying them together. `appendEntry` re-reads the file itself immediately before writing, so
/// there is no *stale-copy* bug — what it appends to is whatever the file most recently held,
/// not a copy read earlier in `provision()`. But this type assumes it is the only writer of
/// `devices.json` at a given time. If a second, concurrent writer touched the file between any
/// of these points, that writer's change would be silently overwritten by whichever write
/// lands last — nothing here detects or reports that a concurrent writer existed. This type
/// has no call site yet (wiring one up is a later section's job); whoever adds one is
/// responsible for whether concurrent writers become possible and, if so, for establishing
/// real mutual exclusion rather than relying on this type's ordering.
public struct DeviceKeyProvisioner: Sendable {
    /// Random bytes drawn from `SecRandomCopyBytes` per generated token. 32 bytes (256 bits)
    /// is ample entropy for a bearer token that is never guessed, only ever compared against
    /// a stored hash — far beyond what a length-extension or brute-force attempt could reach.
    static let tokenByteCount = 32

    private let directory: URL
    private let secretStore: any DeviceKeySecretStore
    private let now: @Sendable () -> Date

    public init(
        directory: URL = DevicesFileReader.defaultDirectory,
        secretStore: any DeviceKeySecretStore,
        now: @escaping @Sendable () -> Date = Date.init
    ) {
        self.directory = directory
        self.secretStore = secretStore
        self.now = now
    }

    /// Provisions a new device key secret for this host and returns it. See this type's doc
    /// comment for what makes this unreachable except from the state that warrants it.
    ///
    /// The Keychain write happens *before* the `devices.json` append, deliberately: either
    /// half can fail, and only this ordering leaves the failure visible. Keychain-write
    /// failure leaves both sides untouched — this host holds nothing new and the store gained
    /// no row, indistinguishable from never having attempted provisioning. An append failure
    /// *after* a successful Keychain write leaves this host holding a secret the store has
    /// no record of; `.devicesFileAppendFailed`'s message names the cleanup command
    /// (`KeychainDeviceKeySecretStore.deleteCommand`) so that state is recoverable rather than
    /// silently accumulating an orphaned Keychain item. The alternative ordering — append
    /// first — would instead leave an orphaned *row* in the operator's own `devices.json` on a
    /// Keychain failure, which nothing surfaces and every subsequent boot would attempt again.
    public func provision() async throws -> DeviceKeySecret {
        guard try DevicesFileReader(directory: directory).hasActiveEntries() else {
            throw DeviceKeyProvisioningError.notRequired
        }
        guard try await secretStore.load() == nil else {
            throw DeviceKeyProvisioningError.notRequired
        }

        let secret = DeviceKeySecret(keyId: UUID().uuidString, secret: try Self.generateToken())

        do {
            try await secretStore.store(secret)
        } catch {
            throw DeviceKeyProvisioningError.keychainWriteFailed(message: String(describing: error))
        }

        do {
            try Self.appendEntry(
                keyId: secret.keyId,
                name: Self.deviceName(),
                secretHash: secret.secretHash,
                createdAt: Self.iso8601String(from: now()),
                directory: directory
            )
        } catch {
            throw DeviceKeyProvisioningError.devicesFileAppendFailed(
                keyId: secret.keyId,
                message: """
                This device's key (keyId "\(secret.keyId)") was stored in the \
                Keychain, but appending it to devices.json failed (\(error)). The network \
                host's device store has no record of this key. Remove the orphaned \
                Keychain item with `\(KeychainDeviceKeySecretStore.deleteCommand)` before \
                retrying, or this host will hold a key the store never vouches for.
                """
            )
        }

        return secret
    }

    /// A cryptographically secure, base64-encoded token. `SecRandomCopyBytes` is Apple's
    /// documented CSPRNG entry point ("Generates an array of cryptographically secure random
    /// bytes") — chosen over `Int.random`/`SystemRandomNumberGenerator` precisely because it
    /// carries that documented guarantee explicitly, rather than relying on an unstated one.
    /// Never falls back to a weaker source on failure: a `SecRandomCopyBytes` failure surfaces
    /// as `.tokenGenerationFailed` rather than substituting anything else.
    ///
    /// Standard base64 (RFC 4648 §4 — `A`–`Z`, `a`–`z`, `0`–`9`, `+`, `/`, `=` padding) is a
    /// subset of `b64token`, the character class RFC 6750 §2.1 defines for a Bearer token
    /// (`b64token = 1*( ALPHA / DIGIT / "-" / "." / "_" / "~" / "+" / "/" ) *"="`) — `b64token`
    /// additionally admits `-`, `.`, `_`, `~`, which standard base64 never produces. (RFC 7235
    /// §2.1 defines the same-shaped `token68` for HTTP auth credentials generically; `b64token`
    /// is RFC 6750's own, separately-defined name for what the Bearer scheme accepts.) Because
    /// standard base64's output always falls inside that wider set, the result needs no
    /// escaping to sit in an `Authorization` header. It also, by construction,
    /// contains no space — `DeviceKeyAuthenticator.Authenticate`
    /// (`frontends/Dmon.Network/DeviceKeys/DeviceKeyAuthenticator.cs`) locates the token by
    /// finding the header's first space and taking everything after it as the token, so a
    /// space inside the token would not be rejected by that split itself, but this encoding
    /// avoids the question entirely rather than relying on that detail.
    private static func generateToken() throws -> String {
        var bytes = [UInt8](repeating: 0, count: tokenByteCount)
        let status = SecRandomCopyBytes(kSecRandomDefault, tokenByteCount, &bytes)
        guard status == errSecSuccess else {
            throw DeviceKeyProvisioningError.tokenGenerationFailed(status: status)
        }
        return Data(bytes).base64EncodedString()
    }

    /// A human-meaningful `name` for the appended `devices.json` row — what tells an operator
    /// reading the file which device it is, never a secret. Derived from the machine's
    /// network hostname (`ProcessInfo.processInfo.hostName`, e.g. `"Rens-MacBook-Pro.local"`),
    /// with `"dmon-home"` as a stable fallback if the system ever reports a blank one.
    private static func deviceName() -> String {
        let hostName = ProcessInfo.processInfo.hostName.trimmingCharacters(in: .whitespacesAndNewlines)
        return hostName.isEmpty ? "dmon-home" : hostName
    }

    /// ISO-8601 with an explicit UTC offset (`...Z`), matching the form `DevicesFileFixture`
    /// models and that .NET's `DateTimeOffset` parses — pinned by
    /// `DeviceKeyProvisionerTests.createdAtIsTheExactISO8601FormDateTimeOffsetParses`.
    private static func iso8601String(from date: Date) -> String {
        ISO8601DateFormatter().string(from: date)
    }

    /// Appends one entry to `devices.json`'s `devices` array without decoding the file
    /// through any DTO — `JSONSerialization` into `[String: Any]`, append, re-serialise —
    /// so fields this client does not model (another device's `name`, a future `expiresAt`)
    /// survive untouched. `schemaVersion` is carried forward as read, never reasserted as
    /// `1`. The write is atomic (temp file in the same directory, then
    /// `FileManager.replaceItemAt`) with the final file's permissions set to owner
    /// read/write only.
    private static func appendEntry(
        keyId: String,
        name: String,
        secretHash: String,
        createdAt: String,
        directory: URL
    ) throws {
        let path = directory.appendingPathComponent("devices.json")
        let data = try Data(contentsOf: path)
        guard var root = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw DevicesFileAppendError.malformedEnvelope
        }
        guard var devices = root["devices"] as? [[String: Any]] else {
            throw DevicesFileAppendError.malformedEnvelope
        }

        devices.append([
            "keyId": keyId,
            "name": name,
            "secretHash": secretHash,
            "createdAt": createdAt
        ])
        root["devices"] = devices

        let output = try JSONSerialization.data(withJSONObject: root, options: [.sortedKeys])
        try writeAtomically(output, to: path)
    }

    /// Creates the temp file with `open(2)` directly — `O_CREAT | O_EXCL`, mode `0o600` from
    /// the moment the file exists — rather than `FileManager.createFile` followed by a
    /// separate chmod. Two things follow from that choice: there is no window in which the
    /// temp file exists with broader-than-owner permissions (`createFile`'s own mode is
    /// subject to the process umask until a later `setAttributes` call narrows it, a gap this
    /// has no equivalent of), and a failure carries the real `errno` — disk-full and
    /// permission-denied are distinguishable this way, unlike `createFile`'s bare `Bool`.
    private static func writeAtomically(_ data: Data, to destination: URL) throws {
        let directory = destination.deletingLastPathComponent()
        let tempURL = directory.appendingPathComponent(".devices-\(UUID().uuidString).json.tmp")

        let descriptor = tempURL.path.withCString { open($0, O_CREAT | O_EXCL | O_WRONLY, 0o600) }
        guard descriptor >= 0 else {
            throw DevicesFileAppendError.temporaryFileWriteFailed(errno: errno)
        }

        // Retries both `EINTR` and a short write: a signal arriving mid-`write` cannot
        // corrupt anything here (the swap into `devices.json` only happens after every byte
        // is confirmed written), so looping past it is free correctness, and doing so also
        // subsumes retrying a partial write with no extra cost.
        var totalWritten = 0
        var sawWriteError = false
        var writeErrno: Int32 = 0
        data.withUnsafeBytes { (buffer: UnsafeRawBufferPointer) in
            while totalWritten < buffer.count {
                let result = write(descriptor, buffer.baseAddress!.advanced(by: totalWritten), buffer.count - totalWritten)
                if result >= 0 {
                    totalWritten += result
                    // A zero-byte write with no error is not itself an error, but looping on
                    // it forever would be — treat it the same as a short write and stop.
                    if result == 0 { break }
                    continue
                }
                let currentErrno = errno
                if currentErrno == EINTR { continue }
                sawWriteError = true
                writeErrno = currentErrno
                break
            }
        }
        guard totalWritten == data.count else {
            close(descriptor)
            try? FileManager.default.removeItem(at: tempURL)
            throw DevicesFileAppendError.temporaryFileWriteFailed(errno: sawWriteError ? writeErrno : nil)
        }

        // Unlike `write`, `close` is never retried on `EINTR`. POSIX leaves the descriptor's
        // state unspecified after an interrupted `close`, but on this platform's libc
        // (Darwin's, inherited from FreeBSD) it is deallocated for every error but `EBADF` —
        // see `man 2 close`'s ERRORS list (`EINTR`, `EIO`) and
        // FreeBSD's `close(2)`, which states plainly that "in case of any error except
        // [EBADF], the supplied file descriptor is deallocated and therefore is no longer
        // valid." A retry would not re-attempt this same descriptor; on a system with enough
        // concurrent fd churn it could close a *different* file that has since been handed
        // the same descriptor number by another thread — a worse bug than the one a retry
        // would be trying to fix. So the descriptor is treated as gone the moment `close`
        // returns, success or failure, and failure is reported rather than retried.
        //
        // This is also why a failing `close` cannot be folded into `temporaryFileWriteFailed`:
        // that case means the `write` call itself reported fewer bytes than given, which is a
        // different claim from "every byte was handed to the kernel but committing them to
        // disk — which on filesystems with delayed allocation, e.g. `ENOSPC`, is only
        // detected at `close` time — was not confirmed."
        guard close(descriptor) == 0 else {
            let closeErrno = errno
            try? FileManager.default.removeItem(at: tempURL)
            throw DevicesFileAppendError.temporaryFileCloseUnconfirmed(errno: closeErrno)
        }

        do {
            _ = try FileManager.default.replaceItemAt(destination, withItemAt: tempURL)
        } catch {
            try? FileManager.default.removeItem(at: tempURL)
            throw error
        }

        // `replaceItemAt` is not documented to guarantee the replaced file's permissions
        // come from the temp file rather than the item it replaced, so this is asserted
        // explicitly rather than trusted.
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: destination.path)
    }
}

/// Errors `DeviceKeyProvisioner.provision()` can raise. Never carries a raw token or secret —
/// only a `keyId`, an `OSStatus`, or a message built from non-secret material (see each
/// case's doc comment for exactly what feeds its message).
public enum DeviceKeyProvisioningError: Error, Sendable, Equatable {
    /// The precondition for provisioning does not hold: `devices.json` has no active entries,
    /// or this host already holds a key secret. Neither the Keychain nor `devices.json` was
    /// touched.
    case notRequired
    /// `SecRandomCopyBytes` returned a status other than `errSecSuccess`. Nothing was stored
    /// or appended.
    case tokenGenerationFailed(status: OSStatus)
    /// The Keychain write failed. `devices.json` was not touched — this host holds no
    /// key secret and the store gained no row, the same state as if provisioning had never
    /// been attempted. `message` is `String(describing:)` of the underlying error thrown by
    /// `DeviceKeySecretStore.store(_:)`; both this module's own `KeychainDeviceKeySecretStore`
    /// and its test double never construct an error that embeds the secret, so this cannot
    /// leak one — but a future conformer's error type is not verified here, so it remains this
    /// conformer's obligation to uphold, not something this type enforces.
    case keychainWriteFailed(message: String)
    /// The Keychain write succeeded, but appending the entry to `devices.json` failed. This
    /// host now holds a key secret the store has no record of — `message` names the cleanup
    /// command (`KeychainDeviceKeySecretStore.deleteCommand`) and the affected `keyId`, never
    /// the secret itself.
    case devicesFileAppendFailed(keyId: String, message: String)
}

/// Errors specific to `DeviceKeyProvisioner.appendEntry`'s file-level write, wrapped by
/// `DeviceKeyProvisioningError.devicesFileAppendFailed`'s `message` rather than surfaced
/// directly — the provisioner is `appendEntry`'s only caller.
private enum DevicesFileAppendError: Error {
    /// `devices.json`'s top-level JSON did not decode as an object, or its `devices` field
    /// was not an array of objects.
    case malformedEnvelope
    /// The temporary file this write stages its content in could not be created, or `write` —
    /// after retrying past `EINTR` and any short write — never reached the requested byte
    /// count. `errno` is the underlying POSIX error from `open`/`write` when the failing call
    /// reported one; `nil` covers the one path with no such report — a `write` that returned
    /// success on some but not all of the bytes.
    case temporaryFileWriteFailed(errno: Int32?)
    /// `write` reported every byte accepted, but `close` failed. On a filesystem with delayed
    /// allocation a write error — `ENOSPC` above all — is reported at `close` time rather than
    /// `write` time, so this means the bytes were handed to the kernel but their arrival on
    /// disk was never confirmed; the temp file is not trusted enough to swap into
    /// `devices.json`. `errno` is `close`'s own, not `write`'s.
    case temporaryFileCloseUnconfirmed(errno: Int32)
}
