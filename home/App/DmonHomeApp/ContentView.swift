import GatewayClient
import Supervisor
import SwiftUI

/// The app's one window: supervised-child status (task 4.6/4.7's health
/// surface) plus the microphone-authorisation placeholder from an earlier
/// block. Real gateway UI lands in later sections.
struct ContentView: View {
    private let wireVersion = WireVersion.current

    /// Owned by `AppDelegate` and handed down by reference — `ContentView`
    /// never constructs one itself. See `AppObservers`' doc comment for why
    /// that matters.
    let observers: AppObservers
    @State private var microphoneAuthorization = MicrophoneAuthorizationModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        VStack(spacing: 8) {
            Text("dmon-home")
                .font(.title)
            Text("wire protocol \(wireVersion.description)")
                .font(.caption)
                .foregroundStyle(.secondary)

            Divider()
                .padding(.vertical, 4)

            SupervisedChildrenView(statuses: observers.status.statuses)

            Divider()
                .padding(.vertical, 4)

            ChildLogPaneView(statuses: observers.status.statuses, buffers: observers.log.buffers)

            Divider()
                .padding(.vertical, 4)

            GatewaySessionView(snapshot: observers.session.snapshot)

            Divider()
                .padding(.vertical, 4)

            Text("Microphone: \(microphoneAuthorization.status.dmonHomeLabel)")
                .font(.headline)
            Text(microphoneAuthorization.status.dmonHomeGuidance)
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            Button("Request Microphone Access") {
                Task { await microphoneAuthorization.requestAccess() }
            }
            .disabled(microphoneAuthorization.status != .notDetermined)
        }
        .padding()
        .frame(minWidth: 320, minHeight: 200)
        .onChange(of: scenePhase) { _, newPhase in
            if newPhase == .active {
                microphoneAuthorization.refresh()
            }
        }
    }
}

/// One row per supervised child: display name, observed health, and
/// crash/restart state — the "Health is visible" scenario. Pure rendering
/// only; `statuses` already arrives merged from `HostRuntime`.
private struct SupervisedChildrenView: View {
    let statuses: [ChildStatus]

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Supervised Children")
                .font(.headline)
            if statuses.isEmpty {
                Text("Starting…")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(statuses, id: \.id) { status in
                    HStack {
                        Text(status.displayName)
                        Spacer()
                        Text(status.health.dmonHomeLabel)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Text(status.supervision.dmonHomeLabel)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// One scrollable section per supervised child: its captured stdout/stderr,
/// attributed by child (the "Child output is visible" scenario) and
/// retained across a restart by `ChildLogStore` itself, not by anything
/// here — this only renders whatever `buffers` already contains. Pure
/// rendering, same as `SupervisedChildrenView`.
private struct ChildLogPaneView: View {
    let statuses: [ChildStatus]
    let buffers: ChildLogStore.Snapshot

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Output")
                .font(.headline)
            if statuses.isEmpty {
                Text("Starting…")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(statuses, id: \.id) { status in
                            ChildLogSectionView(displayName: status.displayName, buffer: buffers[status.id] ?? .empty)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 160)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct ChildLogSectionView: View {
    let displayName: String
    let buffer: ChildLogBuffer

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(displayName)
                .font(.caption)
                .bold()
            if buffer.droppedCount > 0 {
                Text("… \(buffer.droppedCount) earlier line\(buffer.droppedCount == 1 ? "" : "s") dropped")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            if buffer.lines.isEmpty {
                Text("No output yet")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(buffer.lines) { line in
                    Text("[\(line.source.dmonHomeLabel)] \(line.text)")
                        .font(.system(.caption2, design: .monospaced))
                        .textSelection(.enabled)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// The gateway connection's own state — not connected / connecting / attached (with session
/// id) / dropped (with its cause) / connect failed (with its reason), the "Health is visible"
/// scenario's counterpart for the gateway session itself. Pure rendering only, same as
/// `SupervisedChildrenView`: `snapshot` already arrives decided by `SessionCoordinator`, this
/// view only labels it.
///
/// No connect/reconnect controls here — B4's job, and deliberately not a single "reconnect"
/// button even then: from `.dropped`, `connect()` and `reattach()` are two different verbs
/// (`GatewayConnectionState.dropped`'s own doc comment), and a control added now would collapse
/// that distinction before B4 gets to draw it.
private struct GatewaySessionView: View {
    let snapshot: SessionSnapshot?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Gateway")
                .font(.headline)
            Text(connectionLabel)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var connectionLabel: String {
        (snapshot?.connection ?? .idle).dmonHomeLabel
    }
}

extension GatewayConnectionState {
    var dmonHomeLabel: String {
        switch self {
        case .idle:
            "not connected"
        case .connecting:
            "connecting…"
        case .attached(let sessionId):
            "attached (session \(sessionId))"
        case .dropped(let cause):
            "dropped — \(cause.dmonHomeLabel)"
        case .connectFailed(let failure):
            "connect failed — \(failure.dmonHomeLabel)"
        }
    }
}

extension GatewayConnectionState.DisconnectCause {
    var dmonHomeLabel: String {
        switch self {
        case .closedByPeer(let code, let reason):
            "closed by peer (\(code.dmonHomeLabel)): \(reason)"
        case .closedLocally:
            "closed locally"
        case .streamEnded:
            "stream ended"
        case .other(let message):
            message
        }
    }
}

extension GatewayConnectionState.ConnectFailure {
    var dmonHomeLabel: String {
        switch self {
        case .createRejected(let code, let message):
            "rejected (\(code)): \(message)"
        case .wireVersionMismatch(let message):
            message
        case .closedByPeer(let code, let reason):
            "closed by peer (\(code.dmonHomeLabel)): \(reason)"
        case .other(let message):
            message
        }
    }
}

/// Distinguishes the gateway's close codes rather than collapsing every peer close into
/// "disconnected" — 4409 (superseded by a newer attach), 4404 (unknown session), and 4500
/// (core failure) mean genuinely different things to whoever is reading this.
extension GatewayCloseCode {
    var dmonHomeLabel: String {
        switch self {
        case .normal:
            "normal closure"
        case .messageTooBig:
            "message too big"
        case .protocolViolation:
            "protocol violation"
        case .unknownSession:
            "unknown session"
        case .supersededByNewerAttach:
            "superseded by a newer attach"
        case .coreFailure:
            "core failure"
        case .other(let code):
            "code \(code)"
        }
    }
}

extension ChildLogSource {
    var dmonHomeLabel: String {
        switch self {
        case .standardOutput: "stdout"
        case .standardError: "stderr"
        case .host: "host"
        }
    }
}

extension ChildHealth {
    var dmonHomeLabel: String {
        switch self {
        case .unknown: "unknown"
        case .healthy: "healthy"
        case .unhealthy: "unhealthy"
        }
    }
}

extension ChildSupervisionState {
    var dmonHomeLabel: String {
        switch self {
        case .normal: "normal"
        case .restarting: "restarting"
        case .repeatedFailure: "repeated failure"
        case .stoppedIntentionally: "stopped"
        }
    }
}
