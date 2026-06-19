// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "DaemonApp",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .executable(name: "DaemonApp", targets: ["DaemonApp"])
    ],
    targets: [
        .executableTarget(
            name: "DaemonApp",
            path: "Sources/DaemonApp"
        )
    ]
)
