"""Compare Unity5/2021 mixer DSP graphs independent of compiler array order."""

from pathlib import Path
import UnityPy, json, collections, hashlib, sys

NONE = 4294967295


def guid(d):
    return "".join("%08x" % d["data[" + str(i) + "]"] for i in range(4))


def norm(data):
    c = data["m_MixerConstant"]
    gg = list(map(guid, c["groupGUIDs"]))
    eg = list(map(guid, c["effectGUIDs"]))
    sg = list(map(guid, c["snapshotGUIDs"]))
    params = {}
    groups = {}
    effects = {}
    connections = []
    names = bytes(c["groupNameBuffer"]).rstrip(b"\0").decode().split("\0")
    for i, g in enumerate(c["groups"]):
        key = gg[i]
        params[g["volumeIndex"]] = "group:" + key + ":volume"
        params[g["pitchIndex"]] = "group:" + key + ":pitch"
        parent = None if g["parentConstantIndex"] < 0 else gg[g["parentConstantIndex"]]
        groups[key] = dict(
            name=names[i],
            parent=parent,
            mute=g["mute"],
            solo=g["solo"],
            bypass=g["bypassEffects"],
            effects=[eg[j] for j, e in enumerate(c["effects"]) if e["groupConstantIndex"] == i],
        )
        if parent:
            connections.append((key, parent, None))
    for i, e in enumerate(c["effects"]):
        key = eg[i]
        send = None if e["sendTargetEffectIndex"] == NONE else eg[e["sendTargetEffectIndex"]]
        wet = e["wetMixLevelIndex"] != NONE
        if wet:
            params[e["wetMixLevelIndex"]] = "effect:" + key + ":wet"
        for j, index in enumerate(e["parameterIndices"]):
            params[index] = "effect:" + key + ":param" + str(j)
        effects[key] = dict(
            type=e["type"],
            group=gg[e["groupConstantIndex"]],
            send=send,
            wet=wet,
            bypass=e["bypass"],
            parameterCount=len(e["parameterIndices"]),
        )
        # prevEffectIndex describes global execution array order, not a cross-group
        # DSP connection; each compiler forms its own linear instruction chain.
        assert e["prevEffectIndex"] == (i - 1 if i else NONE)
        if send is not None:
            connections.append(
                (
                    gg[e["groupConstantIndex"]],
                    gg[c["effects"][e["sendTargetEffectIndex"]]["groupConstantIndex"]],
                    key,
                )
            )
    if "groupConnections" in c:
        actual = [
            (
                gg[x["sourceGroupIndex"]],
                gg[x["targetGroupIndex"]],
                None if x["sendEffectIndex"] == NONE else eg[x["sendEffectIndex"]],
            )
            for x in c["groupConnections"]
        ]
        routed = [
            x for x in connections if x[2] is None or effects[effects[x[2]]["send"]]["type"] == -4
        ]
        # Duck Volume targets consume a sidechain buffer, not an audible group route.
        assert all(effects[e["send"]]["type"] in (-4, -5) for e in effects.values() if e["send"])
        assert collections.Counter(actual) == collections.Counter(routed), (
            "Explicit2021routingdiffersfromgroup/sendgraph"
        )
    snaps = {}
    for i, s in enumerate(c["snapshots"]):
        assert set(params) == set(range(len(s["values"]))), "Unmapped snapshot parameters"
        snaps[sg[i]] = dict(
            nameHash=s["nameHash"],
            values={params[j]: v for j, v in enumerate(s["values"])},
            transitions={
                params[j]: v for j, v in zip(s["transitionIndices"], s["transitionTypes"])
            },
        )
    return dict(
        groups=groups,
        effects=effects,
        snapshots=snaps,
        connections=sorted(connections, key=str),
        numSideChainBuffers=c["numSideChainBuffers"],
        pluginEffectNameBuffer=c["pluginEffectNameBuffer"],
        snapshotNameBuffer=c["snapshotNameBuffer"],
        exposedParameterNames=c["exposedParameterNames"],
        exposedParameterTargets=[params[i] for i in c["exposedParameterIndices"]],
        settings={k: data[k] for k in ["m_SuspendThreshold", "m_EnableSuspend", "m_UpdateMode"]},
    )


def digest(d):
    return hashlib.sha256(json.dumps(d, sort_keys=True).encode()).hexdigest()


original = json.load(open("build/audio-spatial/assets.json"))["mixers"]
orig = []
for row in original:
    orig.append(
        dict(
            source=row["file"],
            path_id=row["path_id"],
            name=row["data"]["m_Name"],
            normalized=norm(row["data"]),
        )
    )
env = UnityPy.load(sys.argv[1])
built = []
for obj in env.objects:
    if obj.type.name == "AudioMixerController":
        tree = obj.read_typetree()
        built.append(dict(path_id=obj.path_id, name=tree["m_Name"], normalized=norm(tree)))
a = collections.Counter(digest(x["normalized"]) for x in orig)
b = collections.Counter(digest(x["normalized"]) for x in built)
report = dict(
    original_count=len(orig),
    built_count=len(built),
    same_semantic_multiset=a == b,
    unmatched_original=list((a - b).elements()),
    unmatched_built=list((b - a).elements()),
    original=orig,
    built=built,
)
Path(sys.argv[2]).write_text(json.dumps(report, indent=2) + "\n")
print(
    "DSP SEMANTIC COMPARISON",
    len(orig),
    len(built),
    "equal",
    a == b,
    "unmatched",
    len(report["unmatched_original"]),
    len(report["unmatched_built"]),
)
assert a == b
