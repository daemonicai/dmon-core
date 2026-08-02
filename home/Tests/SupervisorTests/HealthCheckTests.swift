import Foundation
import Testing
@testable import Supervisor

@Suite
struct HealthCheckTests {
    @Test
    func httpChecksCompareByURL() {
        let url = URL(string: "http://127.0.0.1:5500")!
        #expect(HealthCheck.http(url) == .http(url))
        #expect(HealthCheck.http(url) != .http(URL(string: "http://127.0.0.1:5501")!))
    }

    @Test
    func tcpChecksCompareByHostAndPort() {
        #expect(HealthCheck.tcp(host: "127.0.0.1", port: 8800) == .tcp(host: "127.0.0.1", port: 8800))
        #expect(HealthCheck.tcp(host: "127.0.0.1", port: 8800) != .tcp(host: "127.0.0.1", port: 8810))
    }

    @Test
    func processChecksCompareByCommandAndArguments() {
        let check = HealthCheck.process(executablePath: "tailscale", arguments: ["status", "--json"])
        #expect(check == .process(executablePath: "tailscale", arguments: ["status", "--json"]))
        #expect(check != .process(executablePath: "tailscale", arguments: ["up"]))
    }

    @Test
    func noneIsDistinctFromEveryAddressedCase() {
        let url = URL(string: "http://127.0.0.1:5500")!
        #expect(HealthCheck.none != .http(url))
        #expect(HealthCheck.none != .tcp(host: "127.0.0.1", port: 1))
        #expect(HealthCheck.none != .process(executablePath: "x", arguments: []))
    }
}
