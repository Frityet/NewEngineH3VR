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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--verify", action="store_true", help="Fail on dark steel or missing room reflections")
    parser.add_argument("--out", type=Path)
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
