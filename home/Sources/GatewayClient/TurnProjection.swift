import Foundation

/// What one inbound wire item renders as, for a turn submitted via
/// `GatewaySession.submitTurn(_:)`. Produced only by `TurnProjection
/// .project(_:)` — see that type's doc comment for what this deliberately
/// does not track.
public enum TurnEvent: Hashable, Sendable {
    /// `{"type":"turnStart"}`.
    case turnStarted

    /// The inner `delta.delta` string from a `messageDelta` event whose
    /// `delta.type` is `"textDelta"` — see `TurnProjection.project(_:)`'s
    /// doc comment for why every other `delta.type` projects to `nil`
    /// instead of a case here.
    case textDelta(String)

    /// `{"type":"turnEnd", ...}`. Terminal — no further `TurnEvent`
    /// belongs to the turn that produced it.
    case turnEnded

    /// `{"type":"error","code":...,"message":...,"recoverable":...}`.
    /// Terminal in the sense that matters — no `TurnEvent` for the same
    /// turn follows it — but **not always the *only* `TurnEvent` the turn
    /// produces**, and a consumer must not assume it arrives before any
    /// rendering state exists. Three real shapes reach this case, all
    /// traced against `core/Dmon.Core/Rpc/TurnHandler.cs` and
    /// `CommandDispatcher.cs`:
    ///
    /// - **`code: "turnInProgress"`** — a concurrent submit.
    ///   `TurnHandler.SubmitAsync` fails its gate and emits this *instead
    ///   of* `turnStart`/`turnEnd`; the turn produces this case alone.
    /// - **A mid-turn failure** — any exception other than
    ///   `OperationCanceledException` escaping the streaming loop inside
    ///   `RunTurnAsync`. That loop's own `try` catches only
    ///   `OperationCanceledException`; anything else propagates up through
    ///   `SubmitAsync` (whose `try` has a `finally` releasing the turn
    ///   gate, but no `catch`) to `CommandDispatcher.RunGuardedAsync`'s
    ///   `catch (Exception ex)`, which emits `ErrorEvent{code:
    ///   "internalError", recoverable: false}` — **after** `turnStart`,
    ///   and after any number of `.textDelta`s, have already gone out.
    ///   `turnEnd` is never reached. A consumer that has already rendered
    ///   partial text when this case arrives must finalise or discard that
    ///   partial rendering itself — this projection does not, and cannot,
    ///   retract what it has already yielded.
    /// - **A cancellation observed once `RunTurnAsync`'s loop is already
    ///   running** does not end here: that loop's own `try` catches
    ///   `OperationCanceledException` specifically, sets a "cancelled" stop
    ///   reason, and still falls through to `turnEnd` — so a cancellation
    ///   in that window produces `.turnEnded`, not `.failed`.
    ///
    /// **A fourth shape reaches neither case, and is the one a consumer
    /// must not overlook: any cancellation observed before `TurnStartEvent`
    /// actually reaches the wire.** `turn.abort` dispatches inline through
    /// `CommandDispatcher.RunGuardedAsync`, independent of `turn.submit`'s
    /// own background task, and cancels `SubmitAsync`'s own
    /// `CancellationTokenSource`. Two distinct windows land here, not one:
    ///
    ///   - On the **first** turn only, `SubmitAsync` runs asset
    ///     provisioning and awaits `_systemPromptBuilder.BuildAsync(_turnCts
    ///     .Token)` *before* `RunTurnAsync` is ever called — a cancellation
    ///     there is the obvious case.
    ///   - On **every** turn, a narrower race against `TurnStartEvent`'s
    ///     own emit call: that emit sits *before* `RunTurnAsync`'s
    ///     `while(true)` loop begins, so it is outside the loop's own
    ///     cancellation catch, and `EventEmitter.EmitAsync`'s first action
    ///     is `await _gate.WaitAsync(cancellationToken)` — which throws
    ///     `OperationCanceledException` immediately if the token is
    ///     already cancelled, whether or not the gate itself is free.
    ///
    /// A cancellation landing in either window escapes `SubmitAsync`
    /// uncaught (its `try` has a `finally` releasing the turn gate, but no
    /// `catch`) and is swallowed silently by `RunGuardedAsync`'s own `catch
    /// (OperationCanceledException) { }` — no `error`, no `turnStart`, no
    /// `turnEnd`, nothing on the wire at all. A consumer that relies
    /// solely on a terminal `TurnEvent` to know a submit has finished will
    /// wait indefinitely for that one.
    case failed(code: String, message: String, recoverable: Bool)
}

