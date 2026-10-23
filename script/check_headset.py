#!/usr/bin/env python3
"""Read the user's native runtime status without starting an XR session."""

import json
from pathlib import Path
import subprocess


def main():
    runtime = Path.home() / "Projects/ValveIndexMacOS/build/bin/indexctl"
    if not runtime.is_file():
        print("Native runtime CLI is missing: " + str(runtime))
        return 1
    try:
        status = json.loads(
            subprocess.check_output([str(runtime), "status"], text=True, timeout=10)
        )
        room = json.loads(subprocess.check_output([str(runtime), "room"], text=True, timeout=10))
    except (subprocess.SubprocessError, OSError, ValueError) as error:
        print("Open Virtual Reality and start hardware tracking. Status read failed: " + str(error))
        return 1
    if not status.get("running") or status.get("simulation") or status.get("source") != "hardware":
        print("Open Virtual Reality and start hardware tracking before launching the demo.")
        return 1
    if not room.get("floor_known"):
        print("Set Virtual Reality → Setup → Floor height before launching the demo.")
        print("The runtime requires a measured floor for the standing space used by H3VR.")
        return 1
    devices = status.get("devices", [])

    def tracked(device):
        age = device.get("pose_age_ms")
        return (
            device.get("connected")
            and device.get("has_pose")
            and device.get("pose_current")
            and isinstance(age, (int, float))
            and 0 <= age <= 250
        )

    if not any(device.get("type") == 1 and tracked(device) for device in devices):
        print(
            "The headset has no fresh tracked pose. Put it in view of the base stations and retry."
        )
        return 1
    controllers = sum(device.get("type") == 2 and bool(tracked(device)) for device in devices)
    print(
        "Live headset tracking and floor are ready. Tracked controllers: "
        + str(controllers)
        + "/2."
    )
    if controllers < 2:
        print("Wake both controllers in view of the base stations to grab and operate items.")
    print(
        "Open Virtual Reality Home for headset output. Physical image quality still needs a headset check."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
