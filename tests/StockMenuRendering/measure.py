# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy", "pillow"]
# ///
"""Measure stereo post switches and calibrated swatches in one StockMenuAudit capture."""
import argparse
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image


def image(path):
    with Image.open(path) as src:
        return np.asarray(src.convert("RGB"), dtype=np.float32)


def change(reference, sample):
    delta = np.abs(reference - sample)
    return {"meanAbsoluteRgbBytes": round(float(delta.mean()), 3),
            "meanLumaShiftBytes": round(float(((reference - sample) @ [0.2126, 0.7152, 0.0722]).mean()), 3),
            "fractionChangingOver5": round(float((delta.max(axis=2) > 5).mean()), 5)}


def inspect(root):
    meta = json.loads((root / "menu.json").read_text())
    out = {}
    for i, side in enumerate(("left", "right")):
        eye = meta["eyes"][i]
        def load(stage):
            return image(root / (stage + "-" + side + ".png"))
        stock = load("stock")
        comparisons = {mode: change(stock, load(mode)) for mode in
                       ("stock-repeat", "stock-no-grade", "stock-no-bloom", "stock-toggle-ao", "stock-no-post", "stock-restored")}
        # Root is 38 cm forward and 12 cm above the head, front-facing along +Z.
        # Use the real eye's asymmetrical frustum and the baseline orientation recorded by the title test.
        l, r, up, down = (math.tan(v) for v in eye["frustumRadians"])
        eye_offset = meta["headPosition"]["x"] - eye["position"]["x"]
        def sample(pixels, x, y):
            tx = (eye_offset + x * .38 / 1.5) / .38
            ty = (.12 + y * .38 / 1.5) / .38
            px = round((tx - l) / (r - l) * eye["width"])
            py = round((up - ty) / (up - down) * eye["height"])
            area = pixels[py - 3:py + 4, px - 3:px + 4]
            return [round(float(v), 2) for v in area.mean(axis=(0, 1))]
        chart = {}
        for mode in ("chart", "chart-no-bloom", "chart-no-bloom-no-grade"):
            pixels = load(mode)
            chart[mode] = {"neutral": [sample(pixels, j * .24, 0) for j in (-2, -1, 0, 1, 2)],
                           "surroundBlack": sample(pixels, -.44, .19),
                           "brightHalo": sample(pixels, .57, 0)}
        out[side] = {"differences": comparisons, "chart": chart}
        if not (comparisons["stock-no-grade"]["meanAbsoluteRgbBytes"] > comparisons["stock-repeat"]["meanAbsoluteRgbBytes"] + 1
                and comparisons["stock-no-bloom"]["fractionChangingOver5"] > comparisons["stock-repeat"]["fractionChangingOver5"]
                and comparisons["stock-toggle-ao"]["fractionChangingOver5"] > comparisons["stock-repeat"]["fractionChangingOver5"]
                and chart["chart"]["brightHalo"][1] - chart["chart-no-bloom"]["brightHalo"][1] > 10):
            raise ValueError(side + " stock post-processing output did not respond to its controls")
    out["shaderResources"] = meta["resources"]
    out["shaderUse"] = meta["shaderUse"]
    out["reflectionProbes"] = meta["reflectionProbes"]
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    args = parser.parse_args()
    result = inspect(args.capture)
    target = args.capture / "eye-measurements.json"
    target.write_text(json.dumps(result, indent=2) + "\n")
    print(target)


if __name__ == "__main__":
    main()
