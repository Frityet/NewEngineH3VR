#!/usr/bin/env python3
"""Install the NewEngineH3VR launcher and sign a generated ARM64 Unity app."""

from pathlib import Path
import argparse
import json
import plistlib
import shutil
import subprocess
from audit_native_app import (
    audit,
    localize_packaged_dependencies,
    macho_commands,
    sign_packaged_binaries,
)
from native_plugin_contract import refresh_bridges

ROOT = Path(__file__).resolve().parents[1]
LAUNCHER = "NewEngineH3VR"
PLAYER = "NewEngineH3VRPlayer"
LEGACY_LAUNCHER = "NativeH3VR"
LEGACY_PLAYER = "NativeH3VRPlayer"


def unity_player(path):
    if path.is_symlink() or not path.is_file():
        raise ValueError("Missing or symlink executable: " + str(path))
    if subprocess.check_output(["lipo", "-archs", str(path)], text=True).strip() != "arm64":
        raise ValueError("Unity player is not ARM64-only: " + str(path))
    dependencies, _ = macho_commands(path)
    return any(Path(name).name == "UnityPlayer.dylib" for name in dependencies)


def prepare_player(bin_dir, executable):
    """Preserve Unity's engine before replacing its public entrypoint.

    The Unity product and packaged launcher deliberately share the public name;
    identify the engine by its UnityPlayer linkage, not that ambiguous filename.
    """
    if Path(executable).name != executable:
        raise ValueError("Bundle executable must be a simple filename")
    entry = bin_dir / executable
    player = bin_dir / PLAYER
    legacy = bin_dir / LEGACY_PLAYER
    if any(path.is_symlink() for path in (entry, player, legacy)):
        raise ValueError("Refusing symlink player/entrypoint")
    entry_is_engine = entry.exists() and unity_player(entry)
    if entry_is_engine:
        # A fresh Unity build may leave the previous separate player beside its
        # new entrypoint. The fresh linked entrypoint is the authoritative engine.
        if player.exists() and not unity_player(player):
            raise ValueError("Separate player path contains a non-Unity executable")
        if legacy.exists() and not unity_player(legacy):
            raise ValueError("Legacy player path contains a non-Unity executable")
        if entry != player:
            entry.replace(player)
    else:
        if executable not in (LAUNCHER, LEGACY_LAUNCHER):
            raise ValueError("Entrypoint is not a Unity player or recognized launcher")
        candidates = [path for path in (player, legacy) if path.exists()]
        if not candidates or any(not unity_player(path) for path in candidates):
            raise ValueError("Packaged launcher has no valid separate Unity player")
        if len(candidates) == 2 and player.read_bytes() != legacy.read_bytes():
            raise ValueError("Conflicting new and legacy Unity players; refusing to guess")
        if not player.exists():
            legacy.replace(player)
    # Retire known legacy paths only after preserving a verified Unity engine.
    legacy.unlink(missing_ok=True)
    old_launcher = bin_dir / LEGACY_LAUNCHER
    if old_launcher != player:
        old_launcher.unlink(missing_ok=True)
    return player


def package(app):
    app = app.resolve()
    if not app.is_relative_to((ROOT / "dist").resolve()):
        raise ValueError("Only this project's generated dist applications may be packaged")
    bridge_refresh = refresh_bridges(app)
    subprocess.run(["python3", str(ROOT / "platform/launch/build.py")], check=True)
    plist = app / "Contents/Info.plist"
    with plist.open("rb") as stream:
        info = plistlib.load(stream)
    bin_dir = app / "Contents/MacOS"
    prepare_player(bin_dir, info["CFBundleExecutable"])
    shutil.copy2(ROOT / "build/launch" / LAUNCHER, bin_dir / LAUNCHER)
    shutil.copy2(
        ROOT / "platform/launch/steam_appid.txt", app / "Contents/Resources/steam_appid.txt"
    )
    info["CFBundleExecutable"] = LAUNCHER
    info["CFBundleName"] = LAUNCHER
    info["CFBundleDisplayName"] = LAUNCHER
    with plist.open("wb") as stream:
        plistlib.dump(info, stream)
    changes = localize_packaged_dependencies(app)
    sign_packaged_binaries(app)
    subprocess.run(["codesign", "--force", "--deep", "--sign", "-", str(app)], check=True)
    subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
    result = audit(app)
    result["packagingDependencyChanges"] = changes
    result["packagingNativeBridgeRefresh"] = bridge_refresh
    report = ROOT / "build/native-app-audit.json"
    report.write_text(json.dumps(result, indent=2) + "\n")
    if not result["passed"]:
        raise ValueError(
            "Packaged architecture/dependency audit failed: " + json.dumps(result["errors"])
        )
    print(
        f"{app} — audited {result['nativeCount']} ARM64 binaries and {result['managedCount']} managed assemblies; {report}"
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    package(parser.parse_args().app)


if __name__ == "__main__":
    main()
