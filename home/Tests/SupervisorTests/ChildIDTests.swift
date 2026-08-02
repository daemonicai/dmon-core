import Testing
@testable import Supervisor

@Suite
struct ChildIDTests {
    @Test
    func rawValueRoundTrips() {
        let id = ChildID(rawValue: "speech")
        #expect(id.rawValue == "speech")
        #expect(ChildID(rawValue: id.rawValue) == id)
    }

    @Test
    func stringLiteralInitializes() {
        let id: ChildID = "audio-engine"
        #expect(id.rawValue == "audio-engine")
    }

    @Test
    func distinctRawValuesAreNotEqual() {
        #expect(ChildID("speech") != ChildID("directedness"))
    }

    @Test
    func descriptionMatchesRawValue() {
        #expect(ChildID("power").description == "power")
    }
}