/// A stateless mapping from one `GatewayInboundItem` to the `TurnEvent`, if
/// any, it renders as.
///
/// **Stateless by construction**, not merely by current implementation:
/// `project(_:)` takes one item and returns one optional result, with no
/// stored notion of "a turn is in flight" or "how many deltas seen so far"
/// anywhere in this type. The raw `.event` items this projects from stay on
/// `GatewaySession`'s own stream unchanged — this type only ever reads one,
/// it never consumes or replaces the stream — so a consumer that needs to
/// know whether it is mid-turn tracks that itself, from the `TurnEvent`
/// sequence it observes, rather than trusting a second, independent copy of
/// that state kept in here. A stateful projection would be exactly the kind
/// of silent-desync bug section 7's `GatewaySession` has already spent three
/// blocks guarding its own cursor against.
///
/// An item this type does not recognise — a control frame, a `messageDelta`
/// whose `delta.type` is not `"textDelta"`, an ADR-003 event `type` this
/// client does not model, or event text that fails to parse as JSON —
/// projects to `nil`, never a placeholder case: nothing is lost by that,
/// since the raw item the caller passed in is still exactly what it was.
/// This differs from `GatewayFrame.unrecognizedControl` (`ControlFrame
/// .swift`), which has to exist as its own outcome because a mis-routed
/// *control* frame would corrupt `GatewaySession`'s sequence counter — no
/// such counter is at stake here.
public enum TurnProjection {
    /// Projects one inbound item to a `TurnEvent`, or `nil` if `item` is
    /// not one this type models.
    ///
    /// Never throws and never traps: `item` sits on the same untrusted
    /// network boundary `ControlFrameCodec.decode(_:)` tolerates malformed
    /// input on, and this follows the same precedent — parse failures and
    /// unrecognised shapes are `nil`, not thrown errors.
    public static func project(_ item: GatewayInboundItem) -> TurnEvent? {
        guard case .event(let raw) = item else {
            return nil
        }
        guard let object = jsonObject(raw), let type = object["type"] as? String else {
            return nil
        }

        switch type {
        case "turnStart":
            return .turnStarted
        case "turnEnd":
            return .turnEnded
        case "error":
            return failedEvent(from: object)
        case "messageDelta":
            return textDeltaEvent(from: object)
        default:
            return nil
        }
    }

    private static func failedEvent(from object: [String: Any]) -> TurnEvent? {
        guard
            let code = object["code"] as? String,
            let message = object["message"] as? String,
            let recoverable = object["recoverable"] as? Bool
        else {
            return nil
        }
        return .failed(code: code, message: message, recoverable: recoverable)
    }

    /// Reads `delta.delta` — the inner delta object's own `"delta"`
    /// member — never the outer `messageDelta` event's top-level
    /// `"delta"` member, which is the object itself, not a string. Only
    /// when that inner object's `"type"` is `"textDelta"`; every other
    /// inner delta kind (`textStart`, `textEnd`, `thinkingDelta`,
    /// `toolCallDelta`, `done`, `error`, …) projects to `nil` here — this
    /// block's scope is rendered text only.
    private static func textDeltaEvent(from object: [String: Any]) -> TurnEvent? {
        guard
            let delta = object["delta"] as? [String: Any],
            delta["type"] as? String == "textDelta",
            let text = delta["delta"] as? String
        else {
            return nil
        }
        return .textDelta(text)
    }

    private static func jsonObject(_ raw: String) -> [String: Any]? {
        guard let data = raw.data(using: .utf8) else {
            return nil
        }
        let parsed = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        return parsed as? [String: Any]
    }
}
