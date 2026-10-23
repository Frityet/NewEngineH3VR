#!/usr/bin/env python3
import json, pathlib, sys
from PIL import Image

base = pathlib.Path(sys.argv[1])
record = {}
for phase in ["grid", "black", "restored"]:
    path = pathlib.Path(str(base) + "." + phase + ".png")
    im = Image.open(path).convert("RGB")
    assert im.size == (2880, 1600)
    colors = set(im.get_flattened_data())
    if phase == "black":
        assert colors == {(0, 0, 0)}, colors
    elif phase == "restored":
        assert colors == {(0, 255, 0)}, len(colors)
    else:
        assert len(colors) > 10, len(colors)
    record[phase] = {
        "capture": str(path),
        "pixels": im.width * im.height,
        "unique_colors": len(colors),
    }
pathlib.Path(str(base) + ".fade-verification.json").write_text(json.dumps(record, indent=2) + "\n")
print(json.dumps(record, indent=2))
