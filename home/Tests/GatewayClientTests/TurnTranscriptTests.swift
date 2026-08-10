import Foundation
import Testing
@testable import GatewayClient

/// Exercises `TurnTranscript` as a pure reducer over `TurnEvent` — no
/// `GatewaySession`, no transport, no wire JSON involved. `TurnProjection
/// Tests` already covers item → `TurnEvent`; this covers `TurnEvent` →
/// rendered transcript.
@Suite
struct TurnTranscriptTests {
    @Test
    func deltasAppendInOrderIntoOneEntryAndEachIsObservableBeforeTheNextArrives() {
        var transcript = TurnTranscript()
        let assistantID = transcript.recordSubmittedTurn("hello")

        transcript.apply(.textDelta("Hel"))
        let afterFirst = transcript.entries.first { $0.id == assistantID }
        #expect(afterFirst?.text == "Hel")
        #expect(afterFirst?.state == .streaming)

        transcript.apply(.textDelta("lo"))
        let afterSecond = transcript.entries.first { $0.id == assistantID }
        #expect(afterSecond?.text == "Hello")

        transcript.apply(.textDelta(" world"))
        let afterThird = transcript.entries.first { $0.id == assistantID }
        #expect(afterThird?.text == "Hello world")
    }

    @Test
    func turnStartDeltasThenTurnEndYieldsAnEndedEntryWithConcatenatedTextAndNoOpenTurn() {
        var transcript = TurnTranscript()
        let assistantID = transcript.recordSubmittedTurn("hi")

        transcript.apply(.turnStarted)
        transcript.apply(.textDelta("Hi "))
        transcript.apply(.textDelta("there"))
        transcript.apply(.turnEnded)

        let entry = transcript.entries.first { $0.id == assistantID }
        #expect(entry?.state == .ended)
        #expect(entry?.text == "Hi there")
        #expect(transcript.openTurn == nil)
    }

    @Test
    func aMidTurnFailureAfterDeltasKeepsThePartialTextAndClosesTheOpenTurn() {
        var transcript = TurnTranscript()
        let assistantID = transcript.recordSubmittedTurn("hi")

        transcript.apply(.turnStarted)
        transcript.apply(.textDelta("partial"))
        transcript.apply(.failed(code: "internalError", message: "boom", recoverable: false))

        let entry = transcript.entries.first { $0.id == assistantID }
        #expect(entry?.text == "partial")
        #expect(entry?.state == .failed(code: "internalError", message: "boom", recoverable: false))
        #expect(transcript.openTurn == nil)
    }

    @Test
    func aFailureWithNoTurnStartAtAllFailsTheOpenTurnRatherThanLeavingItAwaiting() {
        var transcript = TurnTranscript()
        let assistantID = transcript.recordSubmittedTurn("hi")

        transcript.apply(.failed(code: "internalError", message: "no provider configured", recoverable: false))

        let entry = transcript.entries.first { $0.id == assistantID }
        #expect(entry?.state == .failed(code: "internalError", message: "no provider configured", recoverable: false))
        #expect(entry?.text == "")
        #expect(transcript.openTurn == nil)
    }

    @Test
    func aTurnInProgressFailureFailsTheOpenTurn() {
        var transcript = TurnTranscript()
        let assistantID = transcript.recordSubmittedTurn("hi")

        transcript.apply(.failed(code: "turnInProgress", message: "a turn is already running", recoverable: true))

        let entry = transcript.entries.first { $0.id == assistantID }
        #expect(entry?.state == .failed(code: "turnInProgress", message: "a turn is already running", recoverable: true))
    }

    @Test
    func aFailureWithNoOpenTurnAppendsANoticeEntryInFailedState() {
        var transcript = TurnTranscript()

        transcript.apply(.failed(code: "internalError", message: "unexpected", recoverable: false))

        #expect(transcript.entries.count == 1)
        let entry = transcript.entries[0]
        #expect(entry.role == .notice)
        #expect(entry.text == "unexpected")
        #expect(entry.state == .failed(code: "internalError", message: "unexpected", recoverable: false))
        #expect(transcript.openTurn == nil)
    }

