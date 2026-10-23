"""Recreate the saved corrected-emitter Metal probe for local Unity 2021 ARM64.

This copies only the minimal color-path-probe project and restores the four exact
shader wrappers from native-proof. It verifies each wrapper embeds the source body
whose hash is recorded in native-proof/emitter-native-probe-sources.json. It never
reads or modifies unity-full.
"""

from pathlib import Path
import argparse
import hashlib
import json
import re
import shutil

ROOT = Path(__file__).resolve().parents[2]
SKELETON = ROOT / "color-path-probe"
PROOF = ROOT / "shaders/color-path/native-proof"
PACKAGE = ROOT / "shaders/color-path/semantic-v3-packaged"
SOURCE_MAP = PROOF / "emitter-native-probe-sources.json"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument(
    "--project",
    type=Path,
    default=Path("color-path-probe-unity2021"),
    help="new scratch Unity project path",
)
parser.add_argument(
    "--evidence",
    type=Path,
    default=Path("shaders/color-path/native-proof-2021"),
    help="new evidence directory",
)
args = parser.parse_args()

PROJECT = args.project if args.project.is_absolute() else ROOT / args.project
EVIDENCE = args.evidence if args.evidence.is_absolute() else ROOT / args.evidence

sha = lambda data: hashlib.sha256(data).hexdigest()
assert not PROJECT.exists(), f"Refusing to overwrite existing scratch project: {PROJECT}"
assert not EVIDENCE.exists(), f"Refusing to overwrite existing evidence: {EVIDENCE}"

mapping = json.loads(SOURCE_MAP.read_text())
assert len(mapping) == 4 and [r["index"] for r in mapping] == list(range(4))
assert "2021.3.45f2" in (SKELETON / "ProjectSettings/ProjectVersion.txt").read_text()
shutil.copytree(
    SKELETON, PROJECT, ignore=shutil.ignore_patterns("Library", "Temp", "obj", "Logs", "output")
)
(PROJECT / "output").mkdir(parents=True)
rows = []

for item in mapping:
    index = item["index"]
    include = PACKAGE / item["path"]
    include_bytes = include.read_bytes()
    assert sha(include_bytes) == item["source_sha256"], (
        include,
        "emitted include hash differs from frozen proof",
    )
    wrapper_src = PROOF / f"Corrected{index}.shader"
    wrapper_dst = PROJECT / f"Assets/Resources/Corrected{index}.shader"
    wrapper_bytes = wrapper_src.read_bytes()
    body = include_bytes.decode()
    body = re.sub(r"\blog\(", "log2(", re.sub(r"\bexp\(", "exp2(", body))
    assert body in wrapper_bytes.decode(), (
        wrapper_src,
        "wrapper does not embed mapped emitted body",
    )
    shutil.copy2(wrapper_src, wrapper_dst)
    rows.append(
        {
            **item,
            "candidate_include_sha256": sha(include_bytes),
            "corrected_wrapper_path": wrapper_dst.relative_to(PROJECT).as_posix(),
            "corrected_wrapper_sha256": sha(wrapper_bytes),
        }
    )

# Negative controls remain byte-for-byte from the original minimal probe project.
negative = []
for kind in ("Broken", "SwizzleOnly"):
    for branch in range(4):
        rel = Path(f"Assets/Resources/{kind}{branch}.shader")
        source = SKELETON / rel
        copied = PROJECT / rel
        assert sha(source.read_bytes()) == sha(copied.read_bytes()), (
            rel,
            "negative control changed during copy",
        )
        negative.append({"path": rel.as_posix(), "sha256": sha(copied.read_bytes())})

for name, rel in (
    ("BuildColorProbe.cs", "Assets/Editor/BuildColorProbe.cs"),
    ("ColorProbe.cs", "Assets/ColorProbe.cs"),
):
    src = PROOF / name
    dst = PROJECT / rel
    shutil.copy2(src, dst)

manifest = {
    "project": PROJECT.relative_to(ROOT).as_posix(),
    "project_version": (PROJECT / "ProjectSettings/ProjectVersion.txt").read_text().splitlines()[0],
    "skeleton": "color-path-probe",
    "positive_source_base": "shaders/color-path/semantic-v3-packaged",
    "positive_sources": rows,
    "negative_controls_unchanged": negative,
    "test_harness": {
        "build_source": "shaders/color-path/native-proof/BuildColorProbe.cs",
        "build_sha256": sha((PROOF / "BuildColorProbe.cs").read_bytes()),
        "runtime_source": "shaders/color-path/native-proof/ColorProbe.cs",
        "runtime_sha256": sha((PROOF / "ColorProbe.cs").read_bytes()),
    },
    "harness_intrinsic_mapping": "DXBC LOG/EXP to base-two log2/exp2; copied source wrappers and ColorProbe implement the same mapping and runtime test as the saved native-proof.",
    "untouched": ["unity-full", "bundles", "player"],
}
EVIDENCE.mkdir(parents=True)
(EVIDENCE / "source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(
    f"Prepared {PROJECT} from saved native-proof wrappers; source manifest: {EVIDENCE.relative_to(ROOT)}/source-manifest.json"
)
