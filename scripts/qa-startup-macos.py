#!/usr/bin/env python3
"""Exercise an isolated Debug bundle through the actual per-user launch agent."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import time
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("--app-bundle", type=Path, required=True)
parser.add_argument("--output-directory", type=Path, required=True)
parser.add_argument("--fake-agent", type=Path)
args = parser.parse_args()
assembly = (args.app_bundle / "Contents/MacOS/HerdrAutoContinue.dll").read_bytes()
assert "WATCHDOG_QA_SESSION".encode("utf-16le") in assembly, "A Debug QA build is required"
token = uuid.uuid4().hex[:8]
scope = "watchdog-qa-startup-" + token
home = Path.home()
app_root = home / "Applications" / ("watchdog-qa-" + token)
app = app_root / "Herdr Auto Continue.app"
state = home / "Library/Application Support/HerdrAutoContinue/QA" / ("watchdog-qa-" + token)
output = args.output_directory.resolve()
output.mkdir(parents=True, exist_ok=True)
state.mkdir(parents=True)
app_root.mkdir(parents=True)
subprocess.run(["ditto", str(args.app_bundle.resolve()), str(app)], check=True)
label = "local.alex.HerdrAutoContinue-QA-" + hashlib.sha256(scope.encode()).hexdigest().upper()[:12]
entry = home / "Library/LaunchAgents" / (label + ".plist")
domain = "gui/" + str(os.getuid())
env = os.environ.copy()
for key in ("HERDR_SESSION", "HERDR_SOCKET_PATH", "HERDR_PANE_ID", "HERDR_WORKSPACE_ID", "HERDR_TAB_ID"):
    env.pop(key, None)
env.update(WATCHDOG_QA_SESSION=scope, WATCHDOG_QA_SETTINGS_DIR=str(state))
processes = []
server = None
herdr = shutil.which("herdr")
result = {"session": scope, "stages": {}}


def command(arguments):
    return subprocess.run(arguments, env=env, text=True, capture_output=True, check=True).stdout


def wait_for(condition, seconds=30):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if condition():
            return
        time.sleep(0.05)
    raise TimeoutError("QA condition did not become true")


def native_hidden(pid):
    source = """
import AppKit
import CoreGraphics
import Foundation
let pid = Int32(CommandLine.arguments[1])!
guard let app = NSRunningApplication(processIdentifier: pid) else { exit(1) }
let windows = CGWindowListCopyWindowInfo(.optionOnScreenOnly, kCGNullWindowID) as? [[String: Any]] ?? []
let visible = windows.filter { ($0[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == pid && ($0[kCGWindowLayer as String] as? NSNumber)?.intValue == 0 }
guard visible.isEmpty, app.activationPolicy == .accessory else { exit(2) }
print("native window count=0; Dock policy=accessory")
"""
    subprocess.run(["/usr/bin/swift", "-", str(pid)], input=source, text=True, check=True, capture_output=True)


def stage(scenario, hidden=False):
    folder = state / scenario
    stage_env = env | {"WATCHDOG_QA_SCENARIO": scenario, "WATCHDOG_QA_AUTOMATION_DIR": str(folder)}
    folder.mkdir(exist_ok=True)
    with (folder / "process.log").open("wb") as log:
        proc = subprocess.Popen([str(app / "Contents/MacOS/HerdrAutoContinue"), *(["--autostart"] if hidden else [])], env=stage_env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        processes.append(proc)
        if hidden:
            wait_for(lambda: (folder / "background-ready.json").exists())
            native_hidden(proc.pid)
        assert proc.wait(timeout=40) == 0
    report = json.loads((folder / "result.json").read_text())
    assert report["success"], report
    result["stages"][scenario] = report


try:
    if args.fake_agent:
        assert herdr, "Herdr CLI is required for the agent scenario"
        check = subprocess.run(["pgrep", "-f", "^/Applications/Herdr Auto Continue.app/Contents/MacOS/HerdrAutoContinue"], capture_output=True)
        assert check.returncode != 0, "An existing production app could interfere with the agent fixture"
        server_log = (state / "server.log").open("wb")
        server = subprocess.Popen([herdr, "--session", scope, "server"], env=env, stdout=server_log, stderr=subprocess.STDOUT)
        def server_ready():
            sessions = json.loads(command([herdr, "session", "list", "--json"]))["sessions"]
            return any(s["name"] == scope and s["running"] for s in sessions)
        wait_for(server_ready)
        workspace = json.loads(command([herdr, "--session", scope, "workspace", "create", "--label", "Startup QA", "--cwd", str(state), "--no-focus"]))
        pane = workspace["result"]["root_pane"]["pane_id"]
        time.sleep(0.5)
        dotnet_root = Path(shutil.which("dotnet")).resolve().parent
        actor = "env DOTNET_ROOT=" + shlex.quote(str(dotnet_root)) + " " + shlex.quote(str(args.fake_agent.resolve())) + " " + shlex.quote(str(state / "receipt.jsonl"))
        command([herdr, "--session", scope, "pane", "run", pane, actor])
        wait_for(lambda: any(a.get("agent_status") == "idle" for a in json.loads(command([herdr, "--session", scope, "agent", "list"]))["result"]["agents"]))
        (state / "expect-agent").touch()
    stage("startup-enable")
    subprocess.run(["plutil", "-lint", str(entry)], check=True, capture_output=True)
    began = time.monotonic()
    subprocess.run(["launchctl", "bootstrap", domain, str(entry)], check=True, capture_output=True)
    folder = state / "system-login"
    wait_for(lambda: (folder / "background-ready.json").exists())
    ready = json.loads((folder / "background-ready.json").read_text())
    native_hidden(ready["pid"])
    wait_for(lambda: (folder / "result.json").exists(), 40)
    report = json.loads((folder / "result.json").read_text())
    assert report["success"], report
    result["stages"]["system-login"] = report
    if args.fake_agent:
        receipt = (state / "receipt.jsonl").read_text().splitlines()
        assert len(receipt) == 1 and json.loads(receipt[0])["receivedContinueAndNewline"]
        assert time.monotonic() - began >= 10
        result["singleAutomaticContinuation"] = True
    stage("startup-manual")
    stage("startup-os-shutdown")
    stage("startup-disable")
    assert not entry.exists()
    stage("startup-off")
    stage("startup-off-hidden", True)
    result["success"] = True
finally:
    subprocess.run(["launchctl", "bootout", domain + "/" + label], capture_output=True)
    entry.unlink(missing_ok=True)
    for proc in processes:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
    if server:
        for arguments in ([herdr, "session", "stop", scope, "--json"], [herdr, "session", "delete", scope, "--json"]):
            subprocess.run(arguments, env=env, capture_output=True)
        if server.poll() is None:
            server.terminate()
        server.wait(timeout=5)
        server_log.close()
    shutil.copytree(state, output / "state", dirs_exist_ok=True)
    (output / "result.json").write_text(json.dumps(result, indent=2))
    shutil.rmtree(app_root)
    shutil.rmtree(state)
print(json.dumps(result, indent=2))
