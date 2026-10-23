#!/usr/bin/env python3
"""Read-only architecture, dependency and signature audit of a generated app."""

from pathlib import Path
import argparse
import hashlib
import json
import plistlib
import posixpath
import struct
import subprocess
from native_plugin_contract import check_plugin_copies

MACHO = {
    bytes.fromhex(x)
    for x in (
        "feedface",
        "cefaedfe",
        "feedfacf",
        "cffaedfe",
        "cafebabe",
        "bebafeca",
        "cafebabf",
        "bfbafeca",
    )
}
LOAD_DYLIB = {0xC, 0x80000018, 0x8000001F, 0x20, 0x80000023}


def sha256(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(data)
    return value.hexdigest()


def record(path, app):
    return {
        "path": path.relative_to(app).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def shader_overlay_bindings(app):
    assets = app / "Contents/Resources/Data/StreamingAssets"
    marker = assets / "new-engine-shader-state-overlay.json"
    if marker.is_symlink():
        raise ValueError("Shader overlay marker is a symlink")
    if not marker.is_file():
        return []
    overlay = json.loads(marker.read_text())
    bundle_name = "assets_resources_objectids_weaponry_ar15"
    if (
        overlay.get("schemaVersion") != 1
        or overlay.get("kind") != "newengine-unity2021-scope-depth-clip"
        or overlay.get("bundle") != bundle_name
    ):
        raise ValueError("Unexpected shader overlay identity")
    target = assets / bundle_name
    if marker.is_symlink() or target.is_symlink():
        raise ValueError("Shader overlay marker/target is a symlink")
    observed = record(target, app)
    expected = overlay.get("after", {})
    if (
        expected.get("path") != bundle_name
        or expected.get("bytes") != observed["bytes"]
        or expected.get("sha256") != observed["sha256"]
    ):
        raise ValueError("Corrected shader bundle differs from overlay proof")
    baseline = overlay.get("baseContentProof", {})
    if baseline.get("path") != "native-bundle-content-verification.json" or baseline.get(
        "sha256"
    ) != sha256(assets / baseline["path"]):
        raise ValueError("Frozen content baseline differs from overlay proof")
    content = json.loads((assets / baseline["path"]).read_text())
    originals = [r for r in content.get("outputs", []) if r.get("path") == bundle_name]
    if (
        content.get("schemaVersion") != 2
        or content.get("verified") is not True
        or not str(content.get("unityVersion", "")).startswith("2021.")
        or len(originals) != 1
        or overlay.get("before") != originals[0]
    ):
        raise ValueError("Overlay input is not the unique verified Unity bundle baseline")
    proof = overlay.get("proof", {})
    proof_bytes = (json.dumps(proof, indent=2) + "\n").encode("utf-8")
    if (
        overlay.get("proofEncoding") != "json-indent-2-utf8-lf"
        or overlay.get("proofSha256") != hashlib.sha256(proof_bytes).hexdigest()
    ):
        raise ValueError("Embedded shader proof digest disagrees")
    if (
        proof.get("schemaVersion") != 1
        or proof.get("kind") != "newengine-pip-zclip-state-only"
        or proof.get("passed") is not True
        or proof.get("input", {}).get("sha256") != originals[0]["sha256"]
        or proof.get("candidate", {}).get("sha256") != observed["sha256"]
    ):
        raise ValueError("Embedded shader proof is failed or binds different input/output bytes")
    evidence = proof.get("verification", {})
    fields = [
        {
            "path": "m_ParsedForm.m_SubShaders[0].m_Passes[" + str(i) + "].m_State.zClip.val",
            "before": 0.0,
            "after": 1.0,
        }
        for i in range(2)
    ]
    shader = evidence.get("shader", {})
    blocks = evidence.get("blockVerification", {})
    if (
        evidence.get("fieldDifferences") != fields
        or shader.get("name") != "Unlit/PIPScope"
        or shader.get("file") != "CAB-d98205cd3e60321f23684452d96b0f22"
        or shader.get("pathId") != 5248756200144351081
        or shader.get("compiledProgramBytesIdentical") is not True
        or blocks.get("completeDecompressedStreamDiffersOnlyAtShaderStateBytes") is not True
        or blocks.get("directoryAndResourceBytesIdentical") is not True
        or type(evidence.get("objectCount")) is not int
        or evidence["objectCount"] < 1
        or evidence.get("unchangedObjectCount") != evidence["objectCount"] - 1
    ):
        raise ValueError("Shader proof does not establish the exact admitted state-only correction")
    return [record(marker, app), observed]


def pe_kind(path):
    """Require a valid CLR header and ILONLY, not merely a .dll filename."""
    with path.open("rb") as stream:
        header = stream.read(64)
        pe_offset = struct.unpack_from("<I", header, 0x3C)[0]
        stream.seek(pe_offset)
        header = stream.read(24)
        if header[:4] != b"PE\0\0":
            return "invalid-PE"
        sections, optional_size = (
            struct.unpack_from("<H", header, 6)[0],
            struct.unpack_from("<H", header, 20)[0],
        )
        optional = stream.read(optional_size)
        magic = struct.unpack_from("<H", optional)[0]
        directory = {0x10B: 96, 0x20B: 112}.get(magic)
        if (
            directory is None
            or len(optional) < directory + 15 * 8
            or struct.unpack_from("<I", optional, directory - 4)[0] < 15
        ):
            return "native-Windows-PE"
        cli_rva, cli_size = struct.unpack_from("<II", optional, directory + 14 * 8)
        if not cli_rva or cli_size < 20:
            return "native-Windows-PE"
        section_headers = stream.read(40 * sections)
        for index in range(sections):
            virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from(
                "<IIII", section_headers, index * 40 + 8
            )
            if virtual_address <= cli_rva < virtual_address + max(virtual_size, raw_size):
                delta = cli_rva - virtual_address
                if delta + 20 > raw_size:
                    return "invalid-CLR-header"
                stream.seek(raw_offset + delta)
                cli_header = stream.read(20)
                flags = struct.unpack_from("<I", cli_header, 16)[0]
                return "managed-IL" if flags & 1 and not flags & 0x10 else "mixed/native-Windows-PE"
        return "invalid-CLR-header"


def macho_commands(path):
    with path.open("rb") as stream:
        header = stream.read(32)
        if header[:4] != bytes.fromhex("cffaedfe"):
            raise ValueError("Expected a thin little-endian 64-bit Mach-O after architecture check")
        count, size = struct.unpack_from("<II", header, 16)
        if size > 16 * 1024 * 1024:
            raise ValueError("Unreasonable Mach-O load-command length")
        commands = stream.read(size)
    dependencies, rpaths = [], []
    offset = 0
    for _ in range(count):
        command, length = struct.unpack_from("<II", commands, offset)
        if length < 8 or offset + length > len(commands):
            raise ValueError("Invalid Mach-O load command")
        if command in LOAD_DYLIB or command == 0x8000001C:
            string_offset = struct.unpack_from("<I", commands, offset + 8)[0]
            if not 12 <= string_offset < length:
                raise ValueError("Invalid Mach-O dependency string")
            name = commands[offset + string_offset : offset + length].split(b"\0")[0].decode()
            (rpaths if command == 0x8000001C else dependencies).append(name)
        offset += length
    return dependencies, rpaths


def system_path(path):
    # These may live only in the dyld shared cache and need not exist on disk.
    path = posixpath.normpath(path)
    return path.startswith("/usr/lib/") or path.startswith("/System/Library/")


def expand(path, loader, executable):
    if path.startswith("@loader_path/"):
        return loader.parent / path[len("@loader_path/") :]
    if path.startswith("@executable_path/"):
        return executable.parent / path[len("@executable_path/") :]
    return Path(path) if path.startswith("/") else None


def dependency_target(name, loader, executable, rpaths, app):
    if system_path(name):
        return "system"
    if name.startswith("@rpath/"):
        tail = name[len("@rpath/") :]
        candidates = [
            base / tail
            for value, owner in rpaths
            for base in [expand(value, owner, executable)]
            if base is not None
        ]
    else:
        value = expand(name, loader, executable)
        candidates = [] if value is None else [value]
    for candidate in candidates:
        canonical = candidate.resolve()
        if system_path(str(canonical)):
            return "system"
        if canonical.is_relative_to(app) and canonical.is_file():
            return canonical.relative_to(app).as_posix()
    raise ValueError(f"Unresolved or external non-system dylib dependency: {name}")


def audit(app):
    app = app.resolve()
    errors, binaries, parsed = [], [], {}
    with (app / "Contents/Info.plist").open("rb") as stream:
        info = plistlib.load(stream)
    executable = app / "Contents/MacOS" / info["CFBundleExecutable"]
    for path in sorted(app.rglob("*")):
        if not path.is_file():
            continue
        with path.open("rb") as stream:
            magic = stream.read(4)
        if magic not in MACHO and magic[:2] != b"MZ":
            continue
        item = record(path, app)
        binaries.append(item)
        try:
            if not path.resolve().is_relative_to(app):
                raise ValueError("Binary symlink escapes application")
            if magic[:2] == b"MZ":
                item["kind"] = pe_kind(path)
                if item["kind"] != "managed-IL":
                    raise ValueError(
                        "Native/mixed Windows executable code shipped in app: " + item["kind"]
                    )
                continue
            item["kind"] = "Mach-O"
            item["architectures"] = (
                subprocess.check_output(["/usr/bin/lipo", "-archs", str(path)], text=True)
                .strip()
                .split()
            )
            if item["architectures"] != ["arm64"]:
                raise ValueError(
                    "Shipped Mach-O must be ARM64-only: " + repr(item["architectures"])
                )
            dependencies, rpaths = macho_commands(path)
            parsed[path] = (item, dependencies, rpaths)
            item["rpaths"] = rpaths
            signature = subprocess.run(
                ["/usr/bin/codesign", "--verify", "--strict", str(path)],
                capture_output=True,
                text=True,
            )
            item["signatureVerified"] = signature.returncode == 0
            if signature.returncode:
                raise ValueError("Binary signature failed: " + signature.stderr.strip())
        except (ValueError, OSError, struct.error, subprocess.CalledProcessError) as error:
            errors.append(f"{item['path']}: {error}")
    if executable not in parsed:
        errors.append("Application entry point is missing or is not a valid ARM64 Mach-O")
    native_paths = {path.resolve() for path in parsed}
    main_rpaths = [
        (value, path)
        for path, (_, _, paths) in parsed.items()
        if path.parent == executable.parent
        for value in paths
    ]
    # A plugin can inherit runpaths from its loading image. Only app-contained
    # runpaths are considered; system/external lookup cannot hide a missing dylib.
    for path, (item, dependencies, rpaths) in parsed.items():
        item["dependencies"] = []
        for dependency in dependencies:
            try:
                target = dependency_target(
                    dependency,
                    path,
                    executable,
                    [(value, path) for value in rpaths] + main_rpaths,
                    app,
                )
                if target != "system" and (app / target).resolve() not in native_paths:
                    raise ValueError("Linked app dependency is not a validated Mach-O: " + target)
                item["dependencies"].append({"name": dependency, "resolved": target})
            except ValueError as error:
                errors.append(f"{item['path']}: {error}")
    signature = subprocess.run(
        ["/usr/bin/codesign", "--verify", "--deep", "--strict", str(app)],
        capture_output=True,
        text=True,
    )
    if signature.returncode:
        errors.append("Application signature failed: " + signature.stderr.strip())
    if not any(item.get("kind") == "Mach-O" for item in binaries):
        errors.append("No native Mach-O binaries found")
    bindings = [record(app / "Contents/Info.plist", app)]
    for path in (
        app / "Contents/_CodeSignature/CodeResources",
        app / "Contents/Resources/Data/OptiH3VR/new-engine-install.json",
        app / "Contents/Resources/Data/OptiH3VR/native-install.json",
        app / "Contents/Resources/Data/StreamingAssets/native-bundle-content-verification.json",
    ):
        if path.is_file():
            bindings.append(record(path, app))
    try:
        bindings.extend(shader_overlay_bindings(app))
    except (ValueError, OSError, KeyError, TypeError, AttributeError) as error:
        errors.append("Shader state overlay: " + str(error))
    bridge_checks = []
    try:
        bridge_checks = check_plugin_copies(app)
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        errors.append("Native plugin contract: " + str(error))
    inventory = {"binaries": binaries, "bindings": bindings, "nativeBridgeContracts": bridge_checks}
    inventory_hash = hashlib.sha256(
        json.dumps(inventory, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()
    return {
        "schemaVersion": 1,
        "app": str(app),
        "passed": not errors,
        "errors": errors,
        "inventorySha256": inventory_hash,
        "applicationSignatureVerified": signature.returncode == 0,
        "nativeCount": sum(i.get("kind") == "Mach-O" for i in binaries),
        "managedCount": sum(i.get("kind") == "managed-IL" for i in binaries),
        **inventory,
        "limits": [
            "Static linked dependencies, shipped architecture/signatures, duplicate plugin identity and public XR/tracking exports; other PInvoke/dlopen symbols require separate closure or runtime tests.",
            "Binary hashes bind this inventory; data assets are covered by the separate bundle/payload proofs.",
        ],
    }


def localize_packaged_dependencies(app):
    """Only mutate copies inside an app being packaged, never source libraries."""
    app = app.resolve()
    changes = []
    for path in sorted(app.rglob("*")):
        if not path.is_file():
            continue
        with path.open("rb") as stream:
            magic = stream.read(4)
        if magic not in MACHO:
            continue
        if not path.resolve().is_relative_to(app):
            raise ValueError("Binary symlink escapes app: " + str(path))
        dependencies, _ = macho_commands(path)
        for dependency in dependencies:
            if not dependency.startswith("@rpath/"):
                continue
            tail = dependency[len("@rpath/") :]
            local = (path.parent / tail).resolve()
            if local.is_relative_to(app) and local.is_file():
                replacement = "@loader_path/" + tail
                subprocess.run(
                    ["/usr/bin/install_name_tool", "-change", dependency, replacement, str(path)],
                    check=True,
                )
                changes.append(
                    {
                        "path": path.relative_to(app).as_posix(),
                        "from": dependency,
                        "to": replacement,
                    }
                )
    return changes


def sign_packaged_binaries(app):
    app = app.resolve()
    # --deep does not reliably discover code in arbitrary StreamingAssets paths.
    # Sign each actual Mach-O copy before sealing the containing application.
    for path in sorted(app.rglob("*")):
        if not path.is_file():
            continue
        with path.open("rb") as stream:
            magic = stream.read(4)
        if magic in MACHO:
            if not path.resolve().is_relative_to(app):
                raise ValueError("Binary symlink escapes app: " + str(path))
            subprocess.run(["/usr/bin/codesign", "--force", "--sign", "-", str(path)], check=True)


def player_binary(app):
    """Resolve the current public executable or a preserved pre-rename test app."""
    paths = [app / "Contents/MacOS" / name for name in ("NewEngineH3VRPlayer", "NativeH3VRPlayer")]
    found = [path for path in paths if path.is_file()]
    if len(found) != 1:
        raise ValueError("Expected exactly one generated Unity player executable")
    return found[0]


def check_saved_audit(app, report):
    """Bind run evidence to the exact signed binary inventory accepted at packaging."""
    accepted = json.loads(report.read_text())
    if accepted.get("passed") is not True or Path(accepted["app"]).resolve() != app.resolve():
        raise ValueError("Missing/failed packaged-app audit or application mismatch")
    current = audit(app)
    if not current["passed"] or current["inventorySha256"] != accepted["inventorySha256"]:
        raise ValueError("Packaged binary inventory/signatures changed: " + repr(current["errors"]))
    return {
        "report": str(report),
        "reportSha256": sha256(report),
        "inventorySha256": accepted["inventorySha256"],
        "nativeCount": accepted["nativeCount"],
        "managedCount": accepted["managedCount"],
    }


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    parser.add_argument("--report", type=Path, default=root / "build/native-app-audit.json")
    args = parser.parse_args()
    result = audit(args.app)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2) + "\n")
    print(
        json.dumps(
            {
                k: result[k]
                for k in ("passed", "nativeCount", "managedCount", "inventorySha256", "errors")
            },
            indent=2,
        )
    )
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
