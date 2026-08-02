import AVFoundation
import Observation

/// Tracks TCC microphone authorisation and lets the UI request it on demand.
/// No capture or audio engine here — only the permission query and request.
@MainActor
@Observable
final class MicrophoneAuthorizationModel {
    private(set) var status: AVAuthorizationStatus

    init() {
        status = AVCaptureDevice.authorizationStatus(for: .audio)
    }

    func refresh() {
        status = AVCaptureDevice.authorizationStatus(for: .audio)
    }

    /// Requests access when undetermined; otherwise just refreshes, since
    /// macOS only prompts once and later changes happen in System Settings.
    func requestAccess() async {
        guard status == .notDetermined else {
            refresh()
            return
        }
        _ = await AVCaptureDevice.requestAccess(for: .audio)
        refresh()
    }
}

extension AVAuthorizationStatus {
    var dmonHomeLabel: String {
        switch self {
        case .notDetermined:
            "Not Determined"
        case .restricted:
            "Restricted"
        case .denied:
            "Denied"
        case .authorized:
            "Authorized"
        @unknown default:
            "Unknown"
        }
    }

    var dmonHomeGuidance: String {
        switch self {
        case .notDetermined:
            "Request access to let dmon-home hear you."
        case .restricted:
            "Microphone access is restricted by system policy (e.g. parental controls) and cannot be granted here."
        case .denied:
            "Microphone access was denied. Enable it in System Settings > Privacy & Security > Microphone."
        case .authorized:
            "dmon-home may use the microphone."
        @unknown default:
            "Unrecognised authorisation status."
        }
    }
}
