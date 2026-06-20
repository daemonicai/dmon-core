import SwiftUI
import Foundation

// MARK: - Config file helpers (flat YAML, hand-rolled; no library dependency)

private enum ConfigStore {

    // ~/.dmon/config.yaml
    private static var configURL: URL {
        URL(fileURLWithPath: NSHomeDirectory())
            .appendingPathComponent(".dmon/config.yaml")
    }

    /// Parses `key: value` lines; skips comments and blank lines.
    static func load() -> [String: String] {
        guard let text = try? String(contentsOf: configURL, encoding: .utf8) else { return [:] }
        var result: [String: String] = [:]
        for line in text.components(separatedBy: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty, !trimmed.hasPrefix("#") else { continue }
            // Split on FIRST colon only.
            if let colonIndex = trimmed.firstIndex(of: ":") {
                let key   = String(trimmed[..<colonIndex]).trimmingCharacters(in: .whitespaces)
                let after = String(trimmed[trimmed.index(after: colonIndex)...]).trimmingCharacters(in: .whitespaces)
                result[key] = after
            }
        }
        return result
    }

    /// Writes a flat-YAML file. Secret keys whose value is non-empty are written
    /// as `keychain` (the canonical token); on the unsigned-dev fallback path the
    /// real value is written so the core still receives it.
    static func save(
        plaintext: [String: String],
        secrets: [String: String],        // key → real value (may be empty)
        keychainAvailable: Bool
    ) {
        var lines: [String] = ["# Managed by Daemon.App settings panel."]
        for (key, value) in plaintext.sorted(by: { $0.key < $1.key }) {
            lines.append("\(key): \(value)")
        }
        for (key, value) in secrets.sorted(by: { $0.key < $1.key }) {
            if value.isEmpty {
                // Omit cleared secrets.
                continue
            }
            if keychainAvailable {
                lines.append("\(key): keychain")
            } else {
                // Unsigned-dev fallback: real secret in plaintext (visible warning shown in UI).
                lines.append("\(key): \(value)")
            }
        }
        lines.append("")    // trailing newline

        let dir = configURL.deletingLastPathComponent()
        // Mirror GatewayManager.writePIDFile: create ~/.dmon/ if absent.
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let text = lines.joined(separator: "\n")
        try? text.write(to: configURL, atomically: true, encoding: .utf8)
    }
}

// MARK: - SettingsView

struct SettingsView: View {

    @EnvironmentObject private var gateway: GatewayManager
    @StateObject private var loginItems = LoginItemManager()

    // MARK: Inference fields
    @State private var e2bURL:        String = ""
    @State private var reasonerURL:   String = ""
    @State private var geminiKey:     String = ""
    @State private var e2bModel:      String = "gemma4:e2b-it-qat"
    @State private var reasonerModel: String = "gemma4-27b"
    @State private var egressModel:   String = "gemini-2.5-flash"

    // MARK: Calendar fields
    @State private var icalURL:           String = ""
    @State private var dcalAPIKey:        String = ""
    @State private var syncInterval:      String = "15"
    @State private var recurrenceHorizon: String = "90"

    // MARK: Email fields
    @State private var dmailURL:    String = ""
    @State private var dmailAPIKey: String = ""

    // MARK: Advanced fields
    @State private var egressThreshold:  Double = 0.8
    @State private var gatewayPath:      String = ""
    @State private var dcalServerPath:   String = ""
    @State private var dmailServerPath:  String = ""

    // MARK: Save / alert state
    @State private var showRestartAlert = false

    // MARK: Unsigned-dev Keychain fallback
    @State private var keychainFallbackActive = false

    // MARK: - View

