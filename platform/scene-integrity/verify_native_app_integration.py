#!/usr/bin/env python3
"""Audit a disposable binary-only copy; never modify the existing playable app."""

import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "script"))
from audit_native_app import (
    audit,
    check_saved_audit,
    localize_packaged_dependencies,
    sign_packaged_binaries,
)

source = ROOT / "dist/NewEngineH3VR.app"
existing = audit(source)
checks = []
with tempfile.TemporaryDirectory(prefix="native-app-audit-") as directory:
    app = Path(directory) / "Fixture.app"
    for relative in ["Contents/Info.plist"] + [r["path"] for r in existing["binaries"]]:
        destination = app / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source / relative, destination)
    changes = localize_packaged_dependencies(app)
    sign_packaged_binaries(app)
    subprocess.run(["/usr/bin/codesign", "--force", "--deep", "--sign", "-", str(app)], check=True)
    accepted = audit(app)
    if not accepted["passed"]:
        raise RuntimeError(accepted["errors"])
    report = Path(directory) / "audit.json"
    report.write_text(json.dumps(accepted))
    check_saved_audit(app, report)
    checks.append(
        "All 16 actual native binaries + 121 managed assemblies pass after app-copy dependency normalization"
    )
    # An unmanaged Windows PE must not be mistaken for a CLR assembly by suffix.
    pe = bytearray(64 + 24 + 224)
    pe[:2] = b"MZ"
    struct.pack_into("<I", pe, 0x3C, 64)
    pe[64:68] = b"PE\0\0"
    struct.pack_into("<H", pe, 64 + 20, 224)
    struct.pack_into("<H", pe, 88, 0x10B)
    native_pe = app / "Contents/Resources/forbidden.dll"
    native_pe.parent.mkdir(exist_ok=True)
    native_pe.write_bytes(pe)
    wrong_arch = app / "Contents/Resources/intel.dylib"
    wrong_arch.write_bytes(struct.pack("<IiiIIIII", 0xFEEDFACF, 0x1000007, 3, 6, 0, 0, 0, 0))
    rejected = audit(app)
    if not any("Native/mixed Windows executable" in e for e in rejected["errors"]):
        raise RuntimeError("Native PE fixture was incorrectly accepted")
    if not any("ARM64-only" in e for e in rejected["errors"]):
        raise RuntimeError("Intel Mach-O fixture was incorrectly accepted")
    checks.extend(["Unmanaged PE .dll rejected", "Intel Mach-O rejected"])
    try:
        check_saved_audit(app, report)
    except ValueError:
        checks.append("Added/tampered binaries invalidate saved hash-bound acceptance")
    else:
        raise RuntimeError("Modified app incorrectly matched saved inventory")
    native_pe.unlink()
    wrong_arch.unlink()
    # Use a real Mach-O with an external absolute dependency, then re-sign to
    # isolate dependency rejection from signature rejection.
    launcher = app / "Contents/MacOS/NewEngineH3VR"
    subprocess.run(
        [
            "/usr/bin/install_name_tool",
            "-change",
            "/usr/lib/libSystem.B.dylib",
            "/tmp/not-in-app/libSystem.B.dylib",
            str(launcher),
        ],
        check=True,
    )
    sign_packaged_binaries(app)
    subprocess.run(["/usr/bin/codesign", "--force", "--deep", "--sign", "-", str(app)], check=True)
    rejected = audit(app)
    if not any(
        "external non-system dylib dependency: /tmp/not-in-app/" in e for e in rejected["errors"]
    ):
        raise RuntimeError("External absolute dependency was incorrectly accepted")
    checks.append("Signed native binary with external absolute dylib rejected")
result = {
    "passed": True,
    "checks": checks,
    "dependencyChanges": changes,
    "nativeCount": accepted["nativeCount"],
    "managedCount": accepted["managedCount"],
    "fixtureInventorySha256": accepted["inventorySha256"],
    "scope": "Disposable 116MB binary-only app fixture; original generated application remained unchanged",
}
(ROOT / "build/native-app-audit-tests.json").write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps(result, indent=2))
