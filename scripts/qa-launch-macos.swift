import AppKit
import Carbon
import CoreGraphics
let app = URL(fileURLWithPath: CommandLine.arguments[1])
let state = CommandLine.arguments[2]
let scene = CommandLine.arguments[3]
let output = CommandLine.arguments[4]
let config = NSWorkspace.OpenConfiguration()
config.activates = scene != "native-system" && scene != "native-ping"
config.allowsRunningApplicationSubstitution = false
config.environment = ["WATCHDOG_QA_SCENARIO": scene, "WATCHDOG_QA_AUTOMATION_DIR": output]
if scene == "native-system" || scene == "native-ping" {
 let event = NSAppleEventDescriptor(eventClass: AEEventClass(kCoreEventClass), eventID: AEEventID(kAEOpenApplication), targetDescriptor: nil, returnID: AEReturnID(kAutoGenerateReturnID), transactionID: AETransactionID(kAnyTransactionID))
 event.setParam(NSAppleEventDescriptor(enumCode: OSType(keyAELaunchedAsLogInItem)), forKeyword: AEKeyword(keyAEPropData))
 config.appleEvent = event
}
var done = false
var launched: NSRunningApplication?
NSWorkspace.shared.openApplication(at: app, configuration: config) { application, error in
 if let error = error { fputs("\(error)\n", stderr); exit(1) }
 launched = application; done = true
}
while !done { RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.05)) }
guard let running = launched else { exit(2) }
print("pid=\(running.processIdentifier)")
if scene == "native-system" {
 let ready = URL(fileURLWithPath: output).appendingPathComponent("background-ready.json").path
 let deadline = Date(timeIntervalSinceNow: 15)
 while !FileManager.default.fileExists(atPath: ready) && Date() < deadline { RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.05)) }
 guard FileManager.default.fileExists(atPath: ready), running.activationPolicy == .accessory else { exit(3) }
 let windows = CGWindowListCopyWindowInfo(.optionOnScreenOnly, kCGNullWindowID) as? [[String: Any]] ?? []
 let visible = windows.filter { ($0[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == running.processIdentifier && ($0[kCGWindowLayer as String] as? NSNumber)?.intValue == 0 }
 guard visible.isEmpty, NSWorkspace.shared.frontmostApplication?.processIdentifier != running.processIdentifier else { exit(4) }
 print("native login event: no window, no Dock, no focus activation")
}
