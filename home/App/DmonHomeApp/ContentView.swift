import GatewayClient
import Supervisor
import SwiftUI

/// A placeholder window proving the app target wires up `Supervisor` and
/// `GatewayClient`. Real supervision, transport and gateway UI land in later
/// sections.
struct ContentView: View {
    private let wireVersion = WireVersion.current
    private let placeholderChildHealth = ChildHealth.unknown
    @State private var microphoneAuthorization = MicrophoneAuthorizationModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        VStack(spacing: 8) {
            Text("dmon-home")
                .font(.title)
            Text("wire protocol \(wireVersion.description) · supervisor \(placeholderChildHealth.rawValue)")
                .font(.caption)
                .foregroundStyle(.secondary)

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
