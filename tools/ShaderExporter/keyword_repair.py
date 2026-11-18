"""Recover mutually exclusive Unity pass keywords from serialized Direct3D variants."""

from collections import Counter
import re

import lz4.block
import UnityPy
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.streams import EndianBinaryReader


DOMAINS = {
    "FORWARD": ("DIRECTIONAL",),
    "FORWARD_DELTA": ("DIRECTIONAL", "DIRECTIONAL_COOKIE", "POINT", "POINT_COOKIE", "SPOT"),
    "SHADOWCASTER": ("SHADOWS_CUBE", "SHADOWS_DEPTH"),
}
PASS_NAME = re.compile(r'(?m)^[\t ]*Name[\t ]+"([^"]+)"[\t ]*$')


def load_objects(path):
    return {asset.path_id: asset for asset in UnityPy.load(str(path)).objects}


def read_programs(asset):
    serialized = asset.read_typetree()
    parsed = serialized.get("m_ParsedForm")
    if not parsed or 4 not in serialized["platforms"]:
        return {}, []
    index = serialized["platforms"].index(4)
    offset, length = serialized["offsets"][index], serialized["compressedLengths"][index]
    compressed = bytes(serialized["compressedBlob"][offset:offset + length])
    decoded = lz4.block.decompress(compressed, uncompressed_size=serialized["decompressedLengths"][index])
    reader = EndianBinaryReader(decoded, endian="<")
    return parsed, ShaderProgram(reader, asset.version).m_SubPrograms


def keyword_sets(pass_info, programs, field, stage):
    keywords = set()
    for row in pass_info.get(field, {}).get("m_SubPrograms", []):
        program = programs[row["m_BlobIndex"]]
        kind = program.m_ProgramType.name.upper()
        if "DX11" in kind and stage in kind:
            keywords.add(frozenset(program.m_Keywords))
    return keywords


def repair(source, asset):
    locations = list(PASS_NAME.finditer(source))
    counts = Counter(match[1] for match in locations)
    if not counts.keys() & DOMAINS.keys():
        return source, []

    serialized, programs = read_programs(asset)
    passes = {}
    for subshader in serialized.get("m_SubShaders", []):
        for entry in subshader["m_Passes"]:
            passes.setdefault(entry.get("m_State", {}).get("m_Name"), []).append(entry)

    limits = [match.start() for match in locations[1:]] + [len(source)]
    changes = []
    for match, limit in zip(locations, limits):
        name = match[1]
        if name not in DOMAINS or counts[name] != 1 or len(passes.get(name, [])) != 1:
            continue
        original, = passes[name]
        vertex = keyword_sets(original, programs, "progVertex", "VERTEX")
        fragment = keyword_sets(original, programs, "progFragment", "PIXEL")
        group = DOMAINS[name]
        members = frozenset(group)
        if not vertex or not fragment or any(len(variant & members) != 1 for variant in vertex | fragment):
            continue
        program = source.find("CGPROGRAM", match.end(), limit)
        if program < 0:
            continue
        target = source.find("#pragma target", program, limit)
        if target < 0:
            continue
        first = source.find("\n", target, limit) + 1
        last = source.find("#if", first, limit)
        if first <= target or last < 0:
            continue

        declarations = source[first:last]
        original_pragmas = [f"#pragma shader_feature __ {key}\n" for key in group]
        if any(declarations.count(pragma) != 1 for pragma in original_pragmas):
            continue
        for pragma in original_pragmas:
            declarations = declarations.replace(pragma, "")
        declaration = f"#define {group[0]} 1\n" if len(group) == 1 else "#pragma multi_compile " + " ".join(group) + "\n"
        changes.append((first, last, declaration + declarations, {"name": name, "required": group}))

    for first, last, declarations, _ in reversed(changes):
        source = source[:first] + declarations + source[last:]
    return source, [row for _, _, _, row in changes]
