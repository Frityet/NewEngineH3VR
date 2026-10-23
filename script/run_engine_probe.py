#!/usr/bin/env python3
"""Bounded native engine/Metal validation. Does not claim H3VR compatibility."""

import json
import os
from pathlib import Path
import subprocess
import time

root = Path(__file__).resolve().parents[1]
app = root / "dist/NewEngineH3VR Engine Probe.app"
exe = app / "Contents/MacOS/NewEngineH3VR Engine Probe"
result = root / "build/engine-probe-result.json"
log = root / "build/engine-probe-player.log"
architecture = subprocess.check_output(["/usr/bin/lipo", "-archs", str(exe)], text=True).strip()
if architecture != "arm64":
    raise SystemExit(f"Expected ARM64-only executable; found {architecture}")
if result.exists():
    result.unlink()
# LaunchServices forwards this environment to the new app process.
env = dict(os.environ, NATIVEH3VR_PROBE_RESULT=str(result))
process = subprocess.Popen(
    ["/usr/bin/open", "-n", "-W", str(app), "--args", "-force-metal", "-logFile", str(log)], env=env
)
try:
    process.wait(timeout=45)
except subprocess.TimeoutExpired:
    # Only this project-specific probe executable may be stopped.
    subprocess.run(["/usr/bin/pkill", "-f", "^" + str(exe) + "(?: |$)"], check=False)
    process.terminate()
    raise SystemExit(f"Probe timed out; inspect {log}")
if not result.exists():
    raise SystemExit(f"Probe did not produce a result; inspect {log}")
data = json.loads(result.read_text())
data["architecture"] = architecture
data["wine_required"] = False
result.write_text(json.dumps(data, indent=2) + "\n")
print(json.dumps(data, indent=2))
if not data["metal"] or data["frames"] < 120:
    raise SystemExit(1)
