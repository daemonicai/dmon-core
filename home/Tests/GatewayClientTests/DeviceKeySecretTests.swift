import Foundation
import Testing
@testable import GatewayClient

/// Pins `DeviceKeySecret.secretHash` against the C# writer
/// (`test/Dmon.Network.Tests/PerDeviceKeyE2ETests.cs:251-252`):
///
/// ```csharp
/// Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
/// ```
///
/// Any divergence between this CryptoKit computation and that one must
/// fail loudly here (design risk 2) rather than surface as a client that
/// looks correct but cannot authenticate.
@Suite
struct DeviceKeySecretTests {
    /// Expected digest independently derived with
    /// `printf '%s' token1 | shasum -a 256`, matching the C# expression
    /// above — not copied from this type's own output.
    @Test
    func secretHashOfToken1MatchesTheCSharpWriter() {
        let hash = DeviceKeySecret.secretHash(ofToken: "token1")
        #expect(hash == "df3e6b0bb66ceaadca4f84cbc371fd66e04d20fe51fc414da8d1b84d31d178de")
    }

    /// Independently derived with
    /// `printf '%s' another-token-2 | shasum -a 256`.
    @Test
    func secretHashOfASecondTokenMatchesTheCSharpWriter() {
        let hash = DeviceKeySecret.secretHash(ofToken: "another-token-2")
        #expect(hash == "25900f0d6928f1e521f9862b028f0fcc22296b4f6295d8b1165993e0a6a2371a")
    }

    /// Independently derived with `printf '%s' "" | shasum -a 256` — the
    /// well-known SHA-256-of-empty-string digest.
    @Test
    func secretHashOfTheEmptyStringMatchesTheCSharpWriter() {
        let hash = DeviceKeySecret.secretHash(ofToken: "")
        #expect(hash == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
    }

    /// Asserts against both the static delegate (to pin the delegation
    /// itself) and the pinned literal from
    /// `secretHashOfToken1MatchesTheCSharpWriter` above (so the instance
    /// path is anchored to ground truth, not only to its own delegate —
    /// a change that broke both computations identically would still
    /// pass an equality-only check against the delegate).
    @Test
    func instanceSecretHashDelegatesToTheStaticComputation() {
        let secret = DeviceKeySecret(keyId: "device-1", secret: "token1")
        #expect(secret.secretHash == DeviceKeySecret.secretHash(ofToken: "token1"))
        #expect(secret.secretHash == "df3e6b0bb66ceaadca4f84cbc371fd66e04d20fe51fc414da8d1b84d31d178de")
    }

    // MARK: - Redaction

    @Test
    func stringInterpolationDoesNotExposeTheSecret() {
        let secret = DeviceKeySecret(keyId: "device-1", secret: "super-secret-token")
        #expect(!"\(secret)".contains("super-secret-token"))
    }

    @Test
    func stringDescribingDoesNotExposeTheSecret() {
        let secret = DeviceKeySecret(keyId: "device-1", secret: "super-secret-token")
        #expect(!String(describing: secret).contains("super-secret-token"))
    }

    @Test
    func stringReflectingDoesNotExposeTheSecret() {
        let secret = DeviceKeySecret(keyId: "device-1", secret: "super-secret-token")
        #expect(!String(reflecting: secret).contains("super-secret-token"))
    }

    @Test
    func descriptionStillIdentifiesWhichKeyByKeyId() {
        let secret = DeviceKeySecret(keyId: "device-1", secret: "super-secret-token")
        #expect(String(describing: secret).contains("device-1"))
    }
}
