#!/usr/bin/env python3
import pathlib, json, collections, UnityPy, gc

root = pathlib.Path(
    (
        str(pathlib.Path.home())
        + "/Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
    )
)
out = pathlib.Path("build/audio-spatial")
out.mkdir(exist_ok=True, parents=True)
report = dict(
    files=0,
    counts=collections.Counter(),
    audioManagers=[],
    spatializedSources=[],
    spatializePostEffects=[],
    blendValues=collections.Counter(),
    ambisonicClips=[],
    mixers=[],
    errors=[],
)
for entry in json.load(open("audit-assets/inventory.json"))["files"]:
    if not any("Audio" in k for k in entry["counts"]):
        continue
    env = UnityPy.load(str(root / entry["path"]))
    report["files"] += 1
    for f in env.assets:
        for o in f.objects.values():
            t = o.type.name
            if t not in ("AudioManager", "AudioSource", "AudioClip", "AudioMixerController"):
                continue
            report["counts"][t] += 1
            try:
                d = o.read_typetree()
            except Exception as e:
                report["errors"].append(
                    dict(file=entry["path"], path_id=o.path_id, type=t, error=str(e))
                )
                continue
            item = dict(file=entry["path"], path_id=o.path_id)
            if t == "AudioManager":
                report["audioManagers"].append(dict(**item, data=d))
            elif t == "AudioSource":
                if d.get("Spatialize"):
                    report["spatializedSources"].append(dict(**item, data=d))
                if d.get("SpatializePostEffects"):
                    report["spatializePostEffects"].append(item)
                blend = d.get("panLevelCustomCurve", {}).get("m_Curve", [])
                report["blendValues"][str([round(x["value"], 5) for x in blend])] += 1
            elif t == "AudioClip":
                if d.get("m_Ambisonic", False):
                    report["ambisonicClips"].append(dict(**item, name=d.get("m_Name")))
            else:
                report["mixers"].append(dict(**item, data=d))
    del env
    gc.collect()
    print(entry["path"], dict(report["counts"]), flush=True)
(out / "assets.json").write_text(json.dumps(report, indent=2) + "\n")
print("SUMMARY", json.dumps({k: v for k, v in report.items() if k != "mixers"}))
