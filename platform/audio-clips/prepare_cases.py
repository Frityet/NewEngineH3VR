from pathlib import Path
import UnityPy, json, copy

root = Path("build/audio-clips/AudioPlaybackProof.app/Contents/Resources/Data")
manifest = json.load(open("build/audio-clips/preservation-manifest.json"))
groups = {}
for row in manifest["clips"]:
    t = row["metadata"]
    key = (row["fsb_format"], t["m_LoadType"], t["m_Channels"], t["m_Frequency"])
    if t["m_Length"] < 0.3:
        continue
    if key not in groups or row["size"] < groups[key]["size"]:
        groups[key] = row
rows = list(groups.values())
Path("build/audio-clips/playback-cases.json").write_text(json.dumps(rows, indent=2))
env = UnityPy.load("dist/NewEngineH3VR.app/Contents/Resources/Data/resources.assets")
prototype = next(o for o in env.objects if o.type.name == "AudioClip")
f = prototype.assets_file
tree = prototype.read_typetree()
objects = {}
with (root / "AudioOriginal.resource").open("wb") as output:
    for i, row in enumerate(rows):
        obj = copy.copy(prototype)
        obj.path_id = i + 1
        t = copy.deepcopy(tree)
        t.update(row["metadata"])
        t["m_Ambisonic"] = False
        raw = (Path("build/audio-clips/staged") / row["resource"]).read_bytes()[
            row["offset"] : row["offset"] + row["size"]
        ]
        t["m_Resource"] = {
            "m_Source": "AudioOriginal.resource",
            "m_Offset": output.tell(),
            "m_Size": len(raw),
        }
        output.write(raw)
        obj.save_typetree(t)
        objects[obj.path_id] = obj
f.objects = objects
(root / "resources.assets").write_bytes(f.save())
env = UnityPy.load(str(root / "globalgamemanagers"))
obj = next(o for o in env.objects if o.type.name == "ResourceManager")
tree = obj.read_typetree()
tree["m_Container"] = [
    ("case" + str(i), {"m_FileID": 5, "m_PathID": i + 1}) for i in range(len(rows))
]
tree["m_DependentAssets"] = []
obj.save_typetree(tree)
(root / "globalgamemanagers").write_bytes(obj.assets_file.save())
print("Prepared", len(rows), "cases")
