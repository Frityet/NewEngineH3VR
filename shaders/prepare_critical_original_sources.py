"""Install isolated actual critical source shader identities beside per-subshader probes."""

from pathlib import Path
import json, re, copy, shutil, hashlib

root = Path(__file__).resolve().parents[1]
probe = root / "critical-probe"
path = probe / "Assets/Resources/OriginalVariants.json"
j = json.loads(path.read_text())
base = [s for s in j["shaders"] if not s.get("resourcePath")]
out = probe / "Assets/Resources/OriginalSources"
out.mkdir(exist_ok=True)
rows = []


def blocks(s, token):
    for m in re.finditer(r"\b" + token + r"\s*\{", s):
        i = m.end()
        d = 1
        while d:
            d += (s[i] == "{") - (s[i] == "}")
            i += 1
        yield s[m.start() : i]


for item in base:
    if item["subshader"] != 0:
        continue
    source = root / "shaders/uncovered-packaged" / item["source_file"]
    s = source.read_text()
    identities = []
    if source.name == "Hidden_Thermal.shader":
        seen = {}

        def unique_name(m):
            name = m[1]
            n = seen.get(name, 0)
            seen[name] = n + 1
            return m[0] if n == 0 else 'Name "' + name + "_NATIVE_SUB" + str(n) + '"'

        s = re.sub(r"Name \"([^\"]+)\"", unique_name, s)
    for sub in blocks(s, "SubShader"):
        for body in blocks(sub, "Pass"):
            header = body.split("CGPROGRAM")[0]
            name = re.search(r'Name "([^"]+)"', header)[1]
            mode = re.search(r'"LIGHTMODE"\s*=\s*"([^"]+)"', header)
            identities.append((mode[1] if mode else "ALWAYS", name))
    assert len(identities) == len(set(identities)), (source, identities)
    target = out / source.name
    target.write_text(s)
    actual = copy.deepcopy(item)
    actual.update(
        file=source.name,
        name=re.search(r'Shader "([^"]+)"', s)[1],
        resourcePath="OriginalSources/" + source.stem,
    )
    for p, identity in zip(actual["passes"], identities):
        p["passName"] = identity[1]
    rows.append(actual)
path.write_text(json.dumps({"shaders": base + rows}, indent=2))
print("Added", len(rows), "actual shader sources")
