#!/usr/bin/env python3
"""Produce isolated PIP shader candidates restoring a proven missing legacy default.

Run with audit-assets/.venv/bin/python for UnityPy. No input, staging or app file
is modified. Original serialized pass identities and field absence are required;
an authored literal/property-backed zClip state is never rewritten.
"""

from __future__ import annotations

import argparse
import gc
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
INVENTORY = ROOT / "shaders/semantic-optics-probe-inventory.json"
DEFAULT_SOURCE = Path.home() / "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data"
PIP_FILE = re.compile(r"Unlit_PIPScope(?:_[0-7])?\.shader\Z")
DOC = "https://docs.unity3d.com/2021.3/Documentation/Manual/SL-ZClip.html"
EXPORTER = ROOT / ".tools/assetripper-source-1.0.15/Source"


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def blank(text: str) -> str:
    return "".join(c if c in "\r\n" else " " for c in text)


def mask(text: str) -> str:
    # Keep all character offsets stable while removing HLSL braces, comments
    # and quoted text from the ShaderLab structure scan.
    programs = re.compile(
        r"(?ms)^[ \t]*(CGPROGRAM|CGINCLUDE|HLSLPROGRAM|HLSLINCLUDE|GLSLPROGRAM)\b.*?^[ \t]*(?:ENDCG|ENDHLSL|ENDGLSL)\b"
    )
    result = programs.sub(lambda m: blank(m[0]), text)
    tokens = re.compile(r'//[^\r\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"')
    return tokens.sub(lambda m: blank(m[0]), result)


def shader_passes(text: str) -> list[list[dict]]:
    masked = mask(text)
    # Only actual ShaderLab declarations are block owners. A SubShader-level
    # ZClip directive is rejected later rather than guessing inheritance.
    kinds = {m.end() - 1: m[1] for m in re.finditer(r"\b(SubShader|Pass)\s*\{", masked)}
    stack: list[dict] = []
    subshaders: list[dict] = []
    all_passes: list[dict] = []
    for match in re.finditer(r"[{}]", masked):
        if match[0] == "{":
            node = {"kind": kinds.get(match.start()), "start": match.start(), "end": None}
            if node["kind"] == "SubShader":
                node["index"] = len(subshaders)
                subshaders.append(node)
            if node["kind"] == "Pass":
                owner = next((n for n in reversed(stack) if n["kind"] == "SubShader"), None)
                if owner is None:
                    raise ValueError("Pass outside SubShader")
                node["subshader"] = owner["index"]
                all_passes.append(node)
            stack.append(node)
        else:
            if not stack:
                raise ValueError("Unbalanced ShaderLab closing brace")
            stack.pop()["end"] = match.end()
    if stack or not subshaders:
        raise ValueError("Unbalanced/missing ShaderLab SubShader")
    grouped: list[list[dict]] = [[] for _ in subshaders]
    covered: list[tuple[int, int]] = []
    for item in all_passes:
        start, end = item["start"], item["end"]
        body = masked[start:end]
        names = list(re.finditer(r"\bName\b", body))
        if len(names) != 1:
            raise ValueError("Each recovered PIP Pass must have exactly one Name")
        name = re.match(r'Name\s+"([^"\r\n]*)"', text[start + names[0].start() :])
        if name is None:
            raise ValueError("Malformed Pass Name")
        directives = []
        for command in re.finditer(r"(?m)^[ \t]*ZClip\b[^\r\n]*", body):
            a, b = start + command.start(), start + command.end()
            directives.append((a, b, masked[a:b]))
            covered.append((a, b))
        grouped[item["subshader"]].append({"name": name[1], "directives": directives})
    for command in re.finditer(r"\bZClip\b", masked):
        if not any(a <= command.start() < b for a, b in covered):
            raise ValueError("ZClip outside a recognized Pass directive; inheritance is ambiguous")
    return grouped


