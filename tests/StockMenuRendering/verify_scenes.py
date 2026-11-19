"""Check submitted stock scene captures against the original scene and material inventory."""
import argparse
import json
from pathlib import Path


def measure(run, path):
    meta = json.loads(path.read_text())
    name = meta["scene"]
    stage = "stock" if name == "MainMenu3" else "survey-" + name
    if not meta["eyeBound"] or len(meta["eyes"]) != 2:
        raise ValueError(f"{name} has no stereo source")
    for side in ("left", "right"):
        with (run / f"{stage}-{side}.png").open("rb") as eye:
            if eye.read(8) != b"\x89PNG\r\n\x1a\n":
                raise ValueError(f"Invalid {side} eye in {name}")
    issues = [row for row in meta["issues"] if "(empty slot)" not in row]
    # Both Windows Friendly45 scenes contain three disabled fir trees with a null material shader.
    expected = []
    if name in ("Friendly45_New", "Friendly45_Dark"):
        expected = [f"fir_{index}_NTMod [MeshRenderer, active=False, material=trees_elka_NT, shader=Hidden/InternalErrorShader]"
                    for index in (1, 4, 5)]
    unsupported = {row["name"]: row["slots"] for row in meta["shaderUse"]
                   if not row["supported"] or row["name"] == "Hidden/InternalErrorShader"}
    if sorted(issues) != sorted(expected) or unsupported != ({"Hidden/InternalErrorShader": 3} if expected else {}):
        raise ValueError(f"Unexpected shader on a {name} renderer: {unsupported}, {issues}")
    return name, {"sourceRun": run.name, "loadedRenderers": meta["rendererCount"],
                  "enabledRenderers": meta["enabledRendererCount"],
                  "assignedSupportedShaders": sorted(s["name"] for s in meta["shaderUse"] if s["name"] not in unsupported),
                  "stockInactivePlaceholders": issues, "emptySlots": len(meta["issues"]) - len(issues)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profiles", type=Path, help="JSON report from verify_profiles.py")
    parser.add_argument("runs", type=Path, nargs="+", help="Completed StockMenuRendering captures, newest last")
    args = parser.parse_args()
    scenes = {}
    for run in args.runs:
        if not (run / "complete").is_file():
            raise ValueError(f"Incomplete scene capture {run}")
        for path in (run / "menu.json", *sorted(run.glob("survey-*.json"))):
            name, measurements = measure(run, path)
            scenes[name] = measurements
    expected = set(json.loads(args.profiles.read_text())["scenes"])
    if expected != set(scenes):
        raise ValueError(f"Scene inventory differs: missing {sorted(expected-scenes.keys())}, extra {sorted(scenes.keys()-expected)}")
    shaders = {shader for m in scenes.values() for shader in m["assignedSupportedShaders"]}
    print(json.dumps({"sceneCount": len(scenes), "distinctAssignedSupportedShaderCount": len(shaders),
                      "scenes": dict(sorted(scenes.items()))}, indent=2))


if __name__ == "__main__":
    main()
