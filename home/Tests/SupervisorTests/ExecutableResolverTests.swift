import Foundation
import Testing
@testable import Supervisor

@Suite
struct ExecutableResolverTests {
    @Test
    func emptyCandidatesAreNotDecidedRatherThanUnresolved() {
        let resolver = ExecutableResolver()
        #expect(resolver.resolve([]) == .notDecided)
    }

    @Test
    func environmentVariableCandidateResolvesWhenTheFileIsExecutable() {
        let resolver = ExecutableResolver(
            environment: { ["DMON_TEST_PATH": "/opt/tools/thing"] },
            isExecutableFile: { $0 == "/opt/tools/thing" }
        )
        #expect(resolver.resolve([.environmentVariable("DMON_TEST_PATH")]) == .resolved(path: "/opt/tools/thing"))
    }

    @Test
    func environmentVariableCandidateFallsThroughWhenTheKeyIsAbsent() {
        let resolver = ExecutableResolver(
            environment: { [:] },
            isExecutableFile: { _ in true }
        )
        #expect(resolver.resolve([.environmentVariable("DMON_TEST_PATH")]) == .unresolved)
    }

    @Test
    func homeRelativeCandidateExpandsAgainstTheInjectedHomeDirectory() {
        let resolver = ExecutableResolver(
            homeDirectory: { "/Users/fixture" },
            isExecutableFile: { $0 == "/Users/fixture/.dotnet/tools/ndmon" }
        )
        #expect(resolver.resolve([.homeRelativePath(".dotnet/tools/ndmon")]) == .resolved(path: "/Users/fixture/.dotnet/tools/ndmon"))
    }

    @Test
    func absolutePathCandidateIsUsedVerbatim() {
        let resolver = ExecutableResolver(isExecutableFile: { $0 == "/usr/local/bin/thing" })
        #expect(resolver.resolve([.absolutePath("/usr/local/bin/thing")]) == .resolved(path: "/usr/local/bin/thing"))
    }

    /// The falsifiable half: a resolver that ignored order, or that returned
    /// the first candidate regardless of executability, would return the
    /// override here instead of the fallback.
    @Test
    func firstResolvingCandidateWinsOverLaterOnes() {
        let resolver = ExecutableResolver(
            environment: { ["DMON_TEST_PATH": "/does/not/exist"] },
            homeDirectory: { "/Users/fixture" },
            isExecutableFile: { $0 == "/Users/fixture/.dotnet/tools/ndmon" }
        )
        let candidates: [ExecutableSource] = [.environmentVariable("DMON_TEST_PATH"), .homeRelativePath(".dotnet/tools/ndmon")]
        #expect(resolver.resolve(candidates) == .resolved(path: "/Users/fixture/.dotnet/tools/ndmon"))
    }

    @Test
    func noCandidateResolvingIsUnresolvedRatherThanNotDecided() {
        let resolver = ExecutableResolver(
            environment: { [:] },
            isExecutableFile: { _ in false }
        )
        let candidates: [ExecutableSource] = [.environmentVariable("DMON_TEST_PATH"), .absolutePath("/nowhere")]
        #expect(resolver.resolve(candidates) == .unresolved)
    }

    /// Exercises the real, non-injected default: a genuine executable on
    /// this machine must resolve through `FileManager.default` with no
    /// fakes involved anywhere in the chain.
    @Test
    func defaultWiringResolvesARealExecutableOnDisk() {
        let resolver = ExecutableResolver()
        #expect(resolver.resolve([.absolutePath("/bin/sh")]) == .resolved(path: "/bin/sh"))
    }

    @Test
    func defaultWiringReportsUnresolvedForARealButNonExecutablePath() {
        let resolver = ExecutableResolver()
        #expect(resolver.resolve([.absolutePath("/dev/null")]) == .unresolved)
    }
}