def repair_bytes(before: bytes, parsed_form: dict) -> tuple[bytes, list[dict]]:
    """Pure bounded transformation; tests also exercise explicit/newer states."""
    text = before.decode("utf-8")
    declarations = list(re.finditer(r"\bShader\b", mask(text)))
    declaration = (
        re.match(r'Shader\s+"([^"\r\n]+)"', text[declarations[0].start() :])
        if len(declarations) == 1
        else None
    )
    if (
        declaration is None
        or declaration[1] != "Unlit/PIPScope"
        or parsed_form.get("m_Name") != "Unlit/PIPScope"
    ):
        raise ValueError("Only the original Unlit/PIPScope family is admitted")
    current = shader_passes(text)
    original = parsed_form.get("m_SubShaders", [])
    if len(current) != len(original):
        raise ValueError("Original/recovered SubShader count differs")
    edits: list[tuple[int, int, str]] = []
    rows = []
    for si, passes in enumerate(current):
        original_passes = original[si]["m_Passes"]
        if len(passes) != len(original_passes):
            raise ValueError("Original/recovered pass count differs")
        for pi, (candidate, original_pass) in enumerate(zip(passes, original_passes)):
            state = original_pass["m_State"]
            name = state.get("m_Name", "")
            if candidate["name"] != name:
                raise ValueError(
                    f"Pass identity differs at {si}/{pi}: {candidate['name']!r} != {name!r}"
                )
            authored_keys = [k for k in state if k.replace("_", "").lower() in ("zclip", "mzclip")]
            row = {
                "subshader": si,
                "pass": pi,
                "name": name,
                "original_zclip_field_present": bool(authored_keys),
                "original_state_sha256": sha(
                    json.dumps(state, sort_keys=True, separators=(",", ":")).encode()
                ),
            }
            commands = candidate["directives"]
            if len(commands) > 1:
                raise ValueError(f"Multiple ZClip commands at {si}/{pi}")
            if authored_keys:
                row["action"] = "preserved-explicit-original-state"
            elif not commands:
                row["action"] = "default-already-omitted"
            else:
                a, b, masked_command = commands[0]
                literal = re.fullmatch(r"[ \t]*ZClip[ \t]+(Off|On)[ \t]*", masked_command)
                if literal is None:
                    row["action"] = "preserved-property-or-nonliteral-candidate"
                elif literal[1] == "On":
                    row["action"] = "default-already-on"
                else:
                    # Change only the three bytes spelling Off to two bytes On;
                    # whitespace, trailing comments, properties and HLSL stay exact.
                    start, end = a + literal.start(1), a + literal.end(1)
                    edits.append((start, end, "On"))
                    row["action"] = "invented-off-to-default-on"
                    row["byte_offset"] = len(text[:start].encode("utf-8"))
            rows.append(row)
    after = text
    for start, end, replacement in reversed(edits):
        after = after[:start] + replacement + after[end:]
    # Reverse the exact edits, accounting for changed lengths, to prove that
    # all bytes outside the admitted tokens are identical to the input.
    reconstructed = after
    delta = 0
    applied = []
    for start, end, replacement in edits:
        applied.append((start + delta, start + delta + len(replacement)))
        delta += len(replacement) - (end - start)
    for start, end in reversed(applied):
        reconstructed = reconstructed[:start] + "Off" + reconstructed[end:]
    if reconstructed.encode("utf-8") != before:
        raise AssertionError("Unrelated bytes changed")
    return after.encode("utf-8"), rows


def original_shader(source_data: Path, identity: dict, cache: dict) -> tuple[dict, dict]:
    import UnityPy

    path = (source_data / identity["source_file"]).resolve()
    if not path.is_relative_to(source_data.resolve()):
        raise ValueError("Original path escapes source data")
    if path not in cache:
        # Default family audit spans nine large original bundles. Keep one
        # lazy UnityPy environment alive rather than retaining all of them.
        cache.clear()
        gc.collect()
        environment = UnityPy.load(str(path))
        cache[path] = (environment, sha(path.read_bytes()))
    environment, source_hash = cache[path]
    matches = [
        o
        for o in environment.objects
        if o.type.name == "Shader"
        and o.path_id == identity["source_path_id"]
        and o.assets_file.name == identity["source_serialized_file"]
    ]
    if len(matches) != 1:
        raise ValueError("Original Shader identity is missing/ambiguous: " + str(identity))
    obj = matches[0]
    version = str(obj.assets_file.unity_version)
    if not version.startswith("5.6."):
        raise ValueError("Only proven original Unity 5.6 input is admitted: " + version)
    parsed = obj.read_typetree()["m_ParsedForm"]
    if parsed["m_Name"] != "Unlit/PIPScope":
        raise ValueError("Original Shader name mismatch")
    return parsed, {
        "path": str(path),
        "sha256": source_hash,
        "unity_version": version,
        "serialized_file": obj.assets_file.name,
        "path_id": obj.path_id,
    }


