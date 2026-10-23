#!/usr/bin/env python3
"""Build shared OptiH3VR patches and install an audited app-only overlay.

CPU algorithms and their engine adapters are owned by the sibling OptiH3VR mod.
Unity's accepted content build is retained, with a separate runtime ABI proof.
"""

from pathlib import Path
import argparse
from datetime import datetime, timezone
import json
import os
import shutil
import subprocess
import uuid
from audit_native_app import audit, check_saved_audit, sha256

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "dist/NewEngineH3VR.app"
GAME = "NativeH3VR.Game.dll"
HELPER = "OptiH3VR.NewEngine.dll"
MANIFEST = "new-engine-install.json"
# Read-only recognition/removal of the earlier integration naming on upgrade.
LEGACY_HELPER = "OptiH3VR.Native.dll"
LEGACY_MANIFEST = "native-install.json"
STATE = ROOT / "build/optih3vr/installed.json"
AUDIT = ROOT / "build/native-app-audit.json"
ENGINE_TARGET = "System.Boolean UnityEngine.Rendering.PostProcessing.RuntimeUtilities::get_isSinglePassStereoEnabled()"
ENGINE_HELPER_METHOD = "System.Boolean NativeH3VR.NewEnginePostProcessing::get_IsPackedStereo()"
ENGINE_SOURCES = frozenset(
    (
        "native/managed/NativeH3VRXR.cs",
        "native/managed/NativePostProcessStereo.cs",
        "native/patch/StereoPackingPatch.cs",
        "native/patch/Program.cs",
        "native/stereo-packing-patch/Program.cs",
        "native/stereo-packing-patch/StereoPackingPatch.csproj",
        "native/build_managed.py",
        "native/prepare_stereo_packing.py",
        "script/compose_managed.py",
    )
)
ENGINE_PAIR_KIND = "newengine-packed-stereo-scope-pair"
ENGINE_PAIR_SOURCES = frozenset(
    (
        "native/managed/NativeH3VRXR.cs",
        "native/managed/NativePostProcessStereo.cs",
        "native/managed/NativeScopeActivation.cs",
        "native/patch/StereoPackingPatch.cs",
        "native/patch/ScopePairPatch.cs",
        "native/patch/Program.cs",
        "native/scope-pair-patch/Program.cs",
        "native/scope-pair-patch/ScopePairPatch.csproj",
        "native/build_managed.py",
        "script/compose_managed.py",
        "native/prepare_scope_pair.py",
    )
)
ENGINE_SCOPE_TARGET = (
    "System.Void PIPScope::UpdateAllScopes(PIPScope_Camera,PIPScope,System.Int32,System.Int32)"
)
ENGINE_XR_ITERATOR = (
    "System.Boolean NativeH3VR.NativeStereoRenderer/<Start>c__Iterator0::MoveNext()"
)
ENGINE_PAIR_CALLSITES = [
    dict(
        instruction_index=index,
        old_opcode="callvirt",
        old_method="System.Void UnityEngine.Behaviour::set_enabled(System.Boolean)",
        new_opcode="call",
        new_method="System.Void NativeH3VR.NewEngineScopeActivation::SetEnabled(UnityEngine.Behaviour,System.Boolean)",
    )
    for index in (331, 374, 580)
]
ENGINE_PAIR_REQUIRED = dict(
    changed_game_methods=[ENGINE_TARGET, ENGINE_SCOPE_TARGET],
    scope_setter_callsites=ENGINE_PAIR_CALLSITES,
    changed_existing_xr_methods=[ENGINE_XR_ITERATOR],
    scope_setter_original_body_sha256="34456ecd9b17649db695894e936bb76309e8941863d929719c84f904e52f8d25",
    xr_changed_body_fingerprints=[
        dict(
            method=ENGINE_XR_ITERATOR,
            before_sha256="bfebe9be4c1e6b1f8c0cf8987428e8f35b2e0bafa3163811342a45efaec43dee",
            after_sha256="b1d6360f92735f26d59e51b6f819fac595c065fe3ed9bb38e6b281666189db99",
        )
    ],
    xr_added_types=["NativeH3VR.NewEnginePostProcessing", "NativeH3VR.NewEngineScopeActivation"],
    xr_added_methods=["System.Boolean NativeH3VR.NativeEyeProjection::get_IsRenderingEye()"],
    xr_added_properties=["NativeH3VR.NativeEyeProjection.IsRenderingEye"],
    xr_iterator_instruction_delta=4,
    xr_iterator_finally_delta=1,
    xr_existing_method_bodies_preserved=32,
)
XR = "NativeH3VR.XR.Managed.dll"
ENGINE_XR_ACCEPTANCE = "build/optih3vr/live-scene-telemetry-extensions"


