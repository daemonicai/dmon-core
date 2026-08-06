import Foundation

/// Where one `ChildLogLine` came from: the child's stdout or stderr pipe, or
/// a line the host itself synthesized about that child rather than
/// something the child wrote (today, only the adoption notice — see
/// `HostSupervisor.apply(_:id:)`'s `.adopted` case).
public enum ChildLogSource: Hashable, Sendable {
    case standardOutput
    case standardError
    case host
}

/// One line of a supervised child's output, attributed to the child and the
/// stream it came from.
///
/// `capturedAt` is when the host observed the line, not when the child wrote
/// it, and `id` is assigned in the order `ChildLogStore` received the line —
/// stdout and stderr are two independent pipes read by two independent
/// tasks, so nothing here claims their relative interleaving reflects the
/// child's actual write order between the two streams. Order *within* a
/// single stream is preserved, since each stream has exactly one reader
/// appending to the store in the order it reads.
public struct ChildLogLine: Hashable, Sendable, Identifiable {
    public let id: Int
    public let childID: ChildID
    public let source: ChildLogSource
    public let text: String
    public let capturedAt: Date

    public init(id: Int, childID: ChildID, source: ChildLogSource, text: String, capturedAt: Date) {
        self.id = id
        self.childID = childID
        self.source = source
        self.text = text
        self.capturedAt = capturedAt
    }
}
