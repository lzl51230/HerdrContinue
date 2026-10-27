#!/usr/bin/env python3
"""Validate native macOS Login Items (SMAppService)."""
from pathlib import Path
import runpy
runpy.run_path(str(Path(__file__).with_name("qa-native-startup-macos.py")), run_name="__main__")