    @Test
    func aDeltaWithNoOpenTurnAppendsANewStreamingAssistantEntryRatherThanDroppingTheText() {
        var transcript = TurnTranscript()

        transcript.apply(.textDelta("replayed text"))

        #expect(transcript.entries.count == 1)
        let entry = transcript.entries[0]
        #expect(entry.role == .assistant)
        #expect(entry.text == "replayed text")
        #expect(entry.state == .streaming)
    }

    @Test
    func aTurnStartedWithNoOpenTurnAppendsAnEmptyStreamingAssistantEntry() {
        var transcript = TurnTranscript()

        transcript.apply(.turnStarted)

        #expect(transcript.entries.count == 1)
        let entry = transcript.entries[0]
        #expect(entry.role == .assistant)
        #expect(entry.text == "")
        #expect(entry.state == .streaming)
    }

    @Test
    func turnEndedWithNoOpenTurnChangesNothing() {
        var transcript = TurnTranscript()
        transcript.recordSubmissionRefused("hi", reason: "not attached")
        let before = transcript

        transcript.apply(.turnEnded)

        #expect(transcript == before)
    }

    @Test
    func convertOpenTurnToRefusalReplacesTheOpenAssistantEntryWithARefusedNoticeInTheSameShapeAsRecordSubmissionRefused() {
        var transcript = TurnTranscript()
        let assistantID = transcript.recordSubmittedTurn("what's the plan?")

        transcript.convertOpenTurnToRefusal(reason: "no session is attached")

        #expect(transcript.entries.count == 2)
        #expect(transcript.entries[0].role == .user)
        #expect(transcript.entries[0].text == "what's the plan?")
        #expect(transcript.entries[1].id == assistantID, "the entry's id is kept, not minted fresh")
        #expect(transcript.entries[1].role == .notice)
        #expect(transcript.entries[1].text == "no session is attached")
        #expect(transcript.entries[1].state == .refused(reason: "no session is attached"))
        #expect(transcript.openTurn == nil)
    }

    @Test
    func convertOpenTurnToRefusalWithNoOpenTurnIsAHarmlessNoOp() {
        var transcript = TurnTranscript()
        transcript.recordSubmissionRefused("hi", reason: "no session is attached")
        let before = transcript

        transcript.convertOpenTurnToRefusal(reason: "irrelevant")

        #expect(transcript == before)
    }

    @Test
    func abandonOpenTurnMarksTheOpenTurnAbandonedKeepsPartialTextAndClearsOpenTurn() {
        var transcript = TurnTranscript()
        let assistantID = transcript.recordSubmittedTurn("hi")

        transcript.apply(.turnStarted)
        transcript.apply(.textDelta("partial"))
        transcript.abandonOpenTurn()

        let entry = transcript.entries.first { $0.id == assistantID }
        #expect(entry?.state == .abandoned)
        #expect(entry?.text == "partial")
        #expect(transcript.openTurn == nil)
    }

    @Test
    func abandonOpenTurnWithNoOpenTurnIsAHarmlessNoOp() {
        var transcript = TurnTranscript()
        transcript.recordSubmissionRefused("hi", reason: "no session is attached")
        let before = transcript

        transcript.abandonOpenTurn()

        #expect(transcript == before)
    }

    /// Finding 1's own constraint, made concrete: `.abandoned` is a decision only
    /// `abandonOpenTurn()` may reach — the reducer itself must never conclude, from any event or
    /// combination of events, that an open turn is over on its own. Every `TurnEvent` variant is
    /// driven here against a freshly opened turn; none may leave `.abandoned` anywhere in
    /// `entries`.
    @Test
    func applyNeverProducesTheAbandonedStateForAnyEventVariant() {
        let events: [TurnEvent] = [
            .textDelta("x"),
            .turnStarted,
            .turnEnded,
            .failed(code: "internalError", message: "boom", recoverable: false),
        ]
        for event in events {
            var transcript = TurnTranscript()
            transcript.recordSubmittedTurn("hi")
            transcript.apply(event)
            for entry in transcript.entries {
                #expect(entry.state != .abandoned, "apply(\(event)) must never produce .abandoned")
            }
        }
    }

