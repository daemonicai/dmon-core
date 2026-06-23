import SwiftUI

// MARK: - Status display helpers (shared by MenuBarView and StatusSectionView)

/// SF Symbol name for a given health status.
func statusSymbol(_ status: HealthStatus) -> String {
    switch status {
    case .ok:       return "circle.fill"
    case .degraded: return "exclamationmark.circle.fill"
    case .down:     return "xmark.circle.fill"
    case .unknown:  return "questionmark.circle.fill"
    }
}

/// Foreground colour for a given health status.
func statusColor(_ status: HealthStatus) -> Color {
    switch status {
    case .ok:       return .green
    case .degraded: return .orange
    case .down:     return .red
    case .unknown:  return .gray
    }
}

// MARK: - Relative age helper (pure, injectable `now` for testability)

/// Returns a human-readable relative age string for a given date.
///
/// - Parameters:
///   - date: The timestamp to describe; `nil` means "never published".
///   - now: The reference instant (injectable so callers can assert exact output).
/// - Returns: "—" when `date` is `nil`; otherwise a locale-formatted relative string
///   such as "12 seconds ago" or "2 hours ago".
func relativeAge(from date: Date?, now: Date) -> String {
    guard let date else { return "—" }
    let formatter = RelativeDateTimeFormatter()
    formatter.unitsStyle = .full
    return formatter.localizedString(for: date, relativeTo: now)
}
