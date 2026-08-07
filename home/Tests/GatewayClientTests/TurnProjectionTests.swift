import Foundation
import Testing
@testable import GatewayClient

/// Exercises `TurnProjection.project(_:)` directly against fixtures built
/// from the real wire shapes (`core/Dmon.Protocol/Events/TurnEvents.cs`,
/// `Delta/MessageDelta.cs`) — no `GatewaySession` or transport involved.
@Suite
struct TurnProjectionTests {
    @Test
    func turnStartProjectsToTurnStarted() {
        let item = GatewayInboundItem.event(#"{"type":"turnStart"}"#)

        #expect(TurnProjection.project(item) == .turnStarted)
    }

    @Test
    func turnEndProjectsToTurnEnded() {
        let item = GatewayInboundItem.event(
            #"{"type":"turnEnd","message":{},"toolResults":[]}"#
        )

        #expect(TurnProjection.project(item) == .turnEnded)
    }

    @Test
    func anErrorEventProjectsToFailedWithItsCodeMessageAndRecoverable() {
        let item = GatewayInboundItem.event(
            #"{"type":"error","code":"turnInProgress","message":"a turn is already running","recoverable":true}"#
        )

        #expect(TurnProjection.project(item) == .failed(
            code: "turnInProgress",
            message: "a turn is already running",
            recoverable: true
        ))
    }

    /// Pins the hazard the brief calls out by name: `messageDelta` has a
    /// top-level `"message"` and a top-level `"delta"` — and that `"delta"`
    /// object itself has a member literally named `"delta"`. This fixture
    /// is exactly the real shape (`MessageDeltaEvent` wrapping a
    /// `TextDeltaDelta`) so a projection that read the outer `"delta"`
    /// member as a string, instead of the inner `delta.delta`, fails this
    /// test rather than merely being untested.
    @Test
    func aTextDeltaMessageDeltaProjectsToTheInnerDeltaStringNotTheOuterEnvelope() {
        let item = GatewayInboundItem.event(
            #"""
            {"type":"messageDelta","message":{"role":"assistant"},"delta":{"type":"textDelta","delta":"hello world","partial":true}}
            """#
        )

        #expect(TurnProjection.project(item) == .textDelta("hello world"))
    }

    @Test
    func aNonTextDeltaMessageDeltaProjectsToNil() {
        let thinkingDelta = GatewayInboundItem.event(
            #"{"type":"messageDelta","message":{},"delta":{"type":"thinkingDelta","delta":"reasoning..."}}"#
        )
        let toolCallDelta = GatewayInboundItem.event(
            #"{"type":"messageDelta","message":{},"delta":{"type":"toolCallDelta","delta":"{}"}}"#
        )
        let doneDelta = GatewayInboundItem.event(
            #"{"type":"messageDelta","message":{},"delta":{"type":"done","reason":"stop"}}"#
        )

        #expect(TurnProjection.project(thinkingDelta) == nil)
        #expect(TurnProjection.project(toolCallDelta) == nil)
        #expect(TurnProjection.project(doneDelta) == nil)
    }

    @Test
    func anUnmodelledEventTypeProjectsToNilNotAPlaceholderCase() {
        let item = GatewayInboundItem.event(
            #"{"type":"toolExecutionStart","callId":"c1","name":"read_file","args":{}}"#
        )

        #expect(TurnProjection.project(item) == nil)
    }

    @Test
    func malformedJSONProjectsToNilRatherThanThrowingOrTrapping() {
        let notJSON = GatewayInboundItem.event("definitely not json{{{")
        let jsonButNotAnObject = GatewayInboundItem.event("42")
        let missingType = GatewayInboundItem.event(#"{"code":"x"}"#)

        #expect(TurnProjection.project(notJSON) == nil)
        #expect(TurnProjection.project(jsonButNotAnObject) == nil)
        #expect(TurnProjection.project(missingType) == nil)
    }

    @Test
    func aControlFrameProjectsToNilNeverToATurnEvent() {
        let item = GatewayInboundItem.control(.ack(AckFrame(id: "abc")))

        #expect(TurnProjection.project(item) == nil)
    }
}
