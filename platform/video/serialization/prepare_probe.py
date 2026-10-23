import pathlib, json, re

out = pathlib.Path("build/video/serialization")
probe = pathlib.Path("platform/video/serialization/probe/Assets/Recovered")
plan = json.loads((out / "restoration-plan.json").read_text())
raw = json.loads((out / "decoded.json").read_text())
byid = {(x["file"], x["path_id"]): x for x in raw}
expect = []
for index, m in enumerate(plan["mapping"]):
    p = out / "staged" / pathlib.Path(m["target"]).relative_to("unity-full")
    s = p.read_text()
    body = re.search(
        r"^--- !u!114 &" + str(m["target_file_id"]) + r"\n([\s\S]*?)(?=^--- !u!|\Z)", s, re.M
    )[1]
    body = re.sub(r"m_GameObject: \{fileID: \d+\}", "m_GameObject: {fileID: 1}", body)
    body = re.sub(r"m_PrefabInternal: \{fileID: \d+\}", "m_PrefabInternal: {fileID: 0}", body)
    header = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &1
GameObject:
  m_PrefabInternal: {fileID: 100100000}
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 2}
  - component: {fileID: 3}
  m_Layer: 0
  m_Name: RecoveredMediaPlayer
  m_TagString: Untagged
  m_IsActive: 0
--- !u!4 &2
Transform:
  m_PrefabInternal: {fileID: 100100000}
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 1}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 0}
--- !u!114 &3
"""
    header = (
        header.replace("!u!1 &1\n", "!u!1 &1000000000000001\n")
        .replace("!u!4 &2\n", "!u!4 &4000000000000001\n")
        .replace("!u!114 &3\n", "!u!114 &114000000000000001\n")
        .replace("{fileID: 1}", "{fileID: 1000000000000001}")
        .replace("{fileID: 2}", "{fileID: 4000000000000001}")
        .replace("{fileID: 3}", "{fileID: 114000000000000001}")
    )
    body = body.replace("m_GameObject: {fileID: 1}", "m_GameObject: {fileID: 1000000000000001}")
    name = f"MediaPlayer-{index:02}.prefab"
    (probe / name).write_text(
        (header + body).replace(
            "m_PrefabInternal: {fileID: 0}", "m_PrefabInternal: {fileID: 100100000}"
        )
        + "--- !u!1001 &100100000\nPrefab:\n  m_ObjectHideFlags: 1\n  serializedVersion: 2\n  m_Modification:\n    m_TransformParent: {fileID: 0}\n    m_Modifications: []\n    m_RemovedComponents: []\n  m_ParentPrefab: {fileID: 0}\n  m_RootGameObject: {fileID: 1000000000000001}\n  m_IsPrefabParent: 1\n"
    )
    leaves = []

    def walk(v, p=""):
        if isinstance(v, dict):
            if set(v) == {"m_FileID", "m_PathID"}:
                leaves.append(
                    dict(
                        path=p,
                        kind="reference",
                        value=plan["media_reference"]["guid"] if v["m_PathID"] else "",
                    )
                )
                return
            for k, x in v.items():
                walk(x, p + "." + k if p else k)
        elif isinstance(v, list):
            leaves.append(dict(path=p, kind="array", value=str(len(v))))
        elif p == "_events.m_TypeName":
            pass  # Removed by Unity2020 UnityEventBase; no persistent calls exist.
        else:
            leaves.append(
                dict(
                    path=p,
                    kind="bool"
                    if isinstance(v, bool)
                    else "number"
                    if isinstance(v, (int, float))
                    else "string",
                    value=str(int(v)) if isinstance(v, bool) else str(v),
                )
            )

    walk(byid[(m["source_file"], m["source_path_id"])]["fields"])
    expect.append(dict(file="Assets/Recovered/" + name, leaves=leaves))
(probe / "expected.json").write_text(json.dumps(dict(records=expect), indent=2))
print(
    "Wrote", len(expect), "prefabs with", sum(len(x["leaves"]) for x in expect), "leaf assertions"
)