def validate_output(output: Path, input_dir: Path, source_data: Path) -> None:
    output, input_dir, source_data = output.resolve(), input_dir.resolve(), source_data.resolve()
    protected = [input_dir, source_data, (ROOT / "unity-full").resolve(), (ROOT / "dist").resolve()]
    if source_data.name.lower().endswith("_data"):
        protected.append(source_data.parent)
    protected += [p.parent for p in source_data.parents if p.name.lower() == "steamapps"]
    if (
        output.exists()
        or any(output == p or output.is_relative_to(p) for p in protected)
        or any(p.endswith(".app") for p in output.parts)
    ):
        raise ValueError(
            "Output must be a new isolated directory outside input/source/staging/Steam/app trees"
        )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-data", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--inventory", type=Path, default=INVENTORY)
    parser.add_argument("--input-dir", type=Path, required=True)
    parser.add_argument(
        "--shader",
        action="append",
        help="Explicit PIP filename; repeatable. Default: all nine inventory PIP shaders",
    )
    parser.add_argument(
        "--output-dir", type=Path, help="New isolated directory; omitted means audit only"
    )
    args = parser.parse_args()
    source_data, input_dir = args.source_data.resolve(), args.input_dir.resolve()
    inventory_bytes = args.inventory.read_bytes()
    sources = json.loads(inventory_bytes)["sources"]
    selected = {
        Path(row["identity"]["exported_path"]).name: row["identity"]
        for row in sources
        if row["identity"]["name"] == "Unlit/PIPScope"
        and PIP_FILE.fullmatch(Path(row["identity"]["exported_path"]).name)
    }
    names = args.shader or sorted(selected)
    if (
        not names
        or len(names) != len(set(names))
        or any(not PIP_FILE.fullmatch(n) or n not in selected for n in names)
    ):
        raise ValueError("Unknown, duplicate or non-PIP shader selection")
    output = args.output_dir.resolve() if args.output_dir else None
    if output is not None:
        validate_output(output, input_dir, source_data)
    prepared = []
    receipt = {
        "schema": "legacy-pip-depth-clip-repair-1",
        "scope": "PIP family only; isolated candidates, no staging or runtime installation",
        "unity_default_reference": DOC,
        "inventory_sha256": sha(inventory_bytes),
        "tool_sha256": sha(Path(__file__).read_bytes()),
        "exporter_source_provenance": [],
        "shaders": [],
    }
    for relative in (
        "AssetRipper.SourceGenerated.Extensions/SerializedShaderStateExtensions.cs",
        "AssetRipper.SourceGenerated.Extensions/Enums/Shader/SerializedShader/ZClip.cs",
        "AssetRipper.Export.Modules.Shader/IO/SerializedExtensions.cs",
    ):
        path = EXPORTER / relative
        receipt["exporter_source_provenance"].append(
            {
                "path": str(path),
                "available": path.is_file(),
                "sha256": sha(path.read_bytes()) if path.is_file() else None,
            }
        )
    cache: dict = {}
    for name in names:
        path = input_dir / name
        if not path.resolve().is_relative_to(input_dir):
            raise ValueError("Input path escapes selected root")
        parsed, provenance = original_shader(source_data, selected[name], cache)
        before = path.read_bytes()
        after, passes = repair_bytes(before, parsed)
        receipt["shaders"].append(
            {
                "filename": name,
                "input": str(path),
                "input_sha256": sha(before),
                "candidate_sha256": sha(after),
                "original": provenance,
                "identity": selected[name],
                "passes": passes,
                "unrelated_bytes_preserved": True,
            }
        )
        prepared.append((name, after))
    if output is not None:
        # Every source and transformation is validated before creating output.
        output.mkdir(parents=True)
        for name, after in prepared:
            (output / name).write_bytes(after)
        (output / "depth-clip-repair.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    main()