    var body: some View {
        Form {

            // MARK: - Unsigned-dev warning
            if keychainFallbackActive {
                Section {
                    Label(
                        "Keychain unavailable (unsigned build) — API keys are stored in plaintext in ~/.dmon/config.yaml.",
                        systemImage: "exclamationmark.triangle"
                    )
                    .foregroundColor(.yellow)
                }
            }

            // MARK: - Inference
            Section("Inference") {
                TextField("E2B endpoint URL", text: $e2bURL)
                    .help("DMON_E2B_URL — remote code-execution endpoint")
                TextField("E2B model ID", text: $e2bModel)
                    .help("DMON_E2B_MODEL — model for the E2B / code-execution turn (default: gemma4:e2b-it-qat)")
                TextField("Reasoner URL", text: $reasonerURL)
                    .help("DMON_REASONER_URL — local or remote reasoning model endpoint")
                TextField("Reasoner model ID", text: $reasonerModel)
                    .help("DMON_REASONER_MODEL — model for the reasoner turn (default: gemma4-27b)")
                TextField("Egress model ID", text: $egressModel)
                    .help("DMON_EGRESS_MODEL — model for egress / final-answer turn (default: gemini-2.5-flash)")
                SecureField("Gemini API key", text: $geminiKey)
                    .help("GEMINI_API_KEY — stored in Keychain (signed builds)")
            }

            // MARK: - Calendar
            Section("Calendar") {
                TextField("iCal URL", text: $icalURL)
                    .help("DCAL_ICAL_URL — calendar feed for the Dcal server (separate process)")
                SecureField("Dcal API key", text: $dcalAPIKey)
                    .help("DCAL_API_KEY — stored in Keychain (signed builds)")
                TextField("Sync interval (minutes)", text: $syncInterval)
                    .help("DCAL_SYNC_INTERVAL_MINUTES — for the Dcal server (separate process)")
                TextField("Recurrence horizon (days)", text: $recurrenceHorizon)
                    .help("DCAL_RECURRENCE_HORIZON_DAYS — for the Dcal server (separate process)")
            }

            // MARK: - Email
            Section("Email") {
                TextField("Dmail URL", text: $dmailURL)
                    .help("DMAIL_BASE_URL — Dmail server endpoint")
                SecureField("Dmail API key", text: $dmailAPIKey)
                    .help("DMAIL_API_KEY — stored in Keychain (signed builds)")
            }

            // MARK: - Advanced
            Section("Advanced") {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Confidence threshold: \(String(format: "%.2f", egressThreshold))")
                    Slider(value: $egressThreshold, in: 0...1, step: 0.05)
                        .help("DMON_EGRESS_THRESHOLD — not yet wired into the router; persisted for forward-compat")
                }
                TextField("Gateway binary path", text: $gatewayPath)
                    .help("DMON_GATEWAY_PATH — override resolved gateway binary")
                Toggle("Launch at Login", isOn: $loginItems.isEnabled)
                    .onChange(of: loginItems.isEnabled) { _, newValue in
                        loginItems.setEnabled(newValue)
                    }
            }

            // MARK: - Servers
            Section("Servers") {
                TextField("Dcal server binary path", text: $dcalServerPath)
                    .help("DMON_DCAL_SERVER_PATH — override resolved Dcal server binary; takes effect on next app launch")
                TextField("Dmail server binary path", text: $dmailServerPath)
                    .help("DMON_DMAIL_SERVER_PATH — override resolved Dmail server binary; takes effect on next app launch")
            }

            // MARK: - Save
            Section {
                Button("Save and Restart…") {
                    showRestartAlert = true
                }
                .alert("Restart Daemon?", isPresented: $showRestartAlert) {
                    Button("Cancel", role: .cancel) {}
                    Button("Continue", role: .destructive) {
                        persistAndRestart()
                    }
                } message: {
                    Text("Saving will restart the Daemon session. Continue?")
                }
            }
        }
        .formStyle(.grouped)
        .padding()
        .frame(minWidth: 440, minHeight: 480)
        .onAppear {
            loadFromStore()
            loginItems.registerOnFirstLaunchIfNeeded()
        }
    }

    // MARK: - Load

    private func loadFromStore() {
        let cfg = ConfigStore.load()

        e2bURL        = cfg["DMON_E2B_URL"]       ?? ""
        reasonerURL   = cfg["DMON_REASONER_URL"]  ?? ""
        e2bModel      = cfg["DMON_E2B_MODEL"]      ?? "gemma4:e2b-it-qat"
        reasonerModel = cfg["DMON_REASONER_MODEL"] ?? "gemma4-27b"
        egressModel   = cfg["DMON_EGRESS_MODEL"]   ?? "gemini-2.5-flash"
        icalURL       = cfg["DCAL_ICAL_URL"]       ?? ""
        syncInterval  = cfg["DCAL_SYNC_INTERVAL_MINUTES"] ?? "15"
        recurrenceHorizon = cfg["DCAL_RECURRENCE_HORIZON_DAYS"] ?? "90"
        dmailURL      = cfg["DMAIL_BASE_URL"]      ?? ""
        gatewayPath   = cfg["DMON_GATEWAY_PATH"]   ?? ""
        dcalServerPath  = cfg["DMON_DCAL_SERVER_PATH"]  ?? ""
        dmailServerPath = cfg["DMON_DMAIL_SERVER_PATH"] ?? ""

        if let raw = cfg["DMON_EGRESS_THRESHOLD"], let d = Double(raw) {
            egressThreshold = d
        }

        // For secret keys: read from Keychain; fall back to YAML value
        // (unsigned-dev path where real value was stored in YAML).
        geminiKey   = readSecret("GEMINI_API_KEY", yaml: cfg)
        dcalAPIKey  = readSecret("DCAL_API_KEY",   yaml: cfg)
        dmailAPIKey = readSecret("DMAIL_API_KEY",  yaml: cfg)
    }

    /// Returns the Keychain value if present; otherwise returns the YAML
    /// value (which on the unsigned-dev path is the real secret).
    private func readSecret(_ account: String, yaml: [String: String]) -> String {
        if let kc = Keychain.read(account: account) {
            return kc
        }
        let raw = yaml[account] ?? ""
        // Don't surface the literal token `keychain` to the text field.
        return raw == "keychain" ? "" : raw
    }

    // MARK: - Persist + restart (10.3)

    private func persistAndRestart() {
        let plaintext: [String: String] = [
            "DMON_E2B_URL":                 e2bURL,
            "DMON_REASONER_URL":            reasonerURL,
            "DMON_E2B_MODEL":               e2bModel,
            "DMON_REASONER_MODEL":          reasonerModel,
            "DMON_EGRESS_MODEL":            egressModel,
            "DCAL_BASE_URL":                "http://localhost:5280",  // default; not exposed in UI
            "DCAL_ICAL_URL":                icalURL,
            "DCAL_SYNC_INTERVAL_MINUTES":   syncInterval,
            "DCAL_RECURRENCE_HORIZON_DAYS": recurrenceHorizon,
            "DMAIL_BASE_URL":               dmailURL,
            "DMON_EGRESS_THRESHOLD":        String(format: "%.2f", egressThreshold),
            // DMON_EGRESS_THRESHOLD is not yet wired into the router; persisted for forward-compat.
            "DMON_GATEWAY_PATH":            gatewayPath,
            "DMON_DCAL_SERVER_PATH":        dcalServerPath,
            "DMON_DMAIL_SERVER_PATH":       dmailServerPath
        ].filter { !$0.value.isEmpty }

        let secrets: [String: String] = [
            "GEMINI_API_KEY": geminiKey,
            "DCAL_API_KEY":   dcalAPIKey,
            "DMAIL_API_KEY":  dmailAPIKey
        ]

        // Write / clear Keychain; detect fallback state.
        var fallback = false
        for (account, value) in secrets {
            if value.isEmpty {
                Keychain.delete(account: account)
            } else {
                let ok = Keychain.save(value, account: account)
                if !ok { fallback = true }
            }
        }
        keychainFallbackActive = fallback

        // Persist config.yaml (secrets written as `keychain` token, or plaintext on fallback).
        ConfigStore.save(plaintext: plaintext, secrets: secrets, keychainAvailable: !fallback)

        // Build env dict from all persisted values; read secrets back from Keychain
        // (or from the in-memory values on the fallback path) so the core receives them.
        var env = plaintext
        for (account, value) in secrets where !value.isEmpty {
            // Dcal-server keys (DCAL_ICAL_URL etc.) are included for forward-compat;
            // this process does NOT spawn/manage the Dcal server.
            env[account] = Keychain.read(account: account) ?? value
        }

        // Wire overrides into GatewayManager and restart.
        gateway.settingsEnvironment = env
        gateway.gatewayPathOverride = gatewayPath.isEmpty ? nil : gatewayPath
        gateway.restart()
    }
}
