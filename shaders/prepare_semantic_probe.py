"""Make isolated native proof projects for corrected candidates, preserving actual sources.
Every original subshader is independently drawable. Multi-pass actual shader resources
are also exercised at their supported first subshader; no full graph mutations.
"""

from pathlib import Path
import json, re, shutil, copy, hashlib

root = Path(__file__).resolve().parents[1]
package = root / "shaders/semantic-v2-packaged"
manifest = json.loads((package / "manifest.json").read_text())
rows = [r for r in manifest["rows"] if r["status"].startswith("candidate")]


def blocks(s, token):
    for m in re.finditer(r"\b" + token + r"\s*\{", s):
        i = m.end()
        d = 1
        while d:
            d += (s[i] == "{") - (s[i] == "}")
            i += 1
        yield m.start(), i, s[m.start() : i]


# Put critical PP/optic branches in one project, other recovered shaders in another.
def group(name):
    return (
        "semantic-optics"
        if (
            "PostProcessing/" in name
            or "PIPScope" in name
            or "Reflex Sight/" in name
            or "ScopeClearLens" in name
            or "Holographic" in name
            or "Holograpic" in name
            or name == "Hidden/Thermal"
        )
        else "semantic-materials"
    )


for batch in ["semantic-optics", "semantic-materials"]:
    probe = root / (batch + "-probe")
    (probe / "Assets/Resources/Shaders").mkdir(parents=True, exist_ok=True)
    (probe / "Assets/Resources/OriginalSources").mkdir(exist_ok=True)
    (probe / "Assets/Editor").mkdir(exist_ok=True)
    probes = []
    deps = set()
    invent = []
    for d in ["ProjectSettings", "Packages"]:
        (probe / d).mkdir(exist_ok=True)
        for p in (root / "critical-probe" / d).glob("*"):
            if p.is_file() and p.name not in [
                "GraphicsSettings.asset",
                "EditorBuildSettings.asset",
            ]:
                shutil.copyfile(p, probe / d / p.name)
    for row in rows:
        if group(row["identity"]["name"]) != batch:
            continue
        source = root / row["source"]
        s = source.read_text()
        shutil.copyfile(source, probe / "Assets/Resources/OriginalSources" / source.name)
        subs = list(blocks(s, "SubShader"))
        assert subs, source
        prefix = s[: subs[0][0]]
        actual = None
        for si, (_, _, sub) in enumerate(subs):
            passes = []
            actualpasses = []
            shift = 0
            for pi, (a, b, body) in enumerate(list(blocks(sub, "Pass"))):
                if "CGPROGRAM" not in body:
                    continue
                header = body.split("CGPROGRAM")[0]
                name = re.search(r'\bName\s+"([^"]+)"', header)[1]
                mode = re.search(r'"LIGHTMODE"\s*=\s*"([^"]+)"', header)
                mode = mode[1].upper() if mode else "ALWAYS"
                keys = [
                    " ".join(sorted(re.findall(r"(?<!!)defined\((\w+)\)", c)))
                    for c in re.findall(r"^\s*#(?:if|elif) (.+)$", body, re.M)
                ] or [""]
                passes.append(
                    {
                        "passName": "NATIVE_PASS_" + str(pi),
                        "index": pi,
                        "lightMode": mode,
                        "allowed": keys,
                    }
                )
                actualpasses.append(dict(passes[-1], passName=name))
                replacement = re.sub(r'\bName\s+"[^"]+"', f'Name "NATIVE_PASS_{pi}"', body, count=1)
                sub = sub[: a + shift] + replacement + sub[b + shift :]
                shift += len(replacement) - (b - a)
            if not passes:
                continue
            fn = source.stem + "__Sub" + str(si) + ".shader"
            name = "NativeSemantic/" + source.stem + "/Sub" + str(si)
            text = re.sub(r'Shader "[^"]+"', f'Shader "{name}"', prefix + sub + "\n}", count=1)
            (probe / "Assets/Resources/Shaders" / fn).write_text(text)
            probes.append({"file": fn, "name": name, "subshader": si, "passes": passes})
            if si == 0:
                actual = {
                    "file": source.name,
                    "name": row["identity"]["name"],
                    "resourcePath": "OriginalSources/" + source.stem,
                    "passes": actualpasses,
                }
        if actual:
            probes.append(actual)
        deps.update(re.findall(r'#include "(Assets/NativeShaders/[^\"]+)"', s))
        invent.append(
            {"source": row["source"], "sha256": row["sha256"], "identity": row["identity"]}
        )
    for rel in sorted(deps):
        target = probe / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(package / rel, target)
    (probe / "Assets/Resources/OriginalVariants.json").write_text(
        json.dumps({"shaders": probes}, indent=2)
    )
    shutil.copyfile(
        root / "shaders/NativeOriginalVariantFilter.cs",
        probe / "Assets/Editor/NativeOriginalVariantFilter.cs",
    )
    runtime = (
        (root / "critical-probe/Assets/CriticalGpuProbe.cs")
        .read_text()
        .replace("CriticalGpuProbe", "SemanticGpuProbe")
        .replace("NATIVEH3VR_CRITICAL_REPORT", "NATIVEH3VR_SEMANTIC_REPORT")
        .replace("int index=criticalPass++;", "int index=pass.index;")
    )
    (probe / "Assets/SemanticGpuProbe.cs").write_text(runtime)
    build = (
        (root / "critical-probe/Assets/Editor/BuildCriticalProbe.cs")
        .read_text()
        .replace("BuildCriticalProbe", "BuildSemanticProbe")
        .replace("CriticalGpuProbe", "SemanticGpuProbe")
        .replace("CriticalNativeProbe.app", batch + "NativeProbe.app")
        .replace("NativeH3VR Critical Probe", "NativeH3VR Semantic Probe")
        .replace("criticalprobe", batch.replace("-", "") + "probe")
    )
    (probe / "Assets/Editor/BuildSemanticProbe.cs").write_text(build)
    report = {
        "batch": batch,
        "sources": invent,
        "probe_objects": len(probes),
        "original_branch_draws": sum(len(p["allowed"]) for s in probes for p in s["passes"]),
        "mono_runtime_draws": sum(
            sum("UNITY_SINGLE_PASS_STEREO" not in k.split(" ") for k in p["allowed"])
            for s in probes
            for p in s["passes"]
            if p["lightMode"] != "META"
        ),
        "dependencies": len(deps),
    }
    (root / f"shaders/{batch}-probe-inventory.json").write_text(json.dumps(report, indent=2))
    print(batch, {k: v for k, v in report.items() if k != "sources"})
