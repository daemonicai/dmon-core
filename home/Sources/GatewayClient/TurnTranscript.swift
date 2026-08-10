import Foundation

/// One rendered line of a `TurnTranscript`: a user's typed message, an
/// assistant reply (possibly still in flight), or a `.notice` the host
/// synthesises for something that never reached the wire, or that reached it
/// but produced nothing to render.
public struct TranscriptEntry: Identifiable, Hashable, Sendable {
    public enum Role: Hashable, Sendable {
        case user
        case assistant
        case notice
    }

    /// `.awaitingResponse` and `.streaming` are the two states an entry
    /// named by `TurnTranscript.openTurn` can be in while it is still named
    /// there — see that property's own doc comment for why membership is
    /// tracked by id, not read back off this state. Every other case is
    /// terminal for this entry: nothing in this type ever transitions an
    /// entry out of `.complete`, `.ended`, `.failed`, or `.refused` once
    /// reached, and an entry can also simply stop being the open turn while
    /// still sitting in `.awaitingResponse` or `.streaming` — see
    /// `TurnTranscript.openTurnID`'s doc comment for that case.
    public enum State: Hashable, Sendable {
        /// A user entry, or a notice — never mutated after creation.
        case complete
        /// The assistant entry `recordSubmittedTurn(_:)` created; no
        /// `TurnEvent` has been folded into it yet.
        case awaitingResponse
        /// `.turnStarted` and/or one or more `.textDelta`s have been folded
        /// into this entry. Its `text` may already be non-empty.
        case streaming
        /// `.turnEnded` was folded into this entry. Terminal.
        case ended
        /// `.failed(code:message:recoverable:)` was folded into this entry,
        /// or a `.notice` was synthesised for one that arrived with no open
        /// turn to fold into. Terminal. `text` on the entry itself may still
        /// hold partial rendered content from before the failure — see
        /// `TurnTranscript.apply(_:)`'s doc comment on `.failed`.
        case failed(code: String, message: String, recoverable: Bool)
        /// `recordSubmissionRefused(_:reason:)` created this entry: the
        /// message never reached the wire at all. Terminal.
        case refused(reason: String)
    }

    public let id: Int
    public let role: Role
    public private(set) var text: String
    public private(set) var state: State

    fileprivate init(id: Int, role: Role, text: String, state: State) {
        self.id = id
        self.role = role
        self.text = text
        self.state = state
    }

    fileprivate mutating func appendDeltaAndMarkStreaming(_ delta: String) {
        text += delta
        state = .streaming
    }

    fileprivate mutating func markStreaming() {
        state = .streaming
    }

    fileprivate mutating func markEnded() {
        state = .ended
    }

    fileprivate mutating func markFailed(code: String, message: String, recoverable: Bool) {
        state = .failed(code: code, message: message, recoverable: recoverable)
    }

    /// The failure detail a renderer should show **in addition to** `text` — `nil` when `text`
    /// already carries the full reason and repeating it would just duplicate the row.
    ///
    /// `TurnTranscript.apply(_:)`'s `.failed` case sets `text` to the failure `message` itself
    /// only on its no-open-turn branch, which always produces a `.notice` entry; its open-turn
    /// branch instead keeps whatever partial content that (`.assistant`) entry had already
    /// streamed, and never writes `message` into `text` at all — so without this property, that
    /// entry's `code`/`message` would never be shown anywhere. `.refused` is only ever produced
    /// by `recordSubmissionRefused(_:reason:)`, which is always a `.notice` entry and always sets
    /// `text` to `reason` — so it never needs a detail line either. This lives here, on the type
    /// that owns both `apply(_:)`'s branches and the entry's own `role`/`state`/`text`, rather
    /// than as a renderer's traced-through-the-reducer guess at the same fact — a later change to
    /// either branch changes this property along with it, with a compiler and test signal, rather
    /// than silently invalidating a rule recorded somewhere else.
    public var additionalFailureDetail: String? {
        guard case .failed(let code, let message, let recoverable) = state, role != .notice else {
            return nil
        }
        return "\(message) (\(code)\(recoverable ? "" : ", not recoverable"))"
    }
}

