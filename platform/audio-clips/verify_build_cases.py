from pathlib import Path
import UnityPy, json, hashlib
from UnityPy.helpers.ResourceReader import get_resource_data

rows = json.load(open("build/audio-clips/playback-cases.json"))
byname = {r["metadata"]["m_Name"]: r for r in rows}
assert len(byname) == 14
env = UnityPy.load("build/audio-clips/AudioClipProbe.app/Contents/Resources/Data/resources.assets")
results = []
for obj in env.objects:
    if obj.type.name != "AudioClip":
        continue
    tree = obj.read_typetree()
    name = tree["m_Name"]
    if name not in byname:
        continue
    row = byname[name]
    res = tree["m_Resource"]
    raw = get_resource_data(res["m_Source"], obj.assets_file, res["m_Offset"], res["m_Size"])
    sha = hashlib.sha256(raw).hexdigest()
    diff = {
        k: [v, tree.get(k)]
        for k, v in row["metadata"].items()
        if k not in ("m_Name", "m_Resource") and tree.get(k) != v
    }
    assert sha == row["sha256"] and not diff, (name, diff)
    results.append(
        dict(
            name=name,
            original_name=row["metadata"]["m_Name"],
            bytes=len(raw),
            sha256=sha,
            relocated_resource=res,
            metadata_differences=diff,
        )
    )
assert len(results) == 14
Path("build/audio-clips/build-2021-cases-proof.json").write_text(
    json.dumps(dict(count=len(results), clips=results, errors=[]), indent=2) + "\n"
)
print("EXACT ORIGINAL PAYLOAD AND METADATA:", len(results), "cases")
