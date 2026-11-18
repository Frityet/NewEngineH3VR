#!/usr/bin/env python3
"""Build the authored Metal client and prepare a local Unity integration manifest."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

SOURCE = Path(__file__).resolve().parent
UNITY_VERSION = "2021.3.45f2"
LIBRARY = "libNativeH3VRMetal.dylib"
BRIDGE = "NativeH3VR.UnityMetal"

PLUGIN_META = """fileFormatVersion: 2
guid: c7d1ecdb3ff056ae8da0bb1f10b279fa
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 1
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
      settings: {}
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: ARM64
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def tool(*args):
    subprocess.run(args, check=True)


def hash_file(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def build_plugin(editor, headers, output):
    unity = editor.parents[1]
    if not (unity / "PluginAPI/IUnityGraphicsMetal.h").is_file():
        raise ValueError("Unity editor is missing its Metal plug-in API headers")
    if not (headers / "openxr/openxr_platform.h").is_file():
        raise ValueError("Expected a Khronos OpenXR SDK include directory with Metal support")
    tool(
        "xcrun", "clang++", "-std=c++17", "-O2", "-Wall", "-Wextra", "-Werror",
        "-fobjc-arc", "-arch", "arm64", "-mmacosx-version-min=13.0", "-dynamiclib",
        f"-Wl,-install_name,@rpath/{LIBRARY}", "-I", str(unity / "PluginAPI"),
        "-I", str(headers), "-framework", "Foundation", "-framework", "Metal",
        str(SOURCE / "MetalOpenXR.mm"), "-o", str(output),
    )
    tool("codesign", "--force", "--sign", "-", str(output))
    architectures = subprocess.check_output(["lipo", "-archs", output], text=True).split()
    if architectures != ["arm64"]:
        raise ValueError(f"Expected an arm64-only Metal plug-in, found {architectures}")


def stage_platform(platform, candidate):
    fragment = json.loads((platform / "platform.json").read_text())
    entries = []
    for entry in fragment["Files"]:
        name = entry["Source"]
        if Path(name).name != name or not name.endswith((".dylib", ".dylib.meta")):
            raise ValueError("Invalid platform plug-in filename")
        source = platform / name
        if source.is_symlink() or hash_file(source) != entry["Sha256"]:
            raise ValueError("Platform fragment hash mismatch for " + name)
        destination = "Assets/Plugins/Native/macOS/" + name
        if entry["Destination"] != destination:
            raise ValueError("Invalid platform plug-in destination for " + name)
        relative = "platform/" + name
        copied = candidate / relative
        copied.parent.mkdir(exist_ok=True)
        if copied.exists():
            raise ValueError("Duplicate platform plug-in " + name)
        shutil.copyfile(source, copied)
        entries.append({"Source": relative, "Destination": destination, "Sha256": hash_file(copied)})
    expected = {"libsteam_api.dylib", "libCSteamworks.dylib", "libfmod.dylib",
                "libfmodstudio.dylib", "libNativeH3VRFMOD.dylib"}
    if {Path(e["Source"]).name for e in entries} != expected | {name + ".meta" for name in expected}:
        raise ValueError("Platform manifest does not contain the full Steam and FMOD plug-in set")
    return entries


def create_manifest(editor, headers, platform, output):
    output = output.resolve()
    if output.exists():
        raise FileExistsError(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".unity-metal-", dir=output.parent) as staging:
        candidate = Path(staging) / "integration"
        candidate.mkdir()
        build_plugin(editor, headers, candidate / LIBRARY)
        metadata = LIBRARY + ".meta"
        (candidate / metadata).write_text(PLUGIN_META)
        files = []
        for name in (LIBRARY, metadata):
            files.append((name, "Assets/Plugins/macOS/" + name))
        for name in ("MetalOpenXR.cs", "NativeStereoRenderer.cs", "H3VRMetalBinding.cs", "SyntheticHands.cs"):
            shutil.copyfile(SOURCE / name, candidate / name)
            files.append((name, "Assets/OpenXRProvider/" + name))
        manifest = {
            "SchemaVersion": 1,
            "UnityVersion": UNITY_VERSION,
            "BuildTarget": "StandaloneOSX",
            "XrBackend": "OpenXR",
            "Provider": BRIDGE,
            "Files": [
                {"Source": name, "Destination": destination, "Sha256": hash_file(candidate / name)}
                for name, destination in files
            ] + stage_platform(platform, candidate),
        }
        (candidate / "integration.json").write_text(json.dumps(manifest, indent=2) + "\n")
        candidate.rename(output)
    return output / "integration.json"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity-editor", type=Path, required=True)
    parser.add_argument("--openxr-headers", type=Path, required=True)
    parser.add_argument("--platform", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    print(create_manifest(args.unity_editor.resolve(), args.openxr_headers.resolve(),
                          args.platform.resolve(), args.output))


if __name__ == "__main__":
    main()
