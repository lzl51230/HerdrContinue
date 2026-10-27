#!/usr/bin/env python3
"""Validate SMAppService, the real Login Items list, and native login events."""
import argparse
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import time
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("--app-bundle", type=Path, required=True)
parser.add_argument("--output-directory", type=Path, required=True)
args = parser.parse_args()
source = args.app_bundle.resolve()
assert "WATCHDOG_QA_SESSION".encode("utf-16le") in (source / "Contents/MacOS/HerdrAutoContinue.dll").read_bytes(), "Debug bundle required"
output = args.output_directory.resolve()
assert not (output / "result.json").exists(), "Use a fresh output directory"
output.mkdir(parents=True, exist_ok=True)
token = uuid.uuid4().hex[:8]
root = Path.home() / "Applications" / ("watchdog-qa-native-" + token)
app = root / "Herdr Auto Continue.app"
state = output / ("watchdog-qa-" + token)
state.mkdir()
root.mkdir()
subprocess.run(["ditto", str(source), str(app)], check=True)
info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
bundle_id = "local.alex.herdr-auto-continue.qa." + token
info["CFBundleIdentifier"] = bundle_id
(app / "Contents/Info.plist").write_bytes(plistlib.dumps(info))
initial = {"WATCHDOG_QA_SESSION": "watchdog-qa-native-" + token, "WATCHDOG_QA_SETTINGS_DIR": str(state),
           "WATCHDOG_QA_SCENARIO": "native-register", "WATCHDOG_QA_AUTOMATION_DIR": str(output / "register")}
(app / "Contents/Resources/watchdog-qa.json").write_text(json.dumps(initial))
for file in (app / "Contents/MacOS").iterdir():
    if file.is_file() and file.name != "HerdrAutoContinue":
        subprocess.run(["codesign", "--force", "--sign", "-", str(file)], capture_output=True, check=True)
subprocess.run(["codesign", "--force", "--sign", "-", str(app)], capture_output=True, check=True)
subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
launcher = Path(__file__).with_name("qa-launch-macos.swift")
result = {"bundleIdentifier": bundle_id, "app": str(app), "stages": {}, "success": False}


def run(command, **kwargs):
    return subprocess.run(command, check=True, capture_output=True, text=True, **kwargs).stdout


def wait(condition, seconds=30):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = condition()
        if value:
            return value
        time.sleep(.1)
    raise TimeoutError("Native startup QA condition timed out")


def stage(scene, folder):
    folder.mkdir(exist_ok=True)
    text = run(["swift", str(launcher), str(app), str(state), scene, str(folder)])
    (folder / "launch.log").write_text(text)


def finish(folder):
    wait(lambda: (folder / "result.json").exists(), 100)
    report = json.loads((folder / "result.json").read_text())
    assert report["success"], report
    def exited():
        try:
            os.kill(report["pid"], 0)
            return False
        except ProcessLookupError:
            return True
    wait(exited, 10)
    result["stages"][folder.name] = report


def list_count():
    script = '''tell application "System Events"
set settingsApp to first application process whose bundle identifier is "com.apple.systempreferences"
set uiItems to entire contents of window 1 of settingsApp
set matches to 0
repeat with uiItem in uiItems
try
if (get role of uiItem) is "AXStaticText" and (get name of uiItem) is "Herdr Auto Continue" then set matches to matches + 1
end try
end repeat
return matches
end tell'''
    return int(run(["osascript", "-e", script]).strip())


def refresh_list():
    run(["open", "-g", "x-apple.systempreferences:com.apple.General-Settings.extension"])
    time.sleep(1)
    run(["open", "-g", "x-apple.systempreferences:com.apple.LoginItems-Settings.extension"])
    time.sleep(1)


