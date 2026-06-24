import AppKit
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

// MARK: - Rollup presentation helpers (shared by MenuBarExtra, DashboardView, and Dock tint)

/// A testable intermediate token representing the visual state driven by `RollupColor`.
/// Mapping to concrete `Color`/`NSColor` happens in the thin UI layer; tests assert tokens.
enum RollupPresentation: Equatable {
    case healthyGreen
    case warningAmber
    case criticalRed

    /// Human-readable label for the rollup state, used in the dashboard toolbar indicator.
    /// Defined here so the single source of truth covers both the token and its display name.
    var label: String {
        switch self {
        case .healthyGreen: return "Healthy"
        case .warningAmber: return "Degraded"
        case .criticalRed:  return "Critical"
        }
    }
}

/// Map a `RollupColor` to its intermediate presentation token.
/// Pure function — no AppKit or SwiftUI import required to call.
func rollupPresentation(_ rollup: RollupColor) -> RollupPresentation {
    switch rollup {
    case .green: return .healthyGreen
    case .amber: return .warningAmber
    case .red:   return .criticalRed
    }
}

/// Map a `RollupColor` to the SwiftUI `Color` used for icon foreground and window status.
/// Routes through `rollupPresentation` so a divergence in the token mapping makes the
/// colour functions structurally impossible to get wrong independently.
func rollupColor(_ rollup: RollupColor) -> Color {
    switch rollupPresentation(rollup) {
    case .healthyGreen: return .green
    case .warningAmber: return .orange
    case .criticalRed:  return .red
    }
}

/// Map a `RollupColor` to an `NSColor` for AppKit surfaces (Dock icon tint).
/// Routes through `rollupPresentation` for the same structural guarantee.
func rollupNSColor(_ rollup: RollupColor) -> NSColor {
    switch rollupPresentation(rollup) {
    case .healthyGreen: return .systemGreen
    case .warningAmber: return .systemOrange
    case .criticalRed:  return .systemRed
    }
}

// MARK: - Dock icon tint helper

/// Returns a copy of the standard application icon with a coloured status-dot badge
/// overlaid in the bottom-right corner.  The badge conveys the aggregate rollup state
/// so the Dock reflects health at a glance (D6 / task 4.1).
///
/// Falls back to the unmodified icon if the base image is unavailable (e.g. in tests
/// running headless without an app bundle).
func tintedAppIcon(_ tint: NSColor) -> NSImage {
    // Prefer the bundle icon; fall back to a plain square so the function never returns nil.
    let base: NSImage = NSApp.applicationIconImage ?? {
        let fallback = NSImage(size: NSSize(width: 512, height: 512))
        fallback.lockFocus()
        NSColor.windowBackgroundColor.setFill()
        NSRect(origin: .zero, size: fallback.size).fill()
        fallback.unlockFocus()
        return fallback
    }()

    let size = base.size
    let copy = NSImage(size: size)
    copy.lockFocus()
    base.draw(in: NSRect(origin: .zero, size: size))

    // Status dot: 20% of icon width, anchored 5% from the bottom-right corner.
    let dotDiameter = size.width * 0.20
    let margin      = size.width * 0.05
    let dotRect = NSRect(
        x: size.width  - dotDiameter - margin,
        y: margin,
        width: dotDiameter,
        height: dotDiameter
    )
    // White halo for contrast against any background.
    NSColor.white.setFill()
    NSBezierPath(ovalIn: dotRect.insetBy(dx: -2, dy: -2)).fill()

    tint.setFill()
    NSBezierPath(ovalIn: dotRect).fill()
    copy.unlockFocus()

    return copy
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
