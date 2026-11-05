#!/usr/bin/env python3
"""Run isolated native lifetime and view-model regression checks, optionally with a GUI soak."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--seconds", type=int, default=0)
    parser.add_argument("--stock", action="store_true", help="Use the official native library: the lifetime check is expected to fail.")
    args = parser.parse_args()
    if args.seconds < 0:
        parser.error("Duration must be nonnegative.")
    output = args.output_directory.resolve()
    output.mkdir(parents=True, exist_ok=True)
    app = output / "app"
    # Stock and patched runs must use different outputs; a stale root library would mask the bug.
    if args.stock and (app / "libAvaloniaNative.dylib").exists():
        (app / "libAvaloniaNative.dylib").unlink()
    with (output / "qa.log").open("w") as log:
        subprocess.run(["dotnet", "build", str(ROOT / "src/HerdrAutoContinue"), "-c", "Debug", "--no-restore",
                        "-m:1", "-nodeReuse:false", "--output", str(app), f"-p:UsePatchedAvaloniaNative={str(not args.stock).lower()}"],
                       cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
        completed = subprocess.run(["dotnet", str(app / "HerdrAutoContinue.dll"), "--qa-memory", str(output),
                                    "--qa-memory-seconds", str(args.seconds)],
                                   cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, timeout=max(120, args.seconds + 120))
    report = json.loads((output / "result.json").read_text())
    print(json.dumps(report, ensure_ascii=False, indent=2))
    sys.exit(completed.returncode if completed.returncode else (0 if report.get("success") else 1))
