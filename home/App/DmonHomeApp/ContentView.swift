import GatewayClient
import Supervisor
import SwiftUI

/// A placeholder window proving the app target wires up `Supervisor` and
/// `GatewayClient`. Real supervision, transport and gateway UI land in later
/// sections.
struct ContentView: View {
    private let wireVersion = WireVersion.current
    private let placeholderChildHealth = ChildHealth.unknown

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
