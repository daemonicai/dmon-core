import GatewayClient

/// Thrown by `AuthenticatedTransportFactory.makeTransport()` when `DeviceAuthPolicy.decide()`
/// names one of its three refusals (`.keyRevoked`, `.keyUnknownToStore`, `.secretMismatch`)
/// rather than something to connect with. `message` is the decision's own message, passed
/// through unchanged — this type never rewrites, appends to, or strips it, so whichever
/// escape hatch `DeviceAuthPolicy` already named (and whichever redaction discipline
/// produced it — see `DeviceAuthDecision`'s doc comment) stays intact for whoever surfaces
/// this to an operator, which is not this type's job.
public struct DeviceAuthConnectionRefused: Error, Sendable, Equatable {
    public let message: String
}

/// Resolves what to present on connect (`DeviceAuthPolicy`), provisioning a fresh credential
/// when one is required and this host holds none (`DeviceKeyProvisioner`), and returns a
/// `GatewayTransport` carrying the right `Authorization` header — or throws
/// `DeviceAuthConnectionRefused` naming why, for the one outcome that must never happen
/// silently: presenting a credential the store no longer vouches for.
///
/// This is the connect flow the spec's device-key requirement names but no earlier block
/// could build: `DeviceAuthDecision`'s five (now six) states are pure data until something
/// actually turns one into a transport. That something is here, and only here — see this
/// type's placement below for why it cannot live in `GatewayClient` instead.
///
/// **Never falls back to `.connectUnauthenticated` on a refusal.** `.keyRevoked`,
/// `.keyUnknownToStore`, and `.secretMismatch` all `throw`; none of the three switch arms
/// below ever calls `buildTransport(credential:)` with `nil` to route around them. A refusal
/// that degraded into "connect anyway, without a key" would recreate the exact silent-401
/// failure `DeviceAuthDecision`'s states exist to prevent — the store would reject the
/// connection downstream, but with nothing here having said why.
///
/// **Placement — `DeviceKeys`, never `GatewayClient`.** `DeviceAuthPolicy` and
/// `DeviceKeyProvisioner` read `devices.json` and the Keychain; `GatewayClient` is written to
/// extract onto iOS unmodified (design D16), and `make dmon-home-ios-check` builds only the
/// `GatewayClient` scheme. A join of the two that lived in `GatewayClient` would drag
/// `devices.json`/Keychain access into that build graph. This type depends on `GatewayClient`
/// (for `GatewayTransport`, `GatewayEndpoint`, `WebSocketGatewayTransport`) in the one
/// direction that dependency is allowed to run, and `GatewayClient` depends on nothing here.
public struct AuthenticatedTransportFactory: Sendable {
    private let endpoint: GatewayEndpoint
    private let policy: DeviceAuthPolicy
    private let provisioner: DeviceKeyProvisioner
    private let buildTransport: @Sendable (GatewayEndpoint) -> any GatewayTransport

    /// - Parameter buildTransport: Builds the transport from the fully-resolved endpoint
    ///   (base `endpoint`'s URL, with `Authorization` decided by `GatewayEndpoint
    ///   .headers(for:additionalHeaders:)`, the one place that decides whether that header
    ///   exists at all). Defaults to `WebSocketGatewayTransport.init(endpoint:)`, the
    ///   production conformer; injectable so a caller — `AuthenticatedTransportFactoryTests`
    ///   included — can verify which headers a given `DeviceAuthDecision` produced without a
    ///   live socket, the same dependency-injection shape `GatewaySession.init(makeTransport:)`
    ///   already uses for the same reason.
    public init(
        endpoint: GatewayEndpoint,
        policy: DeviceAuthPolicy,
        provisioner: DeviceKeyProvisioner,
        buildTransport: @escaping @Sendable (GatewayEndpoint) -> any GatewayTransport = { WebSocketGatewayTransport(endpoint: $0) }
    ) {
        self.endpoint = endpoint
        self.policy = policy
        self.provisioner = provisioner
        self.buildTransport = buildTransport
    }

    /// Resolves `policy.decide()` and turns the result into a transport, or throws.
    /// Suitable directly as `GatewaySession.init(makeTransport:)`'s closure — re-invoked on
    /// every fresh connection that initialiser makes, so a credential revoked between one
    /// connection and the next is re-checked here rather than re-presented from a value
    /// captured once (see `GatewaySession`'s own doc comment on why its `makeTransport` is
    /// `async throws` for exactly this reason).
    ///
    /// - `.connectUnauthenticated`: builds a transport with no `Authorization` header at
    ///   all — `credential: nil` below, not an empty or placeholder value.
    /// - `.presentKey(let secret)`: builds a transport with `Authorization: Bearer
    ///   <secret.secret>`.
    /// - `.keyRequiredButMissing`: provisions a new credential (`provisioner.provision()`),
    ///   then presents the one it returns. A `DeviceKeyProvisioningError` from that call
    ///   propagates unchanged — this type adds no interpretation of its own.
    /// - `.keyRevoked` / `.keyUnknownToStore` / `.secretMismatch`: throws
    ///   `DeviceAuthConnectionRefused` carrying the decision's own `message`. Never reaches
    ///   `buildTransport(_:)`.
    public func makeTransport() async throws -> any GatewayTransport {
        switch try await policy.decide() {
        case .connectUnauthenticated:
            return transport(presenting: nil)
        case .presentKey(let secret):
            return transport(presenting: secret)
        case .keyRequiredButMissing:
            let provisioned = try await provisioner.provision()
            return transport(presenting: provisioned)
        case .keyRevoked(let message), .keyUnknownToStore(let message), .secretMismatch(let message):
            throw DeviceAuthConnectionRefused(message: message)
        }
    }

    private func transport(presenting credential: DeviceKeySecret?) -> any GatewayTransport {
        let headers = GatewayEndpoint.headers(for: credential, additionalHeaders: endpoint.headers)
        return buildTransport(GatewayEndpoint(url: endpoint.url, headers: headers))
    }
}
