#!/usr/bin/env python3
"""Translate the local Unity DXBC compute exports and restore their Unity bindings."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import subprocess
import tempfile

import yaml


IDENTIFIER = re.compile(r"[A-Za-z_]\w*", re.ASCII)
UNIFORM = re.compile(r"^uniform float4 (\w+)\[(\d+)\];\n", re.M)
RESOURCE = re.compile(r"^[\w<>]+ (\w+) : register\(([stu])(\d+)\);$", re.M)
FOLDER = "ComputeShader"
RECEIPT = "compute-provenance.json"


def identifier(value):
    if not IDENTIFIER.fullmatch(value):
        raise ValueError(f"Invalid compute identifier: {value}")
    return value


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verified(path, hashes):
    if path.is_symlink():
        raise ValueError(f"Compute source is a symlink: {path}")
    contents = path.read_bytes()
    if hashlib.sha256(contents).hexdigest() != hashes.get(str(path)):
        raise ValueError(f"Compute source differs from the extraction receipt: {path}")
    return contents.decode("utf-8")


def restore_constants(source, reflection, buffers, kernel):
    bindings = {row["type"]: row["binding"] for row in reflection.get("ubos", ())}
    declarations = {}
    vectors = {}
    for cbuf in kernel["cbs"]:
        binding = int(cbuf["bindPoint"])
        if binding in vectors:
            raise ValueError(f"Duplicate constant buffer binding b{binding}")
        registers = vectors[binding] = {}
        for param in buffers[cbuf["name"]]["params"]:
            name = identifier(param["name"])
            size, offset, width = (int(param[field]) for field in ("arraySize", "offset", "colCount"))
            if param["type"] != "0" or param["rowCount"] != "1" or not 1 <= width <= 4 or offset % 16:
                raise ValueError(f"Unsupported constant layout: {name}")
            dimension = str(width) if width > 1 else ""
            array = f"[{size}]" if size else ""
            declarations[name] = f"float{dimension} {name}{array};"
            for index in range(max(size, 1)):
                value = f"{name}[{index}]" if size else name
                if width != 4:
                    value = f"float4({value}, " + ", ".join(["0.0"] * (4 - width)) + ")"
                register = offset // 16 + index
                if register in registers:
                    raise ValueError(f"Overlapping constant register {register}")
                registers[register] = value

    uniforms = {match[1]: vectors[bindings[match[1]]] for match in UNIFORM.finditer(source)}
    if set(uniforms) != set(bindings):
        raise ValueError("Unexpected SPIRV-Cross constant buffer layout")
    source = UNIFORM.sub("", source)
    for name, registers in uniforms.items():
        source = re.sub(rf"\b{name}\[(\d+)\]", lambda match: registers[int(match[1])], source)
        if re.search(rf"\b{name}\b", source):
            raise ValueError(f"Cannot map a computed constant offset into Unity properties: {name}")
    return "\n".join([*declarations.values(), "", source])


def restore_resources(source, kernel):
    textures = {}
    samplers = {}

    def bind(registers, index, name):
        if index in registers:
            raise ValueError(f"Duplicate compute binding {index}: {name}")
        registers[index] = identifier(name)

    for texture in kernel["textures"]:
        point = int(texture["bindPoint"])
        bind(textures, point & 0xffff, texture["name"])
        sampler = point >> 16
        if sampler >= 0:
            bind(samplers, sampler, "sampler" + texture["name"])
    for entry in kernel["builtinSamplers"]:
        if entry["sampler"] != "1":
            raise ValueError(f"Unknown Unity inline sampler mode: {entry['sampler']}")
        bind(samplers, int(entry["bindPoint"]), "sampler_LinearClamp")
    for entry in kernel["inBuffers"]:
        bind(textures, int(entry["bindPoint"]), entry["name"])
    outputs = {}
    for entry in kernel["outBuffers"]:
        bind(outputs, int(entry["bindPoint"]), entry["name"])
    registers = {"s": samplers, "t": textures, "u": outputs}
    replacements = {}
    for match in RESOURCE.finditer(source):
        replacements[match[1]] = registers[match[2]].pop(int(match[3]))
    if any(registers.values()):
        raise ValueError(f"Unity compute bindings missing in translated source: {registers}")
    return re.sub(r"\b\w+\b", lambda match: replacements.get(match[0], match[0]), source)


def convert_kernel(kernel, buffers, compiler, cross, work):
    name = identifier(kernel["name"])
    data = bytes.fromhex(kernel["code"])
    if data[:4] != b"DXBC":
        raise ValueError(f"Expected a paired Direct3D compute program: {name}")
    sizes = list(struct.unpack("<III", bytes.fromhex(kernel["threadGroupSize"])))
    dxbc, spv = work / "kernel.dxbc", work / "kernel.spv"
    dxbc.write_bytes(data)
    subprocess.run([compiler, dxbc, "--output", spv, "--validate", "--ssbo-uav", "--ssbo-srv"], check=True)
    reflection = json.loads(subprocess.check_output([cross, spv, "--reflect"], text=True))
    entry, = reflection["entryPoints"]
    if entry["mode"] != "comp" or entry["workgroup_size"] != sizes:
        raise ValueError(f"SPIR-V workgroup differs from the installed Unity program: {name}")
    source = subprocess.check_output([cross, spv, "--hlsl", "--shader-model", "50", "--flatten-ubo",
                                      "--rename-entry-point", entry["name"], name, "comp"], text=True)
    source = restore_constants(source, reflection, buffers, kernel)
    source = restore_resources(source, kernel)
    return source, {"name": name, "threads": sizes, "dxbcSha256": digest(dxbc), "spirvSha256": digest(spv)}


def convert_asset(source, hashes, destination, compiler, cross, work):
    meta = source.with_suffix(".asset.meta")
    serialized, metadata = verified(source, hashes), verified(meta, hashes)
    header, body = serialized.split("\nComputeShader:\n", 1)
    if not re.search(r"(?m)^--- !u!72 &7200000$", header):
        raise ValueError(f"Expected a serialized Unity ComputeShader: {source}")
    variant, = yaml.load("ComputeShader:\n" + body, Loader=yaml.BaseLoader)["ComputeShader"]["variants"]
    if variant["targetRenderer"] != "2":
        raise ValueError(f"Expected Direct3D as the single source platform: {source}")
    buffers = {row["name"]: row for row in variant["constantBuffers"]}
    pragmas, programs, kernels = [], [], []
    for index, kernel in enumerate(variant["kernels"]):
        code, reflected = convert_kernel(kernel, buffers, compiler, cross, work)
        guard = f"NATIVEH3VR_COMPUTE_{index}"
        pragmas.append(f"#pragma kernel {reflected['name']} {guard}")
        programs.append(f"#if defined({guard})\n{code}#endif")
        kernels.append(reflected)
    if not kernels or len({row["name"] for row in kernels}) != len(kernels):
        raise ValueError(f"Invalid or duplicate kernels: {source}")

    compute = destination / source.with_suffix(".compute").name
    compute.write_text("\n".join([*pragmas, "", *programs, ""]))
    output_meta = compute.with_suffix(".compute.meta")
    guid = re.search(r"(?m)^guid: ([0-9a-f]{32})$", metadata)[1]
    import_settings = re.findall(r"(?m)^  (?:userData|assetBundleName|assetBundleVariant):[^\r\n]*$", metadata)
    output_meta.write_text("\n".join(["fileFormatVersion: 2", f"guid: {guid}", "ComputeShaderImporter:",
                                     "  externalObjects: {}", *import_settings, ""]))
    return {
        "source": source.name, "sourceSha256": hashes[str(source)], "sourceMetaSha256": hashes[str(meta)],
        "compute": compute.name, "computeSha256": digest(compute), "computeMetaSha256": digest(output_meta),
        "kernels": kernels,
    }


def stage(workspace, overlay, compiler, cross):
    extraction = json.loads((workspace / ".conversion/extract.json").read_text())
    if extraction["Step"] != "extract":
        raise ValueError("The private workspace needs a completed extraction receipt")
    source = workspace / "recovery/ExportedProject/Assets" / FOLDER
    assets = overlay / "Assets"
    target = assets / FOLDER
    if not assets.is_dir() or assets.is_symlink() or target.exists() or target.is_symlink():
        raise ValueError("Choose an existing private shader overlay with an Assets directory and no compute output")
    if overlay.is_relative_to(workspace) or workspace.is_relative_to(overlay):
        raise ValueError("Keep the shader overlay separate from the extraction workspace")
    inputs = sorted(source.glob("*.asset"))
    if not inputs:
        raise ValueError("The extraction has no serialized compute shaders")
    before = {"dxbcCompiler": digest(compiler), "spirvCross": digest(cross)}
    with tempfile.TemporaryDirectory(prefix=".compute-recovery-", dir=overlay) as scratch:
        scratch = Path(scratch)
        candidate = scratch / FOLDER
        candidate.mkdir()
        rows = [convert_asset(path, extraction["Files"], candidate, compiler, cross, scratch) for path in inputs]
        if before != {"dxbcCompiler": digest(compiler), "spirvCross": digest(cross)}:
            raise ValueError("The translation tools changed during recovery")
        receipt = {
            "schemaVersion": 1, "extractionFingerprint": extraction["Fingerprint"],
            "toolSha256": before, "files": rows,
        }
        (candidate / RECEIPT).write_text(json.dumps(receipt, indent=2) + "\n")
        candidate.rename(target)
    print(f"{len(rows)} private compute shaders, {sum(len(row['kernels']) for row in rows)} kernels")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", required=True, type=Path)
    parser.add_argument("--overlay", required=True, type=Path)
    parser.add_argument("--dxbc-compiler", required=True, type=Path, help="Built dxil-spirv executable")
    parser.add_argument("--spirv-cross", required=True, type=Path)
    args = parser.parse_args()
    try:
        stage(*(arg.resolve(strict=True) for arg in (args.workspace, args.overlay, args.dxbc_compiler, args.spirv_cross)))
    except (ValueError, OSError, KeyError, TypeError, subprocess.CalledProcessError, yaml.YAMLError) as error:
        parser.exit(1, f"Compute recovery: {error}\n")


if __name__ == "__main__":
    main()