def snapshot(name, pid=None):
    swift = '''import AppKit
import CoreGraphics
if let app = NSRunningApplication.runningApplications(withBundleIdentifier:"com.apple.systempreferences").first {
 let windows = CGWindowListCopyWindowInfo(.optionAll,kCGNullWindowID) as? [[String:Any]] ?? []
 for w in windows where (w[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == app.processIdentifier && (w[kCGWindowLayer as String] as? Int) == 0 && !(w[kCGWindowName as String] as? String ?? "").isEmpty { print(w[kCGWindowNumber as String]!); break }
}'''
    if pid is not None:
        swift = '''import AppKit
import CoreGraphics
let windows = CGWindowListCopyWindowInfo(.optionAll,kCGNullWindowID) as? [[String:Any]] ?? []
for w in windows where (w[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == QA_PROCESS_ID {
 if let r = w[kCGWindowBounds as String] as? [String:Double], (r["Width"] ?? 0) > 200, (r["Width"] ?? 0) < 700, (r["Height"] ?? 0) > 100, (r["Height"] ?? 0) < 600 { print(w[kCGWindowNumber as String]!); break }
}'''.replace("QA_PROCESS_ID", str(pid))
    number = wait(lambda: run(["swift", "-"], input=swift).strip(), 10)
    run(["/usr/sbin/screencapture", "-l", number, "-x", "-o", str(output / name)])



try:
    register = output / "register"
    stage("native-register", register)
    wait(lambda: (register / "before-register-ready.json").exists())
    refresh_list()
    baseline = list_count()
    result["baselineListRows"] = baseline
    (register / "begin-register").touch()
    wait(lambda: (register / "registered-ready.json").exists())
    refresh_list()
    result["registeredListRows"] = list_count()
    wait(lambda: list_count() == baseline + 1)
    snapshot("system-login-item-enabled.png")
    blocks = re.split(r"\n #\d+:", run(["/usr/bin/sfltool", "dumpbtm"]))
    own = next(block for block in blocks if "Bundle Identifier: " + bundle_id in block)
    assert str(root) in own and "enabled" in own
    (output / "registered-item.txt").write_text(own)
    result["systemListVisibleAndPathMatched"] = True
    (register / "release").touch()
    finish(register)
    login = output / "login"
    stage("native-system", login)
    wait(lambda: (login / "stopped-ready.json").exists())
    stage("native-ping", output / "ping")
    (login / "repeat-login").touch()
    wait(lambda: (login / "repeat-confirmed.json").exists())
    stage("native-manual", output / "manual-open")
    (login / "manual-open").touch()
    finish(login)
    unregister = output / "unregister"
    stage("native-unregister", unregister)
    wait(lambda: (unregister / "unregistered-ready.json").exists())
    refresh_list()
    wait(lambda: list_count() == baseline)
    snapshot("system-login-item-removed.png")
    result["removedFromSystemList"] = True
    (unregister / "release").touch()
    finish(unregister)
    for name in ("prompt-later", "prompt-settings"):
        folder = output / name
        stage("native-prompt", folder)
        wait(lambda: (folder / "prompt-ready.json").exists())
        pid = json.loads((folder / "prompt-ready.json").read_text())["pid"]
        snapshot(name + ".png", pid)
        finish(folder)
    result["approvalPromptUIExercised"] = True
    result["approvalStateCoveredByUnitFixtures"] = True
    result["success"] = True
except Exception as error:
    result["error"] = str(error)
    raise
finally:
    # Unregister from inside the QA bundle even when an assertion interrupted a stage.
    for marker in ("register/release", "unregister/release", "login/repeat-login", "login/manual-open"):
        path = output / marker
        path.parent.mkdir(exist_ok=True)
        path.touch()
    cleanup = output / "cleanup"
    cleanup.mkdir(exist_ok=True)
    (cleanup / "release").touch()
    try:
        for prompt in (output / "prompt-later", output / "prompt-settings"):
            if (prompt / "prompt-ready.json").exists() and not (prompt / "result.json").exists():
                wait(lambda: (prompt / "result.json").exists(), 10)
        time.sleep(.5)
        stage("native-unregister", cleanup)
        finish(cleanup)
        shutil.rmtree(root)
    except Exception as error:
        result["success"] = False
        result["cleanupError"] = str(error)
        raise
    finally:
        (output / "result.json").write_text(json.dumps(result, indent=2))
print(json.dumps(result, indent=2))
