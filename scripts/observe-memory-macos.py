#!/usr/bin/env python3
"""Observe one existing process without changing its state; report footprint and AX allocations."""
import argparse
import ctypes
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import subprocess
import time


class RUsage(ctypes.Structure):
    # rusage_info_v0, from the macOS SDK's sys/resource.h.
    _fields_ = [("uuid", ctypes.c_uint8 * 16)] + [(name, ctypes.c_uint64) for name in (
        "user_time", "system_time", "pkg_idle_wkups", "interrupt_wkups", "pageins",
        "wired_size", "resident_size", "phys_footprint", "proc_start_abstime", "proc_exit_abstime")]


def usage(pid):
    library = ctypes.CDLL("/usr/lib/libproc.dylib", use_errno=True)
    library.proc_pid_rusage.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_void_p]
    library.proc_pid_rusage.restype = ctypes.c_int
    info = RUsage()
    if library.proc_pid_rusage(pid, 0, ctypes.byref(info)) != 0:
        raise ProcessLookupError(f"Cannot read process {pid}: errno={ctypes.get_errno()}")
    return info


def heap_counts(pid):
    summary = subprocess.check_output(["heap", "-q", str(pid)], text=True, stderr=subprocess.STDOUT, timeout=90)
    counts = {}
    for name in ("AvnAccessibilityElement", "AvnAutomationNode"):
        match = re.search(r"^\s*(\d+)\s+\S+\s+\S+\s+" + name + r"\s+(?:ObjC|C\+\+)", summary, re.MULTILINE)
        if match:
            counts[name] = int(match[1])
    return counts


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--seconds", type=int, default=28800)
    parser.add_argument("--interval", type=int, default=60)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--no-heap", action="store_true")
    args = parser.parse_args()
    if args.seconds < 0 or args.interval < 1:
        parser.error("Duration must be nonnegative and interval positive.")
    directory = args.output_directory.resolve()
    directory.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    identity = usage(args.pid).proc_start_abstime
    report = {"pid": args.pid, "startedAt": datetime.now(timezone.utc).isoformat(), "plannedSeconds": args.seconds, "status": "running", "samples": 0}
    peak = 0
    next_heap = 0.0
    try:
        with (directory / "samples.jsonl").open("w", buffering=1) as samples:
            while True:
                elapsed = time.monotonic() - started
                info = usage(args.pid)
                if info.proc_start_abstime != identity:
                    raise ProcessLookupError("PID was reused; stopping observation.")
                sample = {"seconds": elapsed, "time": datetime.now(timezone.utc).isoformat(), "footprintBytes": info.phys_footprint, "residentBytes": info.resident_size}
                if not args.no_heap and (elapsed >= next_heap or elapsed >= args.seconds):
                    try:
                        sample["heapCounts"] = heap_counts(args.pid)
                    except (subprocess.SubprocessError, OSError) as error:
                        sample["heapError"] = str(error)
                    next_heap = elapsed + 3600
                samples.write(json.dumps(sample) + "\n")
                peak = max(peak, info.phys_footprint)
                report.update(samples=report["samples"] + 1, elapsedSeconds=elapsed, latestFootprintBytes=info.phys_footprint, peakFootprintBytes=peak)
                if elapsed >= args.seconds:
                    report["status"] = "complete"
                temporary = directory / "summary.tmp"
                temporary.write_text(json.dumps(report, indent=2))
                temporary.replace(directory / "summary.json")
                if elapsed >= args.seconds:
                    break
                time.sleep(min(args.interval, max(0, args.seconds - (time.monotonic() - started))))
    except (ProcessLookupError, OSError) as error:
        report.update(status="interrupted", error=str(error))
        (directory / "summary.json").write_text(json.dumps(report, indent=2))
        raise
