from pathlib import Path
import json, collections, gc, UnityPy, sys

orig = json.load(open("build/audio-spatial/assets.json"))["mixers"]
root = (
    Path(sys.argv[1])
    if len(sys.argv) > 1
    else Path("dist/NewEngineH3VR.app/Contents/Resources/Data")
)
output = (
    Path(sys.argv[2])
    if len(sys.argv) > 2
    else Path("build/audio-spatial/mixers/built-comparison.json")
)
current = []
for path in sorted(root.glob("*.assets")):
    env = UnityPy.load(str(path))
    for obj in env.objects:
        if obj.type.name == "AudioMixerController":
            current.append(dict(file=path.name, path_id=obj.path_id, data=obj.read_typetree()))
    del env
    gc.collect()
print("original", len(orig), "built", len(current), flush=True)


def guid(x):
    return tuple(x["data[" + str(i) + "]"] for i in range(4))


def identity(row):
    return guid(row["data"]["m_MixerConstant"]["groupGUIDs"][0])


source = {identity(x): x for x in orig}
results = []
for row in current:
    key = identity(row)
    before = source.get(key)
    if not before:
        results.append(dict(name=row["data"]["m_Name"], error="No original master group GUID"))
        continue
    a = before["data"]["m_MixerConstant"]
    b = row["data"]["m_MixerConstant"]
    diff = {
        k: dict(original=a.get(k), built=b.get(k))
        for k in a.keys() | b.keys()
        if a.get(k) != b.get(k)
    }
    settings = {
        k: dict(original=before["data"][k], built=row["data"][k])
        for k in ["m_SuspendThreshold", "m_EnableSuspend", "m_UpdateMode"]
        if before["data"][k] != row["data"][k]
    }
    results.append(
        dict(
            name=row["data"]["m_Name"],
            source=before["file"],
            source_path_id=before["path_id"],
            built_file=row["file"],
            built_path_id=row["path_id"],
            differences=diff,
            settings_differences=settings,
        )
    )
output.write_text(
    json.dumps(dict(original_count=len(orig), built_count=len(current), results=results), indent=2)
    + "\n"
)
print(
    "diffmixers",
    sum(
        bool(x.get("differences") or x.get("settings_differences") or x.get("error"))
        for x in results
    ),
)
print([(x["name"], list(x.get("differences", {}))) for x in results])
