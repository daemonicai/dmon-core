import GatewayClient
import Supervisor
import SwiftUI

/// The app's one window: supervised-child status (task 4.6/4.7's health
/// surface) plus the microphone-authorisation placeholder from an earlier
/// block. Real gateway UI lands in later sections.
struct ContentView: View {
    private let wireVersion = WireVersion.current

    /// Owned by `AppDelegate` and handed down by reference — `ContentView`
    /// never constructs one itself. See `ChildStatusObserver`'s doc comment
    /// for why that matters.
    let statusObserver: ChildStatusObserver
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

            SupervisedChildrenView(statuses: statusObserver.statuses)

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

extension ChildHealth {
    var dmonHomeLabel: String {
        switch self {
        case .unknown: "unknown"
        case .starting: "starting"
        case .healthy: "healthy"
        case .unhealthy: "unhealthy"
        case .stopped: "stopped"
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
