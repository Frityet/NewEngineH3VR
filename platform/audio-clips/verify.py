"""Check every staged native object and every encoded resource slice."""

from pathlib import Path
import json, hashlib, collections, UnityPy

root = Path("build/audio-clips")
m = json.load(open(root / "preservation-manifest.json"))
group = collections.defaultdict(list)
for r in m["clips"]:
    group[r["resource"]].append(r)
checked = 0
resources = []
for resource, rows in group.items():
    raw = (root / "staged" / resource).read_bytes()
    resources.append(dict(path=resource, size=len(raw), sha256=hashlib.sha256(raw).hexdigest()))
    for r in rows:
        payload = raw[r["offset"] : r["offset"] + r["size"]]
        assert hashlib.sha256(payload).hexdigest() == r["sha256"]
        env = UnityPy.load(str(root / "staged" / r["native_asset"]))
        obj = next(iter(env.objects))
        assert obj.path_id == 8300000 and obj.type.value == 83
        tree = obj.read_typetree()
        for k, v in r["metadata"].items():
            if k != "m_Resource":
                assert tree[k] == v, (r["native_asset"], k, tree[k], v)
        assert tree["m_Resource"] == dict(m_Source=resource, m_Offset=r["offset"], m_Size=r["size"])
        assert tree["m_EditorResource"] == tree["m_Resource"]
        assert tree["m_EditorCompressionFormat"] == tree["m_CompressionFormat"]
        assert not tree["m_Ambisonic"]
        checked += 1
(root / "staged-verification.json").write_text(
    json.dumps(
        dict(checked=checked, payload_bytes=m["payload_bytes"], resources=resources, errors=[]),
        indent=2,
    )
    + "\n"
)
print("VERIFIED", checked, "clips", len(resources), "resources")