    @Test
    func recordSubmissionRefusedKeepsTheTypedTextRecordsTheReasonAndLeavesNoOpenTurn() {
        var transcript = TurnTranscript()

        transcript.recordSubmissionRefused("what's the plan?", reason: "not attached")

        #expect(transcript.entries.count == 2)
        #expect(transcript.entries[0].role == .user)
        #expect(transcript.entries[0].text == "what's the plan?")
        #expect(transcript.entries[0].state == .complete)
        #expect(transcript.entries[1].role == .notice)
        #expect(transcript.entries[1].state == .refused(reason: "not attached"))
        // The refusal must be visible to a renderer that shows `entry.text`
        // — a blank notice bubble is exactly the "failing silently" the
        // spec's not-attached scenario forbids.
        #expect(transcript.entries[1].text == "not attached")
        #expect(!transcript.entries[1].text.isEmpty)
        #expect(transcript.openTurn == nil)
    }

    @Test
    func aStreamingEntrySynthesisedFromADeltaWithNoOpenTurnIsSubsequentlyClosedByTurnEnded() {
        var transcript = TurnTranscript()

        transcript.apply(.textDelta("replayed"))
        transcript.apply(.turnEnded)

        #expect(transcript.entries.count == 1)
        #expect(transcript.entries[0].state == .ended)
        #expect(transcript.entries[0].text == "replayed")
        #expect(transcript.openTurn == nil)
    }

    @Test
    func anEarlierTurnThatNeverClosesStaysAwaitingWhileALaterTurnOpensAndEndsAndOpenTurnEndsUpNil() {
        var transcript = TurnTranscript()

        let firstID = transcript.recordSubmittedTurn("first, never answered")
        // No events at all for the first turn — the silence this type must
        // not mistake for anything else.

        let secondID = transcript.recordSubmittedTurn("second")
        transcript.apply(.turnStarted)
        transcript.apply(.textDelta("done"))
        transcript.apply(.turnEnded)

        let firstEntry = transcript.entries.first { $0.id == firstID }
        let secondEntry = transcript.entries.first { $0.id == secondID }

        #expect(firstEntry?.state == .awaitingResponse)
        #expect(secondEntry?.state == .ended)
        #expect(secondEntry?.text == "done")
        // The scan-based bug this guards against: after the second turn
        // ends, `openTurn` must not walk back and resurrect the first.
        #expect(transcript.openTurn == nil)
    }

    @Test
    func twoTurnsInSequenceLandTheSecondsEventsOnTheSecondEntryNotTheFirst() {
        var transcript = TurnTranscript()

        let firstID = transcript.recordSubmittedTurn("first")
        transcript.apply(.turnStarted)
        transcript.apply(.textDelta("one"))
        transcript.apply(.turnEnded)

        let secondID = transcript.recordSubmittedTurn("second")
        transcript.apply(.turnStarted)
        transcript.apply(.textDelta("two"))

        let firstEntry = transcript.entries.first { $0.id == firstID }
        let secondEntry = transcript.entries.first { $0.id == secondID }

        #expect(firstID != secondID)
        #expect(firstEntry?.text == "one")
        #expect(firstEntry?.state == .ended)
        #expect(secondEntry?.text == "two")
        #expect(secondEntry?.state == .streaming)
        #expect(transcript.openTurn?.id == secondID)
    }

