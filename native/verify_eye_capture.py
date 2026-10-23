#!/usr/bin/env python3
"""Check actual runtime composite against Unity's captured eye textures."""

import argparse, json, pathlib, math
from PIL import Image

p = argparse.ArgumentParser()
p.add_argument("capture", type=pathlib.Path)
p.add_argument("--linear-source", action="store_true")
args = p.parse_args()
im = Image.open(args.capture).convert("RGBA")
assert im.size == (256, 128), im.size
checks = []
max_error = 0
for eye in range(2):
    src = Image.open(str(args.capture) + ".source-" + str(eye) + ".png").convert("RGBA")
    assert src.size == (128, 128)

    def encode(v):
        v = v / 255
        return round(255 * (12.92 * v if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055))

    for y in range(128):
        for x in range(128):
            expected = src.getpixel((x, y))
            if args.linear_source:
                expected = tuple(encode(v) for v in expected[:3]) + (expected[3],)
            actual = im.getpixel((eye * 128 + x, y))
            error = max(abs(a - b) for a, b in zip(actual, expected))
            max_error = max(max_error, error)
    corners = [
        im.getpixel((eye * 128 + x, y))[:3] for x, y in [(16, 16), (112, 16), (16, 112), (112, 112)]
    ]
    assert corners[0] != corners[2] and corners[1] != corners[3], corners
    gray = [im.getpixel((eye * 128 + 16 * i + 8, 64))[0] for i in range(8)]
    expected_gray = [round(255 * (1.055 * ((i + 0.5) / 8) ** (1 / 2.4) - 0.055)) for i in range(8)]
    assert all(abs(a - b) <= 1 for a, b in zip(gray, expected_gray)), (gray, expected_gray)
    checks.append(
        {
            "eye": eye,
            "corners_top_left_top_right_bottom_left_bottom_right": corners,
            "srgb_gray_ramp": gray,
        }
    )
assert max_error <= 1, max_error
record = {
    "actual_compositor": str(args.capture),
    "source_format": "linear UNORM" if args.linear_source else "sRGB",
    "pixels_compared": 32768,
    "max_channel_error": max_error,
    "checks": checks,
}
pathlib.Path(str(args.capture) + ".verification.json").write_text(
    json.dumps(record, indent=2) + "\n"
)
print(json.dumps(record, indent=2))
