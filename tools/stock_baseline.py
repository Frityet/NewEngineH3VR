#!/usr/bin/env python3
"""Summarize an existing Steam manifest and Unity Player log without copying them."""

import argparse
import json
from pathlib import Path
import re


H3VR_APP_ID = "450540"
ENGINE_VERSION = re.compile(
    r"^Initialize engine version: ([0-9]{1,4}\.[0-9]{1,3}\.[0-9]{1,3}[abfp][0-9]{1,4})(?:\s|$)"
)
GRAPHICS_APIS = {
    "Direct3D 11": re.compile(r"^\s*Version:\s+Direct3D 11(?:\.|\s|$)"),
    "Direct3D 12": re.compile(r"^\s*Version:\s+Direct3D 12(?:\.|\s|$)"),
    "Metal": re.compile(r"^Initializing Metal device caps:"),
    "OpenGL": re.compile(r"^\s*Version:\s+OpenGL(?:\s|$)"),
    "Vulkan": re.compile(r"^\s*Version:\s+Vulkan(?:\s|$)"),
}


def read_appmanifest(path):
    with path.open(encoding="utf-8-sig") as stream:
        content = stream.read()

    def number_field(name):
        values = re.findall(rf'^\s*"{name}"\s+"([0-9]{{1,20}})"\s*$', content, re.MULTILINE)
        if len(values) != 1:
            raise ValueError(f"expected one numeric {name} in the Steam appmanifest")
        return values[0]

    app_id = number_field("appid")
    if app_id != H3VR_APP_ID:
        raise ValueError("Steam appmanifest is for a different app")
    return {"app_id": app_id, "build_id": number_field("buildid")}


def read_player_log(path):
    engine_versions = set()
    graphics_apis = set()
    graphics_device_initialization_reported = False
    with path.open(encoding="utf-8-sig", errors="replace") as stream:
        for line in stream:
            version = ENGINE_VERSION.match(line)
            if version:
                engine_versions.add(version.group(1))
            if line.startswith("GfxDevice: creating device client"):
                graphics_device_initialization_reported = True
            for api, pattern in GRAPHICS_APIS.items():
                if pattern.match(line):
                    graphics_apis.add(api)

    return {
        "engine_versions_reported": sorted(engine_versions),
        "graphics_apis_reported": sorted(graphics_apis),
        "graphics_device_initialization_reported": graphics_device_initialization_reported,
    }


def main():
    parser = argparse.ArgumentParser(
        description="Read existing H3VR Steam build metadata and Unity startup markers. "
        "These markers do not establish launch time or rendered frames.",
    )
    parser.add_argument("--appmanifest", type=Path, help="Steam appmanifest_450540.acf")
    parser.add_argument("--player-log", type=Path, help="Unity Player.log or output_log.txt from the run")
    args = parser.parse_args()
    if args.appmanifest is None and args.player_log is None:
        parser.error("supply --appmanifest, --player-log, or both")

    try:
        steam_installation = read_appmanifest(args.appmanifest) if args.appmanifest else None
    except (OSError, UnicodeError):
        parser.error("could not read Steam appmanifest as UTF-8")
    except ValueError as error:
        parser.error(str(error))

    try:
        unity_player_log = read_player_log(args.player_log) if args.player_log else None
    except OSError:
        parser.error("could not read Unity Player log")

    print(json.dumps({
        "steam_installation": steam_installation,
        "unity_player_log": unity_player_log,
        "elapsed_time_ms": None,
        "rendered_frames_verified": False,
    }, indent=2))


if __name__ == "__main__":
    main()
