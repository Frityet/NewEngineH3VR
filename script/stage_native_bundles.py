#!/usr/bin/env python3
"""Stage only content-verified macOS bundles, moving data to avoid a second 11GB copy."""

from pathlib import Path
import json
import shutil
from verify_native_bundles import (
    verify,
    inside,
    LEGACY_MARKER,
    CONTENT_MARKER,
    COMPILER_RECEIPT,
    COMPILER_LOG,
)


def stage(source, target, plan, project, report):
    proof = verify(source, plan, project)  # No mutations before all hashes pass.
    target.mkdir(parents=True, exist_ok=True)
    # A failed partial move cannot leave the previous build's acceptance marker.
    for name in (LEGACY_MARKER, CONTENT_MARKER, COMPILER_RECEIPT, COMPILER_LOG):
        (target / name).unlink(missing_ok=True)
    records = []
    for item in proof["outputs"]:
        destination = inside(target, item["path"])
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(inside(source, item["path"])), str(destination))
        records.append({"name": item["path"], "bytes": item["bytes"], "sha256": item["sha256"]})
    for name in (COMPILER_LOG, COMPILER_RECEIPT):
        shutil.copy2(source / name, target / name)
    shutil.copy2(source / CONTENT_MARKER, target / CONTENT_MARKER)
    shutil.copy2(source / LEGACY_MARKER, target / LEGACY_MARKER)
    report.write_text(json.dumps(records, indent=2) + "\n")
    print(f"Staged {proof['bundles']} content-verified macOS bundles with original addresses.")


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[1]
    stage(
        root / "build/StreamingAssets",
        root / "unity-full/Assets/StreamingAssets",
        root / "build/native-bundle-plan.json",
        root / "unity-full",
        root / "build/native-bundles-staged.json",
    )
