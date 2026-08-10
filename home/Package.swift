// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "DmonHome",
    platforms: [
        .macOS(.v14),
        .iOS(.v17)
    ],
    products: [
        .library(name: "Supervisor", targets: ["Supervisor"]),
        .library(name: "GatewayClient", targets: ["GatewayClient"]),
        .library(name: "Power", targets: ["Power"]),
        .library(name: "DeviceKeys", targets: ["DeviceKeys"])
    ],
    targets: [
        .target(
            name: "Supervisor",
            path: "Sources/Supervisor",
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "SupervisorTests",
            dependencies: ["Supervisor"],
            path: "Tests/SupervisorTests",
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .target(
            name: "GatewayClient",
            path: "Sources/GatewayClient",
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "GatewayClientTests",
            dependencies: ["GatewayClient"],
            path: "Tests/GatewayClientTests",
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .target(
            name: "Power",
            path: "Sources/Power",
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "PowerTests",
            dependencies: ["Power"],
            path: "Tests/PowerTests",
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .target(
            name: "DeviceKeys",
            dependencies: ["GatewayClient"],
            path: "Sources/DeviceKeys",
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "DeviceKeysTests",
            dependencies: ["DeviceKeys"],
            path: "Tests/DeviceKeysTests",
            swiftSettings: [.swiftLanguageMode(.v6)]
        )
    ]
)
