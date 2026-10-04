// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "Nova",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "NovaCore"),
        .executableTarget(name: "Nova", dependencies: ["NovaCore"]),
        .executableTarget(name: "NovaSelfTest", dependencies: ["NovaCore"]),
        // Root-side config allowlist (installed next to novactl).
        .executableTarget(name: "NovaGuard", dependencies: ["NovaCore"]),
    ]
)
