# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy", "pillow"]
# ///
"""Measure the actual raised T&H cover surface in both submitted eyes."""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter


def measure_eye(folder, stem, eye):
    def read(mode):
        with Image.open(folder / f"{stem}-{mode}-{eye}.png") as image:
            return np.asarray(image.convert("RGB"), dtype=np.int16)

    original, enabled, unlit, marker = map(read, ("original", "active-probes", "albedo", "mask"))
    if len({frame.shape for frame in (original, enabled, unlit, marker)}) != 1:
        raise ValueError("Mismatched eye dimensions")
    red, green, blue = marker.transpose(2, 0, 1)
    # Remove green scene lights that stayed green in the unchanged material, and drop AA edges.
    mask = (green > 110) & (green > red * 1.7 + 40) & (green > blue * 1.7 + 40)
    mask &= np.abs(marker - original).max(axis=2) > 25
    clean = Image.fromarray(mask.astype(np.uint8) * 255).filter(ImageFilter.MinFilter(5))
    inside = np.asarray(clean) > 0
    count = int(inside.sum())
    if count < 512:
        raise ValueError(f"Too little visible cover geometry in the {eye} eye ({count} pixels)")
    weights = np.array([0.2126, 0.7152, 0.0722])

    def stats(image):
        samples = image[inside]
        luma = samples @ weights
        return {"lumaMean": round(float(luma.mean()), 3),
                "lumaP90": round(float(np.percentile(luma, 90)), 3),
                "nearBlackFraction": round(float((samples.max(axis=1) <= 4).mean()), 5)}

    difference = np.abs((original[inside] - enabled[inside]) @ weights)
    disabled = folder / f"{stem}-inactive-probes-{eye}.png"
    return {"dimensions": [original.shape[1], original.shape[0]], "coverPixels": count,
            "original": stats(original), "allBakedProbesEnabled": stats(enabled), "albedo": stats(unlit),
            "allBakedProbesDisabled": stats(read("inactive-probes")) if disabled.is_file() else None,
            "meanAbsoluteDifferenceToEnabled": round(float(difference.mean()), 3),
            "fractionDifferingOver8": round(float((difference > 8).mean()), 5)}


def teal_stripe(image):
    # The retail observer faces the right-hand wall of Classic's first Hold on route 0.
    rgb = np.asarray(image.convert("RGB"), dtype=np.int16)
    height, width = rgb.shape[:2]
    wall = rgb[int(height * .45):int(height * .72), int(width * .75):int(width * .99)]
    r, g, b = wall.transpose(2, 0, 1)
    painted = (g > r + 12) & (g > b + 3) & (b > r) & (g < 196)
    longest = 0
    for row in painted:
        edges = np.diff(np.r_[False, row, False].astype(np.int8))
        runs = np.flatnonzero(edges == -1) - np.flatnonzero(edges == 1)
        if runs.size:
            longest = max(longest, int(runs.max()))
    return {"tealPixels": int(painted.sum()), "regionFraction": round(float(painted.mean()), 5),
            "longestRunFractionOfEyeWidth": round(longest / width, 5)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--verify", action="store_true", help="Fail on dark steel or missing room reflections")
    parser.add_argument("--out", type=Path)
    parser.add_argument("--retail-room", type=Path, help="Stereo retail reference at the same Classic route 0 observer")
    args = parser.parse_args()
    files = list(args.capture.glob("*-cover-*-materials.json"))
    if len(files) != 1:
        parser.error("Choose an --inspect-cover capture of one live Hold")
    state = json.loads(files[0].read_text())
    stem = files[0].name.removesuffix("-materials.json")
    eyes = {eye: measure_eye(args.capture, stem, eye) for eye in ("left", "right")}
    probes = state["activeProbes"]
    assigned = sum(bool(r["reflectionProbes"]) for r in state["covers"])
    result = {"scene": state["scene"], "raisedCoverMeshes": len(state["covers"]),
              "meshesWithAssignedProbes": assigned, "enabledSceneProbes": sum(p["enabled"] for p in probes),
              "sceneProbeCount": len(probes), "eyes": eyes,
              "method": "Frozen live Hold. The mask selects the steel meshes; smoke is disabled in all compared frames."}
    failed = []
    for eye, row in eyes.items():
        if row["albedo"]["lumaP90"] < 24:
            failed.append(f"{eye} albedo control is dark")
        if row["original"]["lumaP90"] < 16 or row["original"]["nearBlackFraction"] > 0.75:
            failed.append(f"{eye} steel is near black")
        if row["fractionDifferingOver8"] > 0.10:
            failed.append(f"{eye} steel differs materially from the enabled-probes control")
    if probes and state["scene"] == "TakeAndHoldClassic" and assigned == 0:
        failed.append("No raised steel mesh has an assigned room probe")
    if args.retail_room:
        if state["scene"] != "TakeAndHoldClassic":
            parser.error("--retail-room requires the matching Classic Hallways Hold")
        walls = result["bakedWallStripe"] = {}
        with Image.open(args.retail_room) as retail:
            width = retail.width // 2
            for i, eye in enumerate(("left", "right")):
                reference = teal_stripe(retail.crop((i * width, 0, (i + 1) * width, retail.height)))
                with Image.open(args.capture / f"{stem}-original-{eye}.png") as actual:
                    sample = teal_stripe(actual)
                walls[eye] = {"steam": reference, "converted": sample}
                for key, minimum in (("regionFraction", .006), ("longestRunFractionOfEyeWidth", .035)):
                    if reference[key] < minimum:
                        parser.error(f"Retail reference lacks the visible baked wall stripe in the {eye} eye")
                    if sample[key] < minimum:
                        failed.append(f"{eye} baked wall stripe absent or occluded ({key})")
    result["failures"] = failed
    encoded = json.dumps(result, indent=2)
    if args.out:
        args.out.write_text(encoded + "\n")
    else:
        print(encoded)
    if args.verify and failed:
        raise SystemExit("Cover comparison failed: " + ", ".join(failed))


if __name__ == "__main__":
    main()