/// Folds the `TurnEvent` sequence `TurnProjection.project(_:)` produces into
/// an ordered, incrementally-rendered transcript.
///
/// **A pure reducer, deliberately.** No clock, no `Task`, no deadline,
/// nothing `async` anywhere on this type. A submitted turn can, per
/// `TurnEvent.failed`'s own doc comment (`TurnProjection.swift`), produce
/// nothing on the wire at all before ending — a cancellation before
/// `turnStart` ever reaches the wire is one documented way that happens; a
/// turn parked on an unanswered `tool.confirmRequest`, which this module
/// does not yet model, is another, indistinguishable from here. Because
/// nothing on the wire ever tells this type "this turn will never produce
/// another event" — only ever "here is the next one" or silence — it never
/// guesses which silence it is looking at: an open turn stays open, rendered
/// honestly as awaiting or streaming, until an event actually closes it.
/// Timing that out, if it is ever wanted, is a decision for whatever drives
/// this type from the outside, not for the reducer itself.
///
/// **Attribution is positional.** `turnStart`/`messageDelta`/`turnEnd`/
/// `error` carry no correlation id on the wire — only `ResultEvent`
/// subclasses do, and none of these four is one — so every `apply(_:)` call
/// is folded into whichever entry `openTurn` currently names, not matched
/// against the turn id `submitTurn(_:)` returned. That is sound only because
/// the core's own turn gate (`TurnHandler`, see `TurnEvent.failed`'s
/// `turnInProgress` case) refuses a concurrent submit outright rather than
/// interleaving two turns' events on one session — if that gate is ever
/// relaxed to admit concurrent turns, this reducer's positional attribution
/// silently misattributes events between them, and no Swift-side test here
/// would catch it; the invariant lives entirely on the core side.
///
/// Entry ids are a monotonically increasing counter owned by this type, not
/// `UUID` — deterministic, so a test (or a SwiftUI diff) can assert an
/// entry's identity rather than its position in `entries`.
public struct TurnTranscript: Hashable, Sendable {
    public private(set) var entries: [TranscriptEntry]
    private var nextID: Int

    /// The id of the entry `apply(_:)` folds the next `TurnEvent` into, or
    /// `nil` if there is none right now. Tracked explicitly rather than
    /// re-derived by scanning `entries` for the last `.awaitingResponse`/
    /// `.streaming` entry — that scan is wrong the moment two turns overlap
    /// in `entries` without both being live at once: a turn that never
    /// receives a terminal event (silence — see this type's own module doc
    /// comment) leaves its entry sitting in `.awaitingResponse` forever, and
    /// a *later* submitted turn that goes on to reach `.ended` would make
    /// the scan walk back past it and resume folding events into the
    /// abandoned earlier entry. An explicit id has no such blind spot: it
    /// only ever names the turn this type most recently opened and has not
    /// yet closed, regardless of what any other entry's state happens to be.
    ///
    /// Set by `recordSubmittedTurn(_:)` and by the no-open-turn synthesis
    /// branches of `apply(_:)` (`.textDelta`, `.turnStarted`) to the entry
    /// they just appended; cleared to `nil` by the terminal folds
    /// (`.turnEnded`, `.failed` onto an open turn). An entry that is
    /// abandoned this way — never closed because the wire fell silent — is
    /// not transitioned to any other state; it is simply no longer named
    /// here. Rendering it as permanently awaiting is the honest thing: this
    /// type never learns that turn's outcome, so it does not invent one.
    private var openTurnID: TranscriptEntry.ID?

    public init() {
        entries = []
        nextID = 0
        openTurnID = nil
    }

    private mutating func makeID() -> Int {
        defer { nextID += 1 }
        return nextID
    }

    /// Appends the user's message (`.user`, `.complete`) and a fresh
    /// assistant entry (`.assistant`, `.awaitingResponse`) that becomes the
    /// new `openTurn`, and returns that assistant entry's id.
    ///
    /// This type has no idea whether `message` was actually sent — that is
    /// `GatewaySession.submitTurn(_:)`'s job, and its `.notAttached` gate is
    /// not duplicated here (see this type's own module doc for why). A
    /// caller that calls this only after `submitTurn(_:)` has already
    /// succeeded is what keeps "recorded as submitted" meaning what it says.
    @discardableResult
    public mutating func recordSubmittedTurn(_ message: String) -> TranscriptEntry.ID {
        let userEntry = TranscriptEntry(id: makeID(), role: .user, text: message, state: .complete)
        let assistantEntry = TranscriptEntry(id: makeID(), role: .assistant, text: "", state: .awaitingResponse)
        entries.append(userEntry)
        entries.append(assistantEntry)
        openTurnID = assistantEntry.id
        return assistantEntry.id
    }

