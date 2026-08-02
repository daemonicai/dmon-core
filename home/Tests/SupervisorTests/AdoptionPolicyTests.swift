import Testing
@testable import Supervisor

@Suite
struct AdoptionPolicyTests {
    @Test
    func casesAreDistinct() {
        #expect(AdoptionPolicy.adoptOrSpawn != AdoptionPolicy.spawnOnly)
    }
}
