import GatewayClient
import Power
import Supervisor
import SwiftUI

/// A placeholder window proving the app target wires up `Supervisor`,
/// `GatewayClient` and `Power`. Real supervision, transport and gateway UI
/// land in later sections.
struct ContentView: View {
    private let wireVersion = WireVersion.current
    private let placeholderChildHealth = ChildHealth.unknown
    private let placeholderActivityAssertion = ActivityAssertion(
        options: [.userInitiated],
        reason: "dmon-home placeholder window"
    )

    var body: some View {
        VStack(spacing: 8) {
            Text("dmon-home")
                .font(.title)
            Text("wire protocol \(wireVersion.description) · supervisor \(placeholderChildHealth.rawValue)")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding()
        .frame(minWidth: 320, minHeight: 200)
    }
}
