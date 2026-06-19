import Foundation
import Security

/// Thin wrapper around `SecItem` APIs for generic-password storage.
/// Service constant matches the app bundle; account = env-var name.
enum Keychain {

    private static let service = "ai.daemonic.daemon"

    /// Saves or updates `value` under the given account name.
    /// Returns `true` on success; `false` if the Keychain was unreachable
    /// (e.g. unsigned build running without a valid code-signing identity).
    @discardableResult
    static func save(_ value: String, account: String) -> Bool {
        let data = Data(value.utf8)
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account
        ]
        let existing = SecItemCopyMatching(query as CFDictionary, nil)
        if existing == errSecSuccess {
            let update: [CFString: Any] = [kSecValueData: data]
            let status = SecItemUpdate(query as CFDictionary, update as CFDictionary)
            return status == errSecSuccess
        } else {
            var add = query
            add[kSecValueData] = data
            let status = SecItemAdd(add as CFDictionary, nil)
            return status == errSecSuccess
        }
    }

    /// Reads the value stored under `account`, or `nil` if absent or unavailable.
    static func read(account: String) -> String? {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne
        ]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess,
              let data = result as? Data,
              let string = String(data: data, encoding: .utf8) else {
            return nil
        }
        return string
    }

    /// Deletes the item stored under `account`. Silently ignores missing items.
    static func delete(account: String) {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account
        ]
        SecItemDelete(query as CFDictionary)
    }
}
