import Foundation
import Testing
@testable import Supervisor

@Suite
struct ChildDescriptorTests {
    private static let sample = ChildDescriptor(
        id: "sample",
        displayName: "Sample",
        transport: .loopbackHTTP,
        endpoint: URL(string: "http://127.0.0.1:9999")!,
        healthCheck: .http(URL(string: "http://127.0.0.1:9999")!),
        healthCheckTimeout: 5,
        startupOrder: 0,
        adoptionPolicy: .adoptOrSpawn,
        launch: ChildLaunch(candidates: [.absolutePath("/usr/local/bin/sample")]),
        isEnabled: false
    )

    @Test
    func carriesAllSixDeclaredDimensions() {
        let descriptor = Self.sample
        #expect(descriptor.transport == .loopbackHTTP)
        #expect(descriptor.endpoint == URL(string: "http://127.0.0.1:9999")!)
        #expect(descriptor.healthCheck == .http(URL(string: "http://127.0.0.1:9999")!))
        #expect(descriptor.healthCheckTimeout == 5)
        #expect(descriptor.startupOrder == 0)
        #expect(descriptor.adoptionPolicy == .adoptOrSpawn)
    }

    @Test
    func launchCarriesCandidatesAndArguments() {
        #expect(ChildLaunch().candidates.isEmpty)
        #expect(ChildLaunch().arguments.isEmpty)

        let launch = ChildLaunch(
            candidates: [.environmentVariable("DMON_NETWORK_PATH"), .homeRelativePath(".dotnet/tools/ndmon")],
            arguments: ["--port", "8800"]
        )
        #expect(launch.candidates == [.environmentVariable("DMON_NETWORK_PATH"), .homeRelativePath(".dotnet/tools/ndmon")])
        #expect(launch.arguments == ["--port", "8800"])
    }

    @Test
    func equalDescriptorsCompareEqual() {
        #expect(Self.sample == Self.sample)
    }

    @Test
    func differingStartupOrderComparesUnequal() {
        let other = ChildDescriptor(
            id: Self.sample.id,
            displayName: Self.sample.displayName,
            transport: Self.sample.transport,
            endpoint: Self.sample.endpoint,
            healthCheck: Self.sample.healthCheck,
            healthCheckTimeout: Self.sample.healthCheckTimeout,
            startupOrder: 1,
            adoptionPolicy: Self.sample.adoptionPolicy,
            launch: Self.sample.launch,
            isEnabled: Self.sample.isEnabled
        )
        #expect(Self.sample != other)
    }
}
