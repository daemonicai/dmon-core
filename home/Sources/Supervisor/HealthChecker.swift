import Foundation

/// Performs an HTTP GET against `url` with no bound of its own — the caller
/// races this against the check's timeout, so this function is free to hang.
///
/// Mirrors dmonium's `EndpointHealthProbe` classification: any HTTP response,
/// including 4xx/5xx, counts as reachable; a connection failure does not.
/// Cancellation (from `withTimeout`'s losing side) is honoured by
/// `URLSession`'s async data API, which cancels the in-flight request when
/// its enclosing task is cancelled.
public func defaultHTTPReachabilityProbe(_ url: URL) async -> Bool {
    var request = URLRequest(url: url)
    request.httpMethod = "GET"
    do {
        let (_, response) = try await URLSession.shared.data(for: request)
        return response is HTTPURLResponse
    } catch {
        return false
    }
}

/// Runs a single `HealthCheck`, bounded externally by its declared timeout.
///
/// A conformer is never trusted to police its own duration: `check` races the
/// probe seam against `timeout` itself (`withTimeout`), so a probe that simply
/// runs long yields a result rather than blocking forever. As `withTimeout`
/// documents, that bound holds only for a probe that cooperates with
/// cancellation once it loses the race — `URLSession`'s async API does, so it
/// holds for `.http` today. A future `.tcp` or `.process` conformer (a raw
/// socket read, a blocking `Process.run()` + `waitUntilExit()`) gets no such
/// guarantee for free: it must itself observe cancellation for the race to
/// actually bound it.
public struct HealthChecker: Sendable {
    private let httpProbe: @Sendable (URL) async -> Bool

    public init(httpProbe: @escaping @Sendable (URL) async -> Bool = defaultHTTPReachabilityProbe) {
        self.httpProbe = httpProbe
    }

    /// - Returns: `.healthy` or `.unhealthy` for an executed `.http` check;
    ///   `.unhealthy` if the check does not complete within `timeout`;
    ///   `.unknown` for `.tcp`, `.process` and `.none` — kinds this checker
    ///   does not yet execute, or that declare no check at all. `.unknown` is
    ///   deliberate: it asserts nothing, where `.healthy` or `.unhealthy`
    ///   would each assert an observation that was never made.
    public func check(_ healthCheck: HealthCheck, timeout: TimeInterval) async -> ChildHealth {
        switch healthCheck {
        case .http(let url):
            let httpProbe = self.httpProbe
            let reachable = await withTimeout(timeout) { await httpProbe(url) }
            return reachable == true ? .healthy : .unhealthy

        case .tcp, .process, .none:
            return .unknown
        }
    }
}
