# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy", "pillow"]
# ///
"""Measure paired eye captures, with repeat and optional Built-in controls."""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image

EYES = ("left", "right")


def pixels(root, scene, label, eye):
    with Image.open(root / f"{scene}-{label}-{eye}.png") as image:
        return np.asarray(image.convert("RGB"), dtype=np.float32)


def pose(root, scene, label):
    return np.array([float(part) for part in (root / f"{scene}-{label}-pose.txt").read_text().split()])


def health(image):
    red, green, blue = image.transpose(2, 0, 1)
    # Tone mapping can reduce the intensity of the shader error color.
    magenta = (red >= 130) & (blue >= 130) & (np.abs(red - blue) <= 0.22 * np.maximum(red, blue))
    return {"width": image.shape[1], "height": image.shape[0],
            "blackFraction": float((image.max(axis=2) <= 4).mean()),
            "highlightSaturationFraction": float((image.max(axis=2) >= 254).mean()),
            "errorPinkFraction": float(((red >= 248) & (blue >= 248) & (green <= 8)).mean()),
            "strongMagentaFraction": float((magenta & (green <= 0.32 * np.minimum(red, blue))).mean())}


def compare(reference, sample):
    delta = np.abs(reference - sample)
    return {"meanAbsoluteRgbBytes": round(float(delta.mean()), 4),
            "fractionChangingOver5": round(float((delta.max(axis=2) > 5).mean()), 5)}


def inspect(root, baseline):
    failures = []
    report = {}
    for stock_pose in sorted(root.glob("*-stock-pose.txt")):
        scene = stock_pose.name.removesuffix("-stock-pose.txt")
        report[scene] = {"captures": {}, "differences": {}}
        if not np.allclose(pose(root, scene, "stock"), pose(root, scene, "stock-repeat"), atol=1e-4, rtol=0):
            failures.append(f"{scene} stock repeat changed the world-space headset pose")
        for eye in EYES:
            reference = pixels(root, scene, "stock", eye)
            repeat = pixels(root, scene, "stock-repeat", eye)
            captures = report[scene]["captures"][eye] = {"stock": health(reference), "stock-repeat": health(repeat)}
            report[scene]["differences"][eye] = {"repeat": compare(reference, repeat)}
            for label, value in captures.items():
                validate_health(value, f"{scene} {label}", eye, failures)

    for quality_pose in sorted(root.glob("*-quality-pose.txt")):
        scene = quality_pose.name.removesuffix("-quality-pose.txt")
        data = report[scene] = {"captures": {}, "differences": {}}
        labels = ("performance", "balanced", "balanced-repeat", "quality", "quality-effects-off")
        if (root / f"{scene}-quality-zero-exposure-pose.txt").is_file():
            labels += ("quality-zero-exposure",)
        reference_pose = pose(root, scene, "balanced")
        for label in labels:
            if not np.allclose(pose(root, scene, label), reference_pose, atol=1e-4, rtol=0):
                failures.append(f"{scene} {label} changed the world-space headset pose")
        for eye in EYES:
            images = {label: pixels(root, scene, label, eye) for label in labels}
            data["captures"][eye] = {label: health(value) for label, value in images.items()}
            for label, value in data["captures"][eye].items():
                validate_health(value, f"{scene} {label}", eye, failures)
            differences = data["differences"][eye] = {
                "balancedRepeat": compare(images["balanced"], images["balanced-repeat"]),
                "qualityVsPerformance": compare(images["quality"], images["performance"]),
                "qualityVsEffectsOff": compare(images["quality"], images["quality-effects-off"]),
            }
            noise = differences["balancedRepeat"]["meanAbsoluteRgbBytes"]
            if "quality-zero-exposure" in images:
                differences["qualityVsZeroExposure"] = compare(images["quality"], images["quality-zero-exposure"])
            for comparison in (key for key in differences if key != "balancedRepeat"):
                if differences[comparison]["meanAbsoluteRgbBytes"] <= noise + 0.15:
                    failures.append(f"{scene} {eye} {comparison} was indistinguishable from the repeat noise")
            if baseline and (baseline / f"{scene}-stock-{eye}.png").is_file():
                stock = pixels(baseline, scene, "stock", eye)
                if np.allclose(pose(baseline, scene, "stock"), reference_pose, atol=1e-4, rtol=0) and stock.shape == images["quality"].shape:
                    differences["qualityVsStock"] = compare(images["quality"], stock)
                else:
                    differences["qualityVsStock"] = {"reason": "Built-in pose or resolution differs"}
    if not report:
        failures.append("No completed eye captures were found")
    return {"scenes": report, "failures": failures}


def validate_health(metrics, scene, eye, failures):
    if metrics["blackFraction"] > 0.95:
        failures.append(f"{scene} {eye} is nearly all black")
    if metrics["errorPinkFraction"] > 0.002 or metrics["strongMagentaFraction"] > 0.015:
        failures.append(f"{scene} {eye} contains a large error-pink region")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--builtin", type=Path, help="Include pixel comparisons with a matching Built-in capture")
    args = parser.parse_args()
    result = inspect(args.capture, args.builtin)
    output = args.capture / "eye-measurements.json"
    output.write_text(json.dumps(result, indent=2) + "\n")
    print(output)
    if result["failures"]:
        raise SystemExit("\n".join(result["failures"]))


if __name__ == "__main__":
    main()
