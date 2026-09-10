// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "CodexUsageBar",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "CodexUsageBar", targets: ["CodexUsageBar"])],
    targets: [
        .systemLibrary(name: "CSQLite", pkgConfig: "sqlite3"),
        .target(name: "UsageCore"),
        .executableTarget(name: "CodexUsageBar", dependencies: ["UsageCore", "CSQLite"]),
        .testTarget(name: "UsageCoreTests", dependencies: ["UsageCore"])
    ]
)
