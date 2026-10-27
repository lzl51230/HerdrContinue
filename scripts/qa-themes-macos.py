#!/usr/bin/env python3
"""Run theme UI coverage using an isolated self-contained Debug app."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("--app-bundle", type=Path, required=True)
parser.add_argument("--output-directory", type=Path, required=True)
args = parser.parse_args()
app = args.app_bundle.resolve() / "Contents/MacOS/HerdrAutoContinue"
assert "WATCHDOG_QA_SESSION".encode("utf-16le") in app.with_suffix(".dll").read_bytes(), "Debug build required"
output = args.output_directory.resolve()
output.mkdir(parents=True, exist_ok=True)
scope = "watchdog-qa-theme-" + uuid.uuid4().hex[:8]
state = output / scope
state.mkdir()
env = os.environ | {"WATCHDOG_QA_SESSION": scope, "WATCHDOG_QA_SETTINGS_DIR": str(state)}
results = {}
for stage, scenario, hidden in (("themes", "themes", False), ("restart", "theme-restart", False), ("hidden", "theme-restart", True)):
    folder = output / stage
    folder.mkdir(exist_ok=True)
    stage_env = env | {"WATCHDOG_QA_SCENARIO": scenario, "WATCHDOG_QA_AUTOMATION_DIR": str(folder)}
    with (folder / "process.log").open("wb") as log:
        process = subprocess.Popen([str(app), *(["--autostart"] if hidden else [])], env=stage_env, stdout=log, stderr=subprocess.STDOUT)
        try:
            assert process.wait(timeout=60) == 0, "Application failed"
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
    result = json.loads((folder / "result.json").read_text())
    results[stage] = result
    assert result["success"], result
(output / "result.json").write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
