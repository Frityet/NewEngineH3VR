"""Compare authored stock post-processing data before and after Unity script migration."""
import argparse
from collections import Counter
import json
from pathlib import Path
import re
import subprocess


# Unity's file IDs for the original post-processing classes inside the retargeted game DLL.
CLASS_IDS = {"PostProcessProfile": -2099961501, "PostProcessVolume": 708136152,
             "ColorGrading": -472490013, "Bloom": -1437917544, "AmbientOcclusion": 359755753}
SCRIPT = re.compile(r"^  m_Script: \{fileID: (-?\d+), guid: ([a-f0-9]+), type: 3\}$", re.M)
DOCUMENT = re.compile(r"^--- !u!(\d+) &(-?\d+)[^\n]*\n", re.M)


def guid(meta):
    return re.search(r"^guid: (\w+)$", meta.read_text(), re.M)[1]


def components(path):
    text = path.read_text()
    headers = list(DOCUMENT.finditer(text))
    return {header[2]: text[header.end():headers[i + 1].start() if i + 1 < len(headers) else len(text)]
            for i, header in enumerate(headers) if header[1] == "114"}


def verify(extracted, imported):
    scripts = extracted / "Scripts/Assembly-CSharp/UnityEngine/Rendering/PostProcessing"
    classes = {guid(scripts / (name + ".cs.meta")): name for name in CLASS_IDS}
    game = guid(imported / "Plugins/NativeH3VR.Game.dll.meta")
    profile = guid(scripts / "PostProcessProfile.cs.meta")
    volume = guid(scripts / "PostProcessVolume.cs.meta")
    files = subprocess.check_output(["rg", "--no-ignore", "--files-with-matches", "-0", "-g", "*.unity",
                                     "-g", "*.prefab", "-g", "*.asset", profile + "|" + volume, str(extracted)])
    counts = Counter()
    scenes = set()
    linked = set()
    profiles = {}
    for filename in sorted(files.decode().rstrip("\0").split("\0")):
        path = Path(filename)
        relative = path.relative_to(extracted)
        source = components(path)
        rebuilt = components(imported / relative)
        for file_id, body in source.items():
            script = SCRIPT.search(body)
            if script is None or script[2] not in classes:
                continue
            name = classes[script[2]]
            binding = f"  m_Script: {{fileID: {CLASS_IDS[name]}, guid: {game}, type: 3}}"
            if SCRIPT.sub(binding, body, count=1) != rebuilt.get(file_id):
                raise ValueError(f"Changed stock {name} fields or reference at {relative}#{file_id}")
            counts[name] += 1
            if name == "PostProcessVolume":
                if path.suffix == ".unity":
                    scenes.add(path.stem)
                link = re.search(r"^  sharedProfile: \{fileID: -?\d+, guid: ([a-f0-9]+)", body, re.M)
                if link:
                    linked.add(link[1])
            if name == "PostProcessProfile":
                profile_guid = guid(Path(str(path) + ".meta"))
                if guid(Path(str(imported / relative) + ".meta")) != profile_guid:
                    raise ValueError(f"Stock profile GUID changed at {relative}")
                families = []
                for ref in re.findall(r"^  - \{fileID: (-?\d+)\}$", body, re.M):
                    families.append(classes[SCRIPT.search(source[ref])[2]])
                profiles[profile_guid] = {"file": relative.as_posix(), "effects": families}
    if set(profiles) != linked or counts != {"PostProcessVolume": 155, "PostProcessProfile": 25,
                                              "ColorGrading": 13, "Bloom": 8, "AmbientOcclusion": 4} or len(scenes) != 46:
        raise ValueError(f"Unexpected profile inventory for Steam build 25002541: {counts}, {len(scenes)} scenes")
    return {"authoringCounts": dict(counts), "sceneCount": len(scenes),
            "scenes": sorted(scenes), "profiles": profiles}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("extracted", type=Path, help="AssetRipper ExportedProject")
    parser.add_argument("imported", type=Path, help="Retargeted Unity project")
    args = parser.parse_args()
    print(json.dumps(verify(args.extracted / "Assets", args.imported / "Assets"), indent=2))


if __name__ == "__main__":
    main()
