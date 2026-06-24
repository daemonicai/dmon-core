import SwiftUI

// MARK: - Section enum

/// Ordered sidebar sections for the dashboard window.
/// `CaseIterable` drives the sidebar list; adding a future section is a one-liner here.
enum DashboardSection: String, Hashable, CaseIterable, Identifiable {
    case status   = "Status"
    case services = "Services"
    case settings = "Settings"

    var id: Self { self }

    var systemImage: String {
        switch self {
        case .status:   return "heart.text.square"
        case .services: return "server.rack"
        case .settings: return "gear"
        }
    }
}

// MARK: - DashboardView

/// Primary window body. `NavigationSplitView` with a fixed sidebar and a detail pane.
///
/// Selection state lives on `DaemonController.selectedSection` so every entry point
/// (sidebar click, ⌘, keyboard shortcut, menu-bar button) drives the same published
/// property — no duplicated state, no dead code paths.
///
/// The controller and its children are read-only here. Bootstrap/registration is owned
/// exclusively by `AppDelegate.applicationDidFinishLaunching` — no `.task`/`.onAppear`
/// wiring is permitted in this view or its children (D1/D2).
struct DashboardView: View {

    @EnvironmentObject private var controller: DaemonController

    var body: some View {
        NavigationSplitView {
            List(DashboardSection.allCases, selection: Binding(
                get: { controller.selectedSection },
                set: { if let v = $0 { controller.selectedSection = v } }
            )) { section in
                Label(section.rawValue, systemImage: section.systemImage)
                    .tag(section)
            }
            .navigationSplitViewColumnWidth(min: 160, ideal: 180)
        } detail: {
            switch controller.selectedSection {
            case .status:
                StatusSectionView()
                    .environmentObject(controller.healthRegistry)
            case .services:
                ServicesSectionView()
                    .environmentObject(controller.gateway)
                    .environmentObject(controller.dcal)
                    .environmentObject(controller.dmail)
                    .environmentObject(controller.tailscale)
                    .environmentObject(controller.calendarSync)
            case .settings:
                SettingsView()
                    .environmentObject(controller.gateway)
            }
        }
        .navigationTitle(controller.selectedSection.rawValue)
        .frame(minWidth: 680, minHeight: 440)
        .toolbar {
            ToolbarItem(placement: .automatic) {
                RollupStatusIndicator(rollup: controller.healthRegistry.rollupColor)
            }
        }
    }
}

// MARK: - RollupStatusIndicator

/// Toolbar indicator that reflects the aggregate rollup colour.
/// A pure read of `controller.healthRegistry.rollupColor` — no side effects, no
/// bootstrap calls, no `.task`/`.onAppear` wiring (D1/D2 safe).
private struct RollupStatusIndicator: View {
    let rollup: RollupColor

    var body: some View {
        let presentation = rollupPresentation(rollup)
        HStack(spacing: 4) {
            Circle()
                .fill(rollupColor(rollup))
                .frame(width: 10, height: 10)
            Text(presentation.label)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .accessibilityLabel(Text("System status: \(presentation.label)"))
    }
}

// MARK: - StatusSectionView

/// Health grid: one row per `HealthRegistry` component showing status, name, detail,
/// and a relative last-updated time.
struct StatusSectionView: View {

    @EnvironmentObject private var healthRegistry: HealthRegistry

    // Timer drives periodic re-render so relative ages stay fresh.
    private let timer = Timer.publish(every: 10, on: .main, in: .common).autoconnect()
    @State private var now: Date = Date()

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if healthRegistry.components.isEmpty {
                ContentUnavailableView("No components registered", systemImage: "heart.slash")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    // Key by name (unique stable component identifier) so row diffing
                    // is correct if the registry order ever changes.
                    ForEach(healthRegistry.components, id: \.name) { component in
                        ComponentHealthRow(component: component, now: now)
                    }
                }
                .listStyle(.inset)
            }
        }
        .onReceive(timer) { tick in now = tick }
        .navigationTitle("Status")
    }
}

// MARK: - ComponentHealthRow

private struct ComponentHealthRow: View {
    let component: ComponentHealth
    let now: Date

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: statusSymbol(component.status))
                .foregroundStyle(statusColor(component.status))
                .frame(width: 20)

            VStack(alignment: .leading, spacing: 2) {
                Text(component.name)
                    .font(.body)
                if let detail = component.detail {
                    Text(detail)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            Text(relativeAge(from: component.lastUpdated, now: now))
                .font(.caption)
                .foregroundStyle(.tertiary)
                .monospacedDigit()
        }
        .padding(.vertical, 4)
    }
}

// MARK: - ServicesSectionView

/// Per-service start / stop / restart controls plus global actions.
struct ServicesSectionView: View {

    @EnvironmentObject private var gateway: GatewayManager
    @EnvironmentObject private var dcal: ServiceManager
    @EnvironmentObject private var dmail: ServiceManager
    @EnvironmentObject private var tailscale: TailscaleMonitor
    @EnvironmentObject private var calendarSync: DcalHealthMonitor

    var body: some View {
        Form {
            Section("Gateway") {
                ServiceControlRow(name: "Gateway", isRunning: gateway.isRunning) {
                    gateway.start()
                } stop: {
                    gateway.stop()
                } restart: {
                    gateway.restart()
                }
            }

            Section("Dcal") {
                ServiceControlRow(name: "Dcal", isRunning: dcal.isRunning) {
                    dcal.start()
                } stop: {
                    dcal.stop()
                } restart: {
                    dcal.restart()
                }
            }

            Section("Dmail") {
                ServiceControlRow(name: "Dmail", isRunning: dmail.isRunning) {
                    dmail.start()
                } stop: {
                    dmail.stop()
                } restart: {
                    dmail.restart()
                }
            }

            Section("Actions") {
                Button("Sync Calendar Now") {
                    Task { await calendarSync.syncNow() }
                }
                Button("Bring Tailscale up") {
                    Task { await tailscale.bringUp() }
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Services")
    }
}

// MARK: - ServiceControlRow

private struct ServiceControlRow: View {
    let name: String
    let isRunning: Bool
    let start: () -> Void
    let stop: () -> Void
    let restart: () -> Void

    var body: some View {
        LabeledContent(name) {
            HStack {
                Button("Start", action: start)
                    .disabled(isRunning)
                Button("Stop", action: stop)
                    .disabled(!isRunning)
                Button("Restart", action: restart)
                    .disabled(!isRunning)
            }
            .buttonStyle(.bordered)
        }
    }
}
