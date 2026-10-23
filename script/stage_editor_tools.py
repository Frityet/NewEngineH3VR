#!/usr/bin/env python3
"""Keep build helpers separate from asset postprocessors to avoid broad reimports."""

from pathlib import Path
import argparse
import json
import shutil

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--project", type=Path, required=True)
args = parser.parse_args()
project = args.project.resolve()
if project not in (root / "unity", root / "unity-full"):
    raise SystemExit("Only the generated working projects may be staged")
editor = project / "Assets/Editor"
target = editor / "NativeBuildTools"
target.mkdir(parents=True, exist_ok=True)
sources = [
    root / "script" / name
    for name in (
        "NativePortBuild.cs",
        "NativeBundleBuild.cs",
        "NativeAssetSerialization.cs",
        "NativeAssetIntegrity.cs",
    )
]
sources.append(root / "shaders/ForceComputeImports.cs")
sources.append(root / "shaders/NativeOriginalVariantFilter.cs")
for source in sources:
    dest = target / source.name
    if not dest.exists() or dest.read_bytes() != source.read_bytes():
        shutil.copy2(source, dest)
    old = editor / source.name
    if old.exists():
        old.unlink()
    old_meta = old.with_suffix(old.suffix + ".meta")
    new_meta = dest.with_suffix(dest.suffix + ".meta")
    if old_meta.exists():
        if not new_meta.exists():
            old_meta.rename(new_meta)
        else:
            old_meta.unlink()
definition = {
    "name": "NativeH3VR.BuildTools.Editor",
    "references": [],
    "includePlatforms": ["Editor"],
    "excludePlatforms": [],
    "allowUnsafeCode": False,
    "autoReferenced": False,
    "overrideReferences": True,
    "precompiledReferences": [],
}
asmdef = target / "NativeH3VR.BuildTools.Editor.asmdef"
text = json.dumps(definition, indent=2) + "\n"
if not asmdef.exists() or asmdef.read_text() != text:
    asmdef.write_text(text)
# This postprocessor uses only Unity's built-in APIs. Giving it implicit
# references to every recovered game DLL invalidates all imported assets after
# each gameplay patch, despite its own behavior remaining unchanged.
patches = editor / "AssetRipperPatches"
if (patches / "AudioMixerPostprocessor.cs").is_file():
    import_definition = dict(definition, name="NativeH3VR.AssetImport.Editor")
    import_asmdef = patches / "NativeH3VR.AssetImport.Editor.asmdef"
    import_text = json.dumps(import_definition, indent=2) + "\n"
    if not import_asmdef.exists() or import_asmdef.read_text() != import_text:
        import_asmdef.write_text(import_text)
print(target)
