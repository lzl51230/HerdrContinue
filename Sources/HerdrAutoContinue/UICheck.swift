#if DEBUG
import AppKit
import WatchdogCore

/// Development-only window lifecycle check. Not included in the installed release binary.
@MainActor
enum UICheck {
    static var result: [String: Any] = [:]
    static var directory: URL?
    static func run(monitor: Monitor) async {
        guard let path = ProcessInfo.processInfo.environment["HERDR_WATCHDOG_UI_CHECK_DIR"], directory == nil else { return }
        directory = URL(fileURLWithPath: path)
        try? FileManager.default.createDirectory(at: directory!, withIntermediateDirectories: true)
        try? await Task.sleep(nanoseconds: 600_000_000)
        result["initialStopped"] = !monitor.running
        capture("stopped.png")
        monitor.start()
        try? await Task.sleep(nanoseconds: 3_000_000_000)
        result["started"] = monitor.running
        result["agentCount"] = monitor.rows.count
        result["proxyPhase"] = monitor.proxyStatus.phase.rawValue
        result["japanNodeCount"] = monitor.proxyStatus.candidateCount
        result["selectedNode"] = monitor.proxyStatus.selected
        capture("running.png")
        monitor.stop()
        result["stopClearedCountdowns"] = !monitor.running && monitor.rows.allSatisfy { $0.deadline == nil }
        monitor.start()
        try? await Task.sleep(nanoseconds: 300_000_000)
        result["closingWhileRunning"] = monitor.running
        NSApp.windows.first(where: { $0.isVisible })?.performClose(nil)
    }
    static func didTerminate(monitor: Monitor?) {
        guard let directory else { return }
        result["windowCloseStoppedService"] = monitor?.running == false
        result["windowCloseStoppedProxy"] = monitor?.proxyStatus.phase == .stopped
        result["applicationTerminated"] = true
        if let data = try? JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: directory.appendingPathComponent("result.json"))
        }
    }
    private static func capture(_ name: String) {
        guard let view = NSApp.windows.first(where: { $0.isVisible })?.contentView, let directory else { return }
        view.layoutSubtreeIfNeeded()
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        if let data = bitmap.representation(using: .png, properties: [:]) { try? data.write(to: directory.appendingPathComponent(name)) }
    }
}
#endif
