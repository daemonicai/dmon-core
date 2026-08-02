import Testing
@testable import Supervisor

@Suite
struct ChildTransportTests {
    @Test
    func distinctCasesAreNotEqual() {
        #expect(ChildTransport.loopbackHTTP != ChildTransport.socket)
    }

    @Test
    func sameCaseIsEqual() {
        #expect(ChildTransport.socket == ChildTransport.socket)
    }
}
