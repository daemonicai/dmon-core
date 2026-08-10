import GatewayClient
import Supervisor
import SwiftUI

/// The app's one window: supervised-child status (task 4.6/4.7's health
/// surface), the gateway session's connection state and transcript (tasks
/// 8.1/8.2), plus the microphone-authorisation placeholder from an earlier
/// block.
struct ContentView: View {
    private let wireVersion = WireVersion.current

    /// Owned by `AppDelegate` and handed down by reference — `ContentView`
    /// never constructs one itself. See `AppObservers`' doc comment for why
    /// that matters.
    let observers: AppObservers

    /// Owned by `AppDelegate` and handed down by reference, the same as
    /// `observers` — a view calls its `submit(_:)`/`connect()`/`reattach()`
    /// directly rather than duplicating any of the decisions those methods
    /// already make.
    let coordinator: SessionCoordinator
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

            GatewaySessionView(snapshot: observers.session.snapshot, coordinator: coordinator)

            Divider()
                .padding(.vertical, 4)

            TranscriptView(entries: observers.session.snapshot?.transcript.entries ?? [])

            TurnInputView(coordinator: coordinator)

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
/// scenario's counterpart for the gateway session itself, plus the two connect/reattach
/// controls B4 adds. Pure rendering only, same as `SupervisedChildrenView`: `snapshot` already
/// arrives decided by `SessionCoordinator`, this view only labels it and forwards a tap to the
/// coordinator method that same state already makes legal.
private struct GatewaySessionView: View {
    let snapshot: SessionSnapshot?
    let coordinator: SessionCoordinator

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Gateway")
                .font(.headline)
            Text(connectionLabel)
                .font(.caption)
                .foregroundStyle(.secondary)
            GatewayConnectionControls(connectionState: connectionState, coordinator: coordinator)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var connectionState: GatewayConnectionState {
        snapshot?.connection ?? .idle
    }

    private var connectionLabel: String {
        connectionState.dmonHomeLabel
    }
}

/// Two distinct verbs, never collapsed into one "reconnect" button — see
/// `GatewayConnectionState.dropped`'s own doc comment for why `connect()` (a brand-new session,
/// discarding any resumable one) and `reattach()` (resumes the existing one) mean different
/// things, and must never be offered as if they were interchangeable. Each button's visibility
/// reads `GatewayConnectionState.allowsConnect`/`allowsReattach` directly — the same properties
/// `SessionCoordinator.connect()`/`reattach()` guard on themselves — rather than a second,
/// view-local copy of that legality, so this view never offers a verb the coordinator would
/// silently no-op on, and can never drift from it. No disconnect control —
/// `SessionCoordinator.close()`'s own doc comment: it is terminal, and there is no "hang up and
/// come back" verb to wire one to.
private struct GatewayConnectionControls: View {
    let connectionState: GatewayConnectionState
    let coordinator: SessionCoordinator

    var body: some View {
        HStack(spacing: 8) {
            if connectionState.allowsConnect {
                Button("New Session") {
                    Task { await coordinator.connect() }
                }
            }
            if connectionState.allowsReattach {
                Button("Resume Session") {
                    Task { await coordinator.reattach() }
                }
            }
        }
    }
}

/// The transcript half of tasks 8.1/8.2: one row per `TranscriptEntry`, scrolled to the newest
/// as content streams in. Pure rendering only — `entries` already arrives folded by
/// `TurnTranscript`, this view only lays it out.
private struct TranscriptView: View {
    let entries: [TranscriptEntry]

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Conversation")
                .font(.headline)
            if entries.isEmpty {
                Text("No messages yet")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 6) {
                            ForEach(entries) { entry in
                                TranscriptEntryRow(entry: entry)
                                    .id(entry.id)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(maxHeight: 220)
                    // Fires on every fold — a brand-new entry or a streamed delta appended to
                    // the existing last one both produce a new `entries` value (`TranscriptEntry`
                    // is a value type), so this alone covers both "a new row arrived" and "the
                    // last row grew" without a second, separate observer.
                    .onChange(of: entries) { _, newEntries in
                        guard let newestID = newEntries.last?.id else { return }
                        proxy.scrollTo(newestID, anchor: .bottom)
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct TranscriptEntryRow: View {
    let entry: TranscriptEntry

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 4) {
                Text(entry.role.dmonHomeLabel)
                    .font(.caption2)
                    .bold()
                Text(entry.state.dmonHomeLabel)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            Text(entry.text.isEmpty ? "…" : entry.text)
                .font(entry.role == .assistant ? .system(.body, design: .monospaced) : .body)
                .textSelection(.enabled)
            if let detail = entry.additionalFailureDetail {
                Text(detail)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// The input half of tasks 8.1/8.2. **Never disabled on an in-flight turn or an unattached
/// session** — see this change's own B4 brief: an open turn renders as awaiting via the
/// transcript, not as a reason to block further typing, and the unattached case must reach
/// `coordinator.submit(_:)` so `GatewaySession.submitTurn(_:)`'s own `.notAttached` refusal (task
/// 8.2's scenario) actually fires and gets rendered, rather than being pre-empted here by a
/// second, view-side copy of that same check. Ignoring a blank/whitespace-only draft is ordinary
/// input validation, not an attach-state gate, and stays.
private struct TurnInputView: View {
    let coordinator: SessionCoordinator
    @State private var draft = ""

    var body: some View {
        HStack(spacing: 8) {
            TextField("Message", text: $draft)
                .textFieldStyle(.roundedBorder)
                .onSubmit(submit)
            Button("Send", action: submit)
                .disabled(trimmedDraft.isEmpty)
        }
    }

    private var trimmedDraft: String {
        draft.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func submit() {
        let message = trimmedDraft
        guard !message.isEmpty else { return }
        draft = ""
        Task { await coordinator.submit(message) }
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

extension TranscriptEntry.Role {
    var dmonHomeLabel: String {
        switch self {
        case .user: "You"
        case .assistant: "dmon"
        case .notice: "Notice"
        }
    }
}

/// A short tag for every case — deliberately not the full failure/refusal reason, which the row
/// gets from `entry.text` (for a `.notice` entry) or `TranscriptEntry.additionalFailureDetail`
/// (for an assistant entry) instead. See that property's own doc comment for exactly which case
/// needs which.
extension TranscriptEntry.State {
    var dmonHomeLabel: String {
        switch self {
        case .complete: "sent"
        case .awaitingResponse: "awaiting response…"
        case .streaming: "streaming…"
        case .ended: "done"
        case .failed: "failed"
        case .refused: "refused"
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
