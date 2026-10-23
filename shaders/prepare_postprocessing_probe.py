from pathlib import Path
import re, json, shutil

root = Path(__file__).resolve().parents[1]
src = root / "shaders/postprocessing-2017/PostProcessing/Shaders"
dest = root / "shader-probe/Assets/NativePostProcessing/Shaders"
for p in src.rglob("*"):
    if p.suffix in (".hlsl", ".cginc"):
        q = dest / p.relative_to(src)
        q.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(p, q)
selected = {
    "Hidden/PostProcessing/AutoExposure",
    "Hidden/PostProcessing/Debug/Histogram",
    "Hidden/PostProcessing/Debug/LightMeter",
    "Hidden/PostProcessing/Debug/Vectorscope",
    "Hidden/PostProcessing/Debug/Waveform",
    "Hidden/PostProcessing/DepthOfField",
    "Hidden/PostProcessing/ScreenSpaceReflections",
}
rows = []


def rewrite(p):
    s = p.read_text()

    def inc(m):
        val = m.group(1)
        candidate = (p.parent / val).resolve()
        if not candidate.is_relative_to(src) or not candidate.exists():
            return m.group(0)
        return (
            '#include "Assets/NativePostProcessing/Shaders/' + str(candidate.relative_to(src)) + '"'
        )

    return re.sub(r'#include\s+"([^"]+)"', inc, s)


for p in src.rglob("*.shader"):
    name = re.search(r'Shader\s+"([^"]+)"', p.read_text()).group(1)
    if name not in selected:
        continue
    target = root / "shader-probe/Assets/Resources/Shaders" / (name.replace("/", "_") + ".shader")
    before = target.read_text()
    s = rewrite(p)
    target.write_text(s)
    rows.append(
        {
            "name": name,
            "source": str(p.relative_to(root)),
            "probe": str(target.relative_to(root)),
            "original_passes": len(re.findall(r"\bPass\s*{", before)),
            "source_passes": len(re.findall(r"\bPass\s*{", s)),
        }
    )
for p in (root / "shaders/compute-original").glob("[0-9]*.json"):
    d = json.loads(p.read_text())
    name = d["m_Name"]
    source = next(src.rglob(name + ".compute"))
    s = rewrite(source)
    target = root / "shader-probe/Assets/Resources/Computes" / (name + ".compute")
    target.parent.mkdir(exist_ok=True)
    target.write_text(s)
    kernels = sorted({k["name"] for v in d["variants"] for k in v["kernels"]})
    bindings = sorted(
        {
            b["name"]
            for v in d["variants"]
            for k in v["kernels"]
            for kind in ["textures", "inBuffers", "outBuffers"]
            for b in k[kind]
        }
    )
    corpus = s + "\n" + "\n".join(x.read_text() for x in src.rglob("*.hlsl"))
    rows.append(
        {
            "name": name,
            "source": str(source.relative_to(root)),
            "probe": str(target.relative_to(root)),
            "original_kernels": kernels,
            "source_kernels": sorted(set(re.findall(r"#pragma kernel (\w+)", s))),
            "original_bindings": bindings,
            "missing_bindings": [
                b for b in bindings if not re.search(r"\b" + re.escape(b) + r"\b", corpus)
            ],
        }
    )
(root / "shaders/postprocessing-2017-match.json").write_text(json.dumps(rows, indent=2))
print(json.dumps(rows, indent=2))