def write_json(path, data):
    temporary = path.with_name("." + path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        temporary.write_text(json.dumps(data, indent=2) + "\n")
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def replace_file(source, target):
    """Publish a complete file; an interrupted copy cannot truncate the target."""
    temporary = target.with_name("." + target.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        shutil.copy2(source, temporary)
        temporary.replace(target)
    finally:
        temporary.unlink(missing_ok=True)


def verify_preparation(proof, source, inputs, candidates):
    if (
        proof.get("schema_version") != 1
        or proof.get("runtime_detours") is not False
        or proof.get("experimental_ai_nonalloc") is not False
        or not str(proof.get("structural_verification", "")).startswith("passed:")
        or proof["input_sha256"] != sha256(inputs / GAME)
        or proof["output_sha256"] != sha256(candidates / GAME)
        or proof["helper_sha256"] != sha256(candidates / HELPER)
        or proof["patch_count"] != len(proof["patches"])
        or proof["patch_count"] <= 0
    ):
        raise ValueError("OptiH3VR preparation proof/hashes/patch list disagree")
    if not proof.get("sources") or not proof.get("mod_version"):
        raise ValueError("OptiH3VR source/version provenance is missing")
    for relative, expected in proof["sources"].items():
        path = (source / relative).resolve()
        if not path.is_relative_to(source) or not path.is_file() or sha256(path) != expected:
            raise ValueError("OptiH3VR source changed since preparation: " + relative)


def verify_engine_baseline(proof_path, existing, baseline, managed):
    """Accept only either exact declared stereo repair of the recorded baseline."""
    proof_path = Path(proof_path)
    build = (ROOT / "build").resolve()
    if proof_path.is_symlink() or not proof_path.resolve().is_relative_to(build):
        raise ValueError("Engine baseline proof must be a regular build artifact")
    proof_path = proof_path.resolve()
    proof = json.loads(proof_path.read_text())
    kind = proof.get("kind")
    if kind not in ("newengine-packed-stereo-getter-only", ENGINE_PAIR_KIND):
        raise ValueError("Engine baseline proof has an unsupported repair kind")
    required = dict(
        schema_version=1,
        kind=kind,
        engine_define="NEWENGINE_UNITY_2021",
        target=ENGINE_TARGET,
        helper_method=ENGINE_HELPER_METHOD,
        expected_old_opcodes=["ldc.i4.1", "ret"],
        expected_new_opcodes=["call", "ret"],
        all_existing_types_fields_properties_events_and_method_metadata_preserved=True,
        all_other_method_bodies_preserved=True,
        xr_all_existing_type_and_field_layouts_preserved=True,
    )
    if kind == ENGINE_PAIR_KIND:
        required.update(ENGINE_PAIR_REQUIRED)
    for key, expected in required.items():
        if proof.get(key) != expected or (
            type(expected) in (bool, int) and type(proof.get(key)) is not type(expected)
        ):
            raise ValueError("Engine baseline proof has an unsupported repair/verification: " + key)
    if proof.get("verification_mode") not in ("prepared-candidate", "existing-candidate"):
        raise ValueError("Engine baseline proof has no structural verification mode")
    for key in ("checks", "untouched_method_bodies", "xr_existing_method_bodies_preserved"):
        if type(proof.get(key)) is not int or proof[key] <= 0:
            raise ValueError("Engine baseline proof lacks positive verification counts: " + key)
    if proof["checks"] <= proof["untouched_method_bodies"]:
        raise ValueError("Engine baseline verification counts disagree")
    paths = {}
    for field in ("input", "output", "helper", "baseline_xr"):
        path = Path(proof[field])
        if (
            not path.is_absolute()
            or path.is_symlink()
            or not path.is_file()
            or not path.resolve().is_relative_to(build)
        ):
            raise ValueError("Engine baseline assembly must be a regular build artifact: " + field)
        paths[field] = path.resolve()
        if sha256(path) != proof.get(field + "_sha256"):
            raise ValueError("Engine baseline assembly hash changed: " + field)
    if (
        not existing
        or paths["input"] != baseline.resolve()
        or proof["input_sha256"] != existing["baselineSha256"]
    ):
        raise ValueError("Engine baseline proof does not bind the recorded pristine baseline")
    if (
        paths["input"].name != GAME
        or paths["output"].name != GAME
        or paths["helper"].name != XR
        or paths["baseline_xr"].name != XR
        or len(set(paths.values())) != 4
        or proof["output_sha256"] == proof["input_sha256"]
    ):
        raise ValueError(
            "Engine baseline proof has inconsistent assembly paths or unchanged output"
        )
    if (managed / XR).is_symlink() or sha256(managed / XR) != proof["helper_sha256"]:
        raise ValueError("Install the exact proven XR helper before applying its engine baseline")
    # This one-time repair compares XR against the accepted extension preceding
    # the helper update, rather than the older XR beside the pristine Game.
    accepted_directory = ROOT / ENGINE_XR_ACCEPTANCE
    accepted_audit = accepted_directory / "before-native-app-audit.json"
    accepted_update = accepted_directory / "update.json"
    accepted_xr = accepted_directory / XR
    for path in (accepted_audit, accepted_update, accepted_xr):
        if path.is_symlink() or not path.is_file() or not path.resolve().is_relative_to(build):
            raise ValueError("Recorded accepted XR extension receipt is missing or unsafe")
    old_audit = json.loads(accepted_audit.read_text())
    update = json.loads(accepted_update.read_text())
    old_xr_hash = sha256(accepted_xr)
    records = [
        row
        for row in old_audit.get("binaries", [])
        if row.get("path") == "Contents/Resources/Data/Managed/" + XR
    ]
    changes = [row for row in update.get("changes", []) if row.get("assembly") == XR]
    if (
        old_audit.get("passed") is not True
        or Path(old_audit.get("app", "")).resolve() != APP.resolve()
        or Path(update.get("app", "")).resolve() != APP.resolve()
        or len(records) != 1
        or len(changes) != 1
        or records[0].get("sha256") != old_xr_hash
        or changes[0].get("beforeSha256") != old_xr_hash
        or changes[0].get("afterSha256") != old_xr_hash
        or changes[0].get("contract", {}).get("passed") is not True
        or proof["baseline_xr_sha256"] != old_xr_hash
    ):
        raise ValueError(
            "Engine baseline XR does not match its accepted extension receipt/inventory"
        )
    sources = proof.get("sources")
    expected_sources = ENGINE_PAIR_SOURCES if kind == ENGINE_PAIR_KIND else ENGINE_SOURCES
    if not isinstance(sources, dict) or set(sources) != expected_sources:
        raise ValueError("Engine baseline proof does not bind all required port sources")
    for relative, expected in sources.items():
        path = (ROOT / relative).resolve()
        if (
            not path.is_relative_to(ROOT.resolve())
            or not path.is_file()
            or sha256(path) != expected
        ):
            raise ValueError("Engine baseline source changed since preparation: " + relative)
    provenance = dict(
        proofPath=str(proof_path),
        proofSha256=sha256(proof_path),
        proof=proof,
        previousBaseline=str(baseline),
        previousBaselineSha256=existing["baselineSha256"],
        previousInstallStateSha256=sha256(STATE),
        acceptedXrExtension=dict(
            directory=str(accepted_directory),
            xrSha256=old_xr_hash,
            beforeAuditSha256=sha256(accepted_audit),
            updateSha256=sha256(accepted_update),
        ),
    )
    return paths["output"], provenance


def verify_engine_structure(provenance, receipt, managed):
    """Recompare assemblies using the current, source-bound Cecil verifier."""
    proof = provenance["proof"]
    if proof["kind"] == "newengine-packed-stereo-getter-only":
        project = ROOT / "native/stereo-packing-patch/StereoPackingPatch.csproj"
    elif proof["kind"] == ENGINE_PAIR_KIND:
        project = ROOT / "native/scope-pair-patch/ScopePairPatch.csproj"
    else:
        raise ValueError("No verifier for this engine baseline repair kind")
    subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--nologo"], check=True)
    verifier = project.parent / "bin/Release/net10.0" / (project.stem + ".dll")
    report_path = receipt / "engine-baseline-verified.json"
    subprocess.run(
        [
            "dotnet",
            str(verifier),
            "--verify",
            proof["input"],
            proof["helper"],
            proof["output"],
            str(report_path),
            str(managed),
            proof["baseline_xr"],
        ],
        check=True,
    )
    checked = json.loads(report_path.read_text())
    if checked.get("verification_mode") != "existing-candidate":
        raise ValueError(
            "Engine baseline independent verifier did not compare the existing candidate"
        )
    for key, expected in proof.items():
        if key != "verification_mode" and checked.get(key) != expected:
            raise ValueError("Engine baseline independent verification disagrees: " + key)
    provenance.update(
        independentVerification=str(report_path), independentVerificationSha256=sha256(report_path)
    )


def install(source, unity, engine_baseline_proof=None):
    source, unity = source.resolve(), unity.resolve()
    tool = source / "tools/prepare_new_engine.py"
    if not tool.is_file():
        raise ValueError("OptiH3VR offline preparation tool is missing: " + str(tool))
    managed = APP / "Contents/Resources/Data/Managed"
    manifest_path = APP / "Contents/Resources/Data/OptiH3VR" / MANIFEST
    legacy_manifest_path = manifest_path.with_name(LEGACY_MANIFEST)
    previous_manifest = manifest_path if manifest_path.exists() else legacy_manifest_path
    processes = subprocess.check_output(["ps", "-axo", "command="], text=True).splitlines()
    if any(line.startswith(str(APP / "Contents/MacOS") + "/") for line in processes):
        raise ValueError("Quit the generated player before updating OptiH3VR")
    check_saved_audit(APP, AUDIT)
    before = json.loads(AUDIT.read_text())
    before_audit_hash = sha256(AUDIT)
    current_hash = sha256(managed / GAME)
    existing = json.loads(STATE.read_text()) if STATE.is_file() else None
    baseline = managed / GAME
    recorded_install = bool(existing and current_hash == existing["gameSha256"])
    engine_provenance = existing.get("engineBaseline") if recorded_install else None
    if existing and current_hash == existing["gameSha256"]:
        if (
            not previous_manifest.is_file()
            or sha256(previous_manifest) != existing["manifestSha256"]
        ):
            raise ValueError("Installed OptiH3VR provenance changed")
        baseline = Path(existing["baseline"])
        if (
            not baseline.resolve().is_relative_to((ROOT / "build").resolve())
            or sha256(baseline) != existing["baselineSha256"]
        ):
            raise ValueError("Saved unmodified game assembly is missing or changed")
    for name in (HELPER, LEGACY_HELPER):
        if (managed / name).exists() and (
            not existing or sha256(managed / name) != existing["helperSha256"]
        ):
            raise ValueError("Refusing to overwrite an unrecorded OptiH3VR helper")
    for path in (
        baseline,
        managed / GAME,
        managed / HELPER,
        managed / LEGACY_HELPER,
        manifest_path,
        legacy_manifest_path,
    ):
        if path.is_symlink():
            raise ValueError("Refusing symlink assembly: " + str(path))
    previous_baseline = baseline
    if engine_baseline_proof is not None:
        if not recorded_install:
            raise ValueError(
                "Engine baseline proof requires the currently recorded OptiH3VR install"
            )
        baseline, engine_provenance = verify_engine_baseline(
            engine_baseline_proof, existing, baseline, managed
        )
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    receipt = ROOT / "build/optih3vr" / ("install-" + stamp)
    inputs, candidates, backup = (receipt / name for name in ("input", "candidate", "before"))
    for directory in (inputs, candidates, backup):
        directory.mkdir(parents=True)
    if engine_baseline_proof is not None:
        verify_engine_structure(engine_provenance, receipt, managed)
        shutil.copy2(engine_baseline_proof, receipt / "engine-baseline-proof.json")
    # The reflection adapter needs game dependencies, not Unity's copied BCL.
    for path in managed.glob("*.dll"):
        if path.name in (HELPER, LEGACY_HELPER) or path.name.startswith(
            ("System.", "UnityEngine.", "Mono.", "Unity.", "mscorlib", "netstandard")
        ):
            continue
        if path.read_bytes()[:2] == b"MZ":
            shutil.copy2(path, inputs / path.name)
    shutil.copy2(baseline, inputs / GAME)
    shutil.copy2(AUDIT, backup / "native-app-audit.json")
    if STATE.exists():
        shutil.copy2(STATE, backup / "installed.json")
    for name in (GAME, HELPER, LEGACY_HELPER):
        if (managed / name).exists():
            shutil.copy2(managed / name, backup / name)
    for path in (manifest_path, legacy_manifest_path):
        if path.exists():
            shutil.copy2(path, backup / path.name)
    build_manifest = candidates / "optih3vr-new-engine.json"
    subprocess.run(
        [
            "python3",
            str(tool),
            "--input",
            str(inputs / GAME),
            "--output",
            str(candidates / GAME),
            "--unity-contents",
            str(unity),
            "--manifest",
            str(build_manifest),
        ],
        check=True,
    )
    proof = json.loads(build_manifest.read_text())
    verify_preparation(proof, source, inputs, candidates)
    checker_project = ROOT / "platform/managed-extension-check/ManagedExtensionCheck.csproj"
    subprocess.run(
        ["dotnet", "build", str(checker_project), "-c", "Release", "--nologo"], check=True
    )
    checker = checker_project.parent / "bin/Release/net10.0/ManagedExtensionCheck.dll"
    checked = subprocess.run(
        ["dotnet", str(checker), str(inputs / GAME), str(candidates / GAME)],
        capture_output=True,
        text=True,
        check=True,
    )
    contract = json.loads(checked.stdout)
    if not contract.get("passed"):
        raise ValueError("Game assembly ABI/serialized layout changed")
    content = (
        APP / "Contents/Resources/Data/StreamingAssets/native-bundle-content-verification.json"
    )
    report = dict(
        schemaVersion=1,
        app=str(APP),
        modSource=str(source),
        scope="App-only OptiH3VR NewEngine runtime overlay; original content retained",
        limitation="The bundle proof binds the original Unity build. This overlay has separate assembly ABI, provenance and runtime evidence; it is not new full-game parity evidence.",
        preparation=proof,
        assemblyContract=contract,
        baseline=str(inputs / GAME),
        baselineSha256=sha256(inputs / GAME),
        gameSha256=proof["output_sha256"],
        helperSha256=proof["helper_sha256"],
        contentProofSha256=sha256(content),
        receipt=str(receipt),
        runtimeValidation="pending",
    )
    if engine_provenance is not None:
        report["engineBaseline"] = engine_provenance
    write_json(receipt / "install.json", report)
    # Builds can take long enough for a separate app update or player launch.
    # Recheck both the accepted inventory and source immediately before mutation.
    if sha256(AUDIT) != before_audit_hash:
        raise ValueError("App audit changed during preparation; retry against the new app")
    check_saved_audit(APP, AUDIT)
    verify_preparation(proof, source, inputs, candidates)
    if engine_baseline_proof is not None:
        _, rechecked = verify_engine_baseline(
            engine_baseline_proof, existing, previous_baseline, managed
        )
        if (
            any(engine_provenance.get(key) != value for key, value in rechecked.items())
            or sha256(inputs / GAME) != engine_provenance["proof"]["output_sha256"]
        ):
            raise ValueError("Engine baseline proof/state changed during OptiH3VR preparation")
    processes = subprocess.check_output(["ps", "-axo", "command="], text=True).splitlines()
    if any(line.startswith(str(APP / "Contents/MacOS") + "/") for line in processes):
        raise ValueError("Player launched during preparation; quit it and retry")
    try:
        for name in (GAME, HELPER):
            replace_file(candidates / name, managed / name)
        manifest_path.parent.mkdir(parents=True, exist_ok=True)
        write_json(manifest_path, report)
        (managed / LEGACY_HELPER).unlink(missing_ok=True)
        legacy_manifest_path.unlink(missing_ok=True)
        subprocess.run(["codesign", "--force", "--sign", "-", str(APP)], check=True)
        result = audit(APP)
        if not result["passed"]:
            raise ValueError("Updated app audit failed: " + repr(result["errors"]))
        old = {record["path"]: record["sha256"] for record in before["binaries"]}
        new = {record["path"]: record["sha256"] for record in result["binaries"]}
        allowed = {
            "Contents/Resources/Data/Managed/" + name for name in (GAME, HELPER, LEGACY_HELPER)
        }
        allowed.add("Contents/MacOS/NewEngineH3VR")
        unexpected = {
            name for name in old.keys() | new.keys() if old.get(name) != new.get(name)
        } - allowed
        if unexpected or sha256(content) != report["contentProofSha256"]:
            raise ValueError("Unexpected app/content changes: " + repr(sorted(unexpected)))
        report["manifestSha256"] = sha256(manifest_path)
        report["signatureVerified"] = True
        report["inventorySha256"] = result["inventorySha256"]
        result["optih3vrInstall"] = str(receipt / "install.json")
        write_json(AUDIT, result)
        write_json(receipt / "install.json", report)
        write_json(STATE, report)
        print(
            json.dumps(
                {
                    "passed": True,
                    "patches": proof["patch_count"],
                    "receipt": str(receipt),
                    "nativeCount": result["nativeCount"],
                    "managedCount": result["managedCount"],
                }
            )
        )
    except BaseException:
        for name in (GAME, HELPER, LEGACY_HELPER):
            if (backup / name).exists():
                replace_file(backup / name, managed / name)
            elif (managed / name).exists():
                (managed / name).unlink()
        for path in (manifest_path, legacy_manifest_path):
            if (backup / path.name).exists():
                replace_file(backup / path.name, path)
            elif path.exists():
                path.unlink()
        if (backup / "installed.json").exists():
            replace_file(backup / "installed.json", STATE)
        else:
            STATE.unlink(missing_ok=True)
        subprocess.run(["codesign", "--force", "--sign", "-", str(APP)], check=True)
        rollback = audit(APP)
        write_json(AUDIT, rollback)
        report["rolledBack"] = True
        report["rollbackAuditPassed"] = rollback["passed"]
        write_json(receipt / "install.json", report)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--source",
        type=Path,
        default=Path(os.environ.get("OPTIH3VR_SOURCE", ROOT.parent / "OptiH3VR")),
    )
    parser.add_argument(
        "--unity-contents",
        type=Path,
        default=ROOT
        / ".tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents",
    )
    parser.add_argument(
        "--engine-baseline-proof",
        type=Path,
        help="Exact getter-only or stereo-pair repair of the recorded pristine baseline; proven XR helper must already be installed",
    )
    options = parser.parse_args()
    install(options.source, options.unity_contents, options.engine_baseline_proof)
