#!/usr/bin/env python3
"""Stage verified ARM64 native integrations into the generated Unity project."""

from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess
import stat
from datetime import datetime, timezone
import uuid
from native_plugin_contract import HEADERS, check_bridge
from legacy_runtime_staging import retire_legacy_runtime

root = Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser()
p.add_argument("--project", type=Path, default=root / "unity")
p.add_argument("--steam", action="store_true")
p.add_argument("--xr", action="store_true")
p.add_argument(
    "--all", action="store_true", help="Stage composed managed and all native platform integrations"
)
args = p.parse_args()
project = args.project.resolve()
if (
    project == root / "recovered/ExportedProject"
    or project == root / "recovered-full/ExportedProject"
):
    raise SystemExit("Refusing to modify pristine recovery")
plugins = project / "Assets/Plugins/Native/macOS"
plugins.mkdir(parents=True, exist_ok=True)
staged = []


def copy_generated(src, dest):
    dest = Path(dest)
    if dest.exists():
        dest.chmod(dest.stat().st_mode | stat.S_IWUSR)
    return shutil.copy2(src, dest)


def native(src):
    if src.name in HEADERS:
        check_bridge(src)
    arch = subprocess.check_output(["lipo", "-archs", str(src)], text=True).strip()
    if "arm64" not in arch.split():
        raise SystemExit(f"{src}: missing ARM64 slice, found {arch}")
    dest = plugins / src.name
    if arch == "arm64":
        copy_generated(src, dest)
    else:
        subprocess.run(["lipo", str(src), "-thin", "arm64", "-output", str(dest)], check=True)
        subprocess.run(["codesign", "--force", "--sign", "-", str(dest)], check=True)
    guid = hashlib.sha256(("NativeH3VR.Native:" + src.name).encode()).hexdigest()[:32]
    dest.with_suffix(dest.suffix + ".meta").write_text(
        """fileFormatVersion: 2
guid: %s
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        CPU: AnyCPU
        DefaultValueInitialized: true
        OS: AnyOS
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: ARM64
  userData: NativeH3VR native ARM64 platform integration
  assetBundleName:
  assetBundleVariant:
"""
        % guid
    )
    staged.append(
        {
            "source": str(src),
            "target": str(dest),
            "architecture": "arm64",
            "sourceArchitectures": arch,
            "sha256": hashlib.sha256(dest.read_bytes()).hexdigest(),
        }
    )


if args.steam or args.all:
    for name in ["libCSteamworks.dylib", "libsteam_api.dylib"]:
        native(root / "build/steamworks" / name)
    # Preserve the MonoScript GUID already used by the recovered scene data.
    src = root / "build/steamworks/managed/NativeH3VR.FirstPass.dll"
    dest = project / "Assets/Plugins/NativeH3VR.FirstPass.dll"
    if not dest.with_suffix(".dll.meta").exists():
        raise SystemExit("Run managed project preparation before Steam staging")
    copy_generated(src, dest)
    (project / "steam_appid.txt").write_text("450540\n")

if args.xr or args.all:
    for name in ["libNativeH3VRTracking.dylib", "libNativeH3VRXR.dylib"]:
        native(root / "native/build" / name)
    managed = project / "Assets/NativePort"
    managed.mkdir(parents=True, exist_ok=True)
    if not args.all:
        shutil.copy2(root / "native/managed/NativeH3VRXR.cs", managed / "NativeH3VRXR.cs")

if args.all:
    # Bind the opt-in acceptance harness to the exact composed gameplay API.
    subprocess.run(
        [
            "python3",
            str(root / "platform/gameplay-probe/build.py"),
            "--managed",
            str(root / "build/composed/final"),
        ],
        check=True,
    )
    diagnostics = project / "Assets/NativePort/NativePortDiagnostics.cs"
    diagnostics.parent.mkdir(parents=True, exist_ok=True)
    copy_generated(root / "script/NativePortDiagnostics.cs", diagnostics)
    for name in ["libNativeH3VRFMOD.dylib", "libfmod.dylib", "libfmodstudio.dylib"]:
        source = root / "build/fmod" / name
        native(source)
    # The composed XR DLL replaces our earlier source-only bridge, avoiding duplicate types.
    for suffix in ["", ".meta"]:
        old = project / ("Assets/NativePort/NativeH3VRXR.cs" + suffix)
        if old.exists():
            old.unlink()
    managed_sources = [
        *sorted((root / "build/composed/final").glob("*.dll")),
        root / "platform/gameplay-probe/build/NativeH3VR.GameplayProbe.dll",
    ]
    for src in managed_sources:
        dest = project / "Assets/Plugins" / src.name
        copy_generated(src, dest)
        meta = dest.with_suffix(".dll.meta")
        if not meta.exists():
            guid = hashlib.sha256(("NativeH3VR.Managed:" + src.name).encode()).hexdigest()[:32]
            meta.write_text(
                "fileFormatVersion: 2\nguid: " + guid + "\nPluginImporter:\n"
                "  externalObjects: {}\n  serializedVersion: 2\n  iconMap: {}\n  executionOrder: {}\n"
                "  defineConstraints: []\n  isPreloaded: 0\n  isOverridable: 0\n"
                "  isExplicitlyReferenced: 0\n  validateReferences: 1\n  platformData:\n"
                "  - first:\n      Any:\n    second:\n      enabled: 1\n      settings: {}\n"
                "  userData: NativeH3VR composed platform integration\n  assetBundleName:\n  assetBundleVariant:\n"
            )
    streaming = project / "Assets/StreamingAssets"
    retirement = (
        root
        / "build/runtime-staging"
        / (
            "retired-"
            + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
            + "-"
            + uuid.uuid4().hex[:8]
        )
    )
    retire_legacy_runtime(streaming, retirement)
    runtime = streaming / "NewEngine/build"
    shutil.copytree(
        root / "native/build/runtime/build",
        runtime,
        dirs_exist_ok=True,
        copy_function=copy_generated,
    )
    source_data = Path.home() / "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
    # Original banks, video and action definitions are platform-independent data.
    # Asset bundles must be rebuilt separately for macOS before staging them.
    original_streaming = source_data / "StreamingAssets"
    for src in original_streaming.rglob("*"):
        if src.is_file() and src.suffix.lower() in {
            ".bank",
            ".json",
            ".mp4",
            ".webm",
            ".wav",
            ".txt",
        }:
            dest = streaming / src.relative_to(original_streaming)
            dest.parent.mkdir(parents=True, exist_ok=True)
            copy_generated(src, dest)
    actions = streaming / "SteamVR"
    actions.mkdir(parents=True, exist_ok=True)
    for src in source_data.parent.glob("*.json"):
        copy_generated(src, actions / src.name)
    if not (actions / "actions.json").exists():
        raise SystemExit("Original SteamVR actions.json is missing")

report = root / "build/native-staging.json"
report.write_text(json.dumps({"project": str(project), "libraries": staged}, indent=2) + "\n")
print(report)
