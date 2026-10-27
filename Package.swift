// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "HerdrAutoContinue",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "HerdrAutoContinue", targets: ["HerdrAutoContinue"]),
        .executable(name: "WatchdogProbe", targets: ["WatchdogProbe"])
    ],
    targets: [
        .target(name: "WatchdogCore"),
        .executableTarget(name: "HerdrAutoContinue", dependencies: ["WatchdogCore"]),
        .executableTarget(name: "WatchdogProbe", dependencies: ["WatchdogCore"]),
        .testTarget(name: "WatchdogCoreTests", dependencies: ["WatchdogCore"])
    ],
    swiftLanguageModes: [.v5]
)