    /// `TranscriptEntry.additionalFailureDetail` exists so a renderer never has to re-derive,
    /// from `apply(_:)`'s own branches, whether `text` already carries a failure's reason —
    /// every entry below comes from actually driving `TurnTranscript`, not a hand-built
    /// `TranscriptEntry`, so a future change to those branches would break these fixtures too,
    /// not just the property.
    @Test
    func additionalFailureDetailIsNilForEveryNonTerminalOrSuccessfulState() {
        var transcript = TurnTranscript()
        let assistantID = transcript.recordSubmittedTurn("hi")

        let userEntry = transcript.entries[0]
        #expect(userEntry.state == .complete)
        #expect(userEntry.additionalFailureDetail == nil)

        let awaiting = transcript.entries.first { $0.id == assistantID }
        #expect(awaiting?.additionalFailureDetail == nil)

        transcript.apply(.turnStarted)
        let streaming = transcript.entries.first { $0.id == assistantID }
        #expect(streaming?.additionalFailureDetail == nil)

        transcript.apply(.turnEnded)
        let ended = transcript.entries.first { $0.id == assistantID }
        #expect(ended?.state == .ended)
        #expect(ended?.additionalFailureDetail == nil)
    }

    /// The one case the property exists for: a mid-stream failure on an `.assistant` entry never
    /// writes its `code`/`message` into `text` (`text` keeps the partial streamed content
    /// instead — see `aMidTurnFailureAfterDeltasKeepsThePartialTextAndClosesTheOpenTurn` above),
    /// so this is the only place that reason is visible at all.
    @Test
    func additionalFailureDetailSurfacesCodeAndMessageForAnAssistantEntryFailedMidStream() {
        var transcript = TurnTranscript()
        let assistantID = transcript.recordSubmittedTurn("hi")

        transcript.apply(.turnStarted)
        transcript.apply(.textDelta("partial"))
        transcript.apply(.failed(code: "internalError", message: "boom", recoverable: false))

        let entry = transcript.entries.first { $0.id == assistantID }
        #expect(entry?.role == .assistant)
        #expect(entry?.text == "partial", "text keeps the partial content, not the failure message")
        #expect(entry?.additionalFailureDetail == "boom (internalError, not recoverable)")
    }

    @Test
    func additionalFailureDetailOmitsTheNotRecoverableSuffixWhenTheFailureIsRecoverable() {
        var transcript = TurnTranscript()
        let assistantID = transcript.recordSubmittedTurn("hi")

        transcript.apply(.failed(code: "turnInProgress", message: "a turn is already running", recoverable: true))

        let entry = transcript.entries.first { $0.id == assistantID }
        #expect(entry?.additionalFailureDetail == "a turn is already running (turnInProgress)")
    }

    /// The no-open-turn branch of `apply(_:)`'s `.failed` case already writes `message` into the
    /// synthesised `.notice` entry's `text` (`aFailureWithNoOpenTurnAppendsANoticeEntryInFailed
    /// State` above) — so `additionalFailureDetail` must stay `nil` here, or the row would show
    /// the same reason twice.
    @Test
    func additionalFailureDetailIsNilForANoticeEntrySynthesisedFromAFailureWithNoOpenTurn() {
        var transcript = TurnTranscript()

        transcript.apply(.failed(code: "internalError", message: "unexpected", recoverable: false))

        let entry = transcript.entries[0]
        #expect(entry.role == .notice)
        #expect(entry.text == "unexpected")
        #expect(entry.additionalFailureDetail == nil)
    }

    /// `recordSubmissionRefused(_:reason:)` always writes `reason` into the notice entry's `text`
    /// (`recordSubmissionRefusedKeepsTheTypedTextRecordsTheReasonAndLeavesNoOpenTurn` above) — so
    /// this must stay `nil` too, for the same reason.
    @Test
    func additionalFailureDetailIsNilForARefusedNoticeEntry() {
        var transcript = TurnTranscript()

        transcript.recordSubmissionRefused("hi", reason: "no session is attached")

        let entry = transcript.entries[1]
        #expect(entry.role == .notice)
        #expect(entry.state == .refused(reason: "no session is attached"))
        #expect(entry.additionalFailureDetail == nil)
    }
}