    /// Appends the user's message (`.user`, `.complete`) and a `.notice`
    /// entry in `.refused(reason:)`. Creates no open turn: nothing was
    /// submitted, so there is nothing to await. The typed message is kept,
    /// not discarded, so a refusal (e.g. `GatewaySessionError.notAttached`)
    /// never costs the user what they typed.
    ///
    /// The notice's `text` carries `reason` verbatim — not left empty with
    /// the reason only reachable through `state` — matching the convention
    /// the no-open-turn branch of `apply(_:)`'s `.failed` case already
    /// follows for the same reason: a renderer that (reasonably) shows
    /// `entry.text` for a `.notice` must not render a blank bubble for the
    /// one case task 8.2 exists to surface.
    public mutating func recordSubmissionRefused(_ message: String, reason: String) {
        let userEntry = TranscriptEntry(id: makeID(), role: .user, text: message, state: .complete)
        let noticeEntry = TranscriptEntry(id: makeID(), role: .notice, text: reason, state: .refused(reason: reason))
        entries.append(userEntry)
        entries.append(noticeEntry)
    }

    /// The entry named by `openTurnID` — the one `apply(_:)` folds the next
    /// `TurnEvent` into. `nil` once that turn has reached `.ended` or
    /// `.failed`, or if none has ever been opened. See `openTurnID`'s own
    /// doc comment for why this is a lookup by tracked id, not a scan for
    /// the last entry whose state happens to be `.awaitingResponse` or
    /// `.streaming` — an earlier turn that the wire fell silent on can be
    /// sitting in exactly one of those states long after a later turn has
    /// opened and closed, and a scan would find that stale entry instead.
    public var openTurn: TranscriptEntry? {
        guard let openTurnID else {
            return nil
        }
        return entries.first { $0.id == openTurnID }
    }

    private var openTurnIndex: Int? {
        guard let openTurnID else {
            return nil
        }
        return entries.firstIndex { $0.id == openTurnID }
    }

    /// Folds one `TurnEvent` into the transcript. See this type's own doc
    /// comment for why this never waits and never invents a timeout.
    public mutating func apply(_ event: TurnEvent) {
        switch event {
        case .textDelta(let text):
            // Every delta is folded in immediately, one at a time, so it is
            // visible in `entries` before the next arrives — that is what
            // "incremental", not "only on completion", means for this type.
            if let index = openTurnIndex {
                entries[index].appendDeltaAndMarkStreaming(text)
            } else {
                // No open turn: a turn in flight that this transcript did
                // not submit (a replayed event after a reattach, or another
                // client attached to the same session — ADR-012). Rendering
                // it as a fresh streaming entry keeps the text, rather than
                // dropping it because no local submit produced it — and it
                // becomes the new open turn, so a later `.turnEnded`/
                // `.failed` for the same turn still finds and closes it.
                let entry = TranscriptEntry(id: makeID(), role: .assistant, text: text, state: .streaming)
                entries.append(entry)
                openTurnID = entry.id
            }
        case .turnStarted:
            if let index = openTurnIndex {
                entries[index].markStreaming()
            } else {
                let entry = TranscriptEntry(id: makeID(), role: .assistant, text: "", state: .streaming)
                entries.append(entry)
                openTurnID = entry.id
            }
        case .turnEnded:
            // With no open turn, this is ignored rather than synthesising an
            // entry: there is nothing to close, no text was ever received
            // for it, and an empty `.ended` entry would render as noise with
            // no content to justify its own existence.
            if let index = openTurnIndex {
                entries[index].markEnded()
                openTurnID = nil
            }
        case .failed(let code, let message, let recoverable):
            if let index = openTurnIndex {
                // Whatever partial text this entry has already accumulated
                // (a mid-turn failure after `turnStart` and some deltas) is
                // kept, not cleared — this reducer cannot retract what it
                // has already rendered, and should not try to.
                entries[index].markFailed(code: code, message: message, recoverable: recoverable)
                openTurnID = nil
            } else {
                // No open turn: the failure arrived with nothing rendered
                // yet (a `turnInProgress` refusal, or a first-run provider
                // failure before `turnStart` — see `TurnEvent.failed`'s doc
                // comment). Synthesised as its own `.notice` so "nothing
                // rendered yet" is never left indistinguishable from "still
                // running".
                entries.append(
                    TranscriptEntry(
                        id: makeID(),
                        role: .notice,
                        text: message,
                        state: .failed(code: code, message: message, recoverable: recoverable)
                    )
                )
            }
        }
    }
}
