#!/usr/bin/env python3
"""Unwrap Unity 5 Crunch containers to identical DXT blocks, retaining every mip."""

from pathlib import Path
import hashlib
import json
import os
import sys
import UnityPy
import texture2ddecoder
from native_texture_binary import native_file

root = Path(__file__).resolve().parents[1]
project = root / "unity-full"
apply = "--apply" in sys.argv
rows = json.loads((root / "build/asset-identities/full-texture2d-identity-map.json").read_text())
records = []
for row in rows:
    if row["format"] not in ("DXT1Crunched", "DXT5Crunched"):
        continue
    path = (project / row["exported_path"]).with_suffix(".texture2D")
    obj = next(iter(UnityPy.load(str(path)).objects))
    tree = obj.read_typetree()
    format = tree["m_TextureFormat"]
    if format in (10, 12):
        continue
    assert format in (28, 29), (path, format)
    raw = obj.read().get_image_data()
    levels = []
    for mip in range(tree["m_MipCount"]):
        # The decoder's optional second argument selects the original level;
        # its one-argument API exposes only level zero.
        blocks = texture2ddecoder.unpack_crunch(raw, mip)
        width = max(1, tree["m_Width"] >> mip)
        height = max(1, tree["m_Height"] >> mip)
        expected = (
            max(1, (width + 3) // 4) * max(1, (height + 3) // 4) * (8 if format == 28 else 16)
        )
        assert len(blocks) == expected, (path, mip, expected, len(blocks))
        levels.append(blocks)
    assert levels[0] == texture2ddecoder.unpack_crunch(raw)
    payload = b"".join(levels)
    tree["m_TextureFormat"] = 10 if format == 28 else 12
    tree["m_CompleteImageSize"] = len(payload)
    binary = native_file(tree, payload, 28)
    check = next(iter(UnityPy.load(binary).objects))
    assert check.read().get_image_data() == payload
    assert check.read_typetree()["m_MipCount"] == len(levels)
    record = {
        "path": str(path.relative_to(project)),
        "guid": row["guid"],
        "originalFormat": format,
        "nativeFormat": tree["m_TextureFormat"],
        "mips": len(levels),
        "originalBytes": len(raw),
        "dxtBytes": len(payload),
        "originalSHA256": hashlib.sha256(raw).hexdigest(),
        "dxtSHA256": hashlib.sha256(payload).hexdigest(),
        "mipSHA256": [hashlib.sha256(level).hexdigest() for level in levels],
    }
    records.append(record)
    if apply:
        temporary = path.with_suffix(".native-tmp")
        temporary.write_bytes(binary)
        os.replace(temporary, path)
        if not (root / "build/textures/crunch-probe-source.json").exists():
            probe = root / "native-texture-probe/Assets/Resources"
            (probe / "OriginalLegacyCrunch.asset").write_bytes(binary)
            (probe / "OriginalLegacyCrunchRaw.bytes").write_bytes(payload)
            (root / "build/textures/crunch-probe-source.json").write_text(
                json.dumps(record, indent=2) + "\n"
            )
out = root / (
    "build/textures/legacy-crunch-applied.json"
    if apply
    else "build/textures/legacy-crunch-plan.json"
)
if records or not out.exists():
    out.write_text(
        json.dumps(
            {
                "applied": apply,
                "textures": records,
                "method": "Lossless Crunch container unpack to original DXT blocks; all original mip levels retained",
            },
            indent=2,
        )
        + "\n"
    )
print(
    f"{'Converted' if apply else 'Validated'} {len(records)} legacy Crunch textures; no pixel recompression."
)
