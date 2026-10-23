#!/usr/bin/env python3
"""Installer transactions use temporary app fixtures; never operate on a player."""

from pathlib import Path
from copy import deepcopy
import contextlib
import io
import json
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import install_optih3vr as installer


class InstallerChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="optih3vr-installer-check-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.app = self.root / "Fake.app"
        self.managed = self.app / "Contents/Resources/Data/Managed"
        self.managed.mkdir(parents=True)
        (self.managed / installer.GAME).write_bytes(b"MZoriginal fixture")
        content = (
            self.app
            / "Contents/Resources/Data/StreamingAssets/native-bundle-content-verification.json"
        )
        content.parent.mkdir(parents=True)
        content.write_text("{}")
        self.source = self.root / "mod"
        (self.source / "tools").mkdir(parents=True)
        (self.source / "tools/prepare_new_engine.py").write_text("# fixture revision one")
        self.state = self.root / "build/optih3vr/installed.json"
        self.audit_path = self.root / "build/audit.json"
        self.audit_path.parent.mkdir(parents=True)
        self.inputs = []
        self.revision = 1
        for name, value in [
            ("ROOT", self.root),
            ("APP", self.app),
            ("STATE", self.state),
            ("AUDIT", self.audit_path),
        ]:
            p = patch.object(installer, name, value)
            p.start()
            self.addCleanup(p.stop)
        self.audit_path.write_text(json.dumps(self.audit(self.app)))
        for name, value in [("check_saved_audit", lambda *args: None), ("audit", self.audit)]:
            p = patch.object(installer, name, value)
            p.start()
            self.addCleanup(p.stop)
        p = patch.object(installer.subprocess, "check_output", return_value="")
        p.start()
        self.addCleanup(p.stop)
        p = patch.object(installer.subprocess, "run", side_effect=self.fake_run)
        p.start()
        self.addCleanup(p.stop)

    def audit(self, app):
        binaries = [
            {"path": str(p.relative_to(app)), "sha256": installer.sha256(p)}
            for p in sorted(self.managed.glob("*.dll"))
        ]
        return dict(
            passed=True,
            errors=[],
            binaries=binaries,
            inventorySha256="fixture",
            nativeCount=0,
            managedCount=len(binaries),
        )

    def fake_run(self, args, **kwargs):
        if args[0] == "python3":

            def option(name):
                return Path(args[args.index(name) + 1])

            source, output, manifest = option("--input"), option("--output"), option("--manifest")
            self.inputs.append(source.read_bytes())
            output.write_bytes(b"MZoptimized fixture " + str(self.revision).encode())
            helper = output.parent / installer.HELPER
            helper.write_bytes(b"MZhelper fixture")
            tool = self.source / "tools/prepare_new_engine.py"
            proof = dict(
                schema_version=1,
                runtime_detours=False,
                experimental_ai_nonalloc=False,
                structural_verification="passed: fixture",
                mod_version="fixture",
                sources={"tools/prepare_new_engine.py": installer.sha256(tool)},
                input_sha256=installer.sha256(source),
                output_sha256=installer.sha256(output),
                helper_sha256=installer.sha256(helper),
                patch_count=1,
                patches=[{}],
            )
            manifest.write_text(json.dumps(proof))
        return subprocess.CompletedProcess(args, 0, stdout=json.dumps({"passed": True}))

    def install(self):
        with contextlib.redirect_stdout(io.StringIO()):
            installer.install(self.source, self.root / "Unity")

    def test_repeat_and_new_full_build_use_correct_pristine_input(self):
        self.install()
        self.revision = 2
        (self.source / "tools/prepare_new_engine.py").write_text("# fixture revision two")
        self.install()
        self.assertEqual(self.inputs, [b"MZoriginal fixture", b"MZoriginal fixture"])
        # A new full build has a new accepted game hash and must supersede the
        # earlier saved baseline even while prior install state still exists.
        (self.managed / installer.GAME).write_bytes(b"MZnew full build fixture")
        self.audit_path.write_text(json.dumps(self.audit(self.app)))
        self.install()
        self.assertEqual(self.inputs[-1], b"MZnew full build fixture")

    def test_metadata_failure_restores_existing_state_and_app(self):
        self.install()
        before = {
            p: p.read_bytes()
            for p in [
                self.managed / installer.GAME,
                self.managed / installer.HELPER,
                self.state,
                self.app / "Contents/Resources/Data/OptiH3VR/new-engine-install.json",
            ]
        }
        actual = installer.write_json

        def fail_state(path, data):
            if path == self.state:
                actual(path, data)  # Simulate failure after a metadata replacement.
                raise OSError("injected metadata failure")
            actual(path, data)

        self.revision = 2
        with patch.object(installer, "write_json", side_effect=fail_state):
            with self.assertRaisesRegex(OSError, "injected metadata"):
                self.install()
        for path, content in before.items():
            self.assertEqual(path.read_bytes(), content, str(path))
        self.assertTrue(json.loads(self.audit_path.read_text())["passed"])

    def test_missing_structural_proof_rejected_before_app_change(self):
        before = (self.managed / installer.GAME).read_bytes()
        actual_run = self.fake_run

        def stale_proof(args, **kwargs):
            result = actual_run(args, **kwargs)
            if args[0] == "python3":
                manifest = Path(args[args.index("--manifest") + 1])
                data = json.loads(manifest.read_text())
                data.pop("structural_verification")
                manifest.write_text(json.dumps(data))
            return result

        with patch.object(installer.subprocess, "run", side_effect=stale_proof):
            with self.assertRaisesRegex(ValueError, "preparation proof"):
                self.install()
        self.assertEqual((self.managed / installer.GAME).read_bytes(), before)
        self.assertFalse(self.state.exists())

    def test_copy_failure_does_not_truncate_target(self):
        source = self.root / "candidate.dll"
        source.write_bytes(b"new")
        target = self.root / "installed.dll"
        target.write_bytes(b"old")

        def broken_copy(src, dst):
            Path(dst).write_bytes(b"partial")
            raise OSError("injected interrupted copy")

        with patch.object(installer.shutil, "copy2", side_effect=broken_copy):
            with self.assertRaisesRegex(OSError, "interrupted copy"):
                installer.replace_file(source, target)
        self.assertEqual(target.read_bytes(), b"old")
        self.assertFalse(list(self.root.glob(".*.tmp")))

    def test_source_change_after_build_rejected_before_app_change(self):
        before = (self.managed / installer.GAME).read_bytes()
        actual_run = self.fake_run

        def edit_after_build(args, **kwargs):
            result = actual_run(args, **kwargs)
            if args[0] == "python3":
                (self.source / "tools/prepare_new_engine.py").write_text(
                    "# source changed after compilation"
                )
            return result

        with patch.object(installer.subprocess, "run", side_effect=edit_after_build):
            with self.assertRaisesRegex(ValueError, "source changed since preparation"):
                self.install()
        self.assertEqual((self.managed / installer.GAME).read_bytes(), before)
        self.assertFalse(self.state.exists())

    def make_legacy_install(self):
        self.install()
        (self.managed / installer.HELPER).rename(self.managed / installer.LEGACY_HELPER)
        directory = self.app / "Contents/Resources/Data/OptiH3VR"
        (directory / installer.MANIFEST).rename(directory / installer.LEGACY_MANIFEST)
        self.audit_path.write_text(json.dumps(self.audit(self.app)))

    def test_recorded_legacy_names_migrate_with_pristine_baseline(self):
        self.make_legacy_install()
        self.revision = 2
        self.install()
        self.assertEqual(self.inputs[-1], b"MZoriginal fixture")
        self.assertTrue((self.managed / installer.HELPER).is_file())
        self.assertFalse((self.managed / installer.LEGACY_HELPER).exists())
        directory = self.app / "Contents/Resources/Data/OptiH3VR"
        self.assertTrue((directory / installer.MANIFEST).is_file())
        self.assertFalse((directory / installer.LEGACY_MANIFEST).exists())

    def test_failed_migration_restores_recorded_legacy_names(self):
        self.make_legacy_install()
        previous_state = self.state.read_bytes()
        actual = installer.write_json

        def fail_state(path, data):
            if path == self.state:
                actual(path, data)
                raise OSError("injected migration failure")
            actual(path, data)

        self.revision = 2
        with patch.object(installer, "write_json", side_effect=fail_state):
            with self.assertRaisesRegex(OSError, "migration failure"):
                self.install()
        self.assertEqual(self.state.read_bytes(), previous_state)
        self.assertTrue((self.managed / installer.LEGACY_HELPER).is_file())
        self.assertFalse((self.managed / installer.HELPER).exists())
        directory = self.app / "Contents/Resources/Data/OptiH3VR"
        self.assertTrue((directory / installer.LEGACY_MANIFEST).is_file())
        self.assertFalse((directory / installer.MANIFEST).exists())

    def engine_fixture(self, pair=False):
        self.install()
        existing = json.loads(self.state.read_text())
        accepted = self.root / installer.ENGINE_XR_ACCEPTANCE
        accepted.mkdir(parents=True)
        old_xr = accepted / installer.XR
        old_xr.write_bytes(b"MZaccepted old XR fixture")
        old_hash = installer.sha256(old_xr)
        (accepted / "before-native-app-audit.json").write_text(
            json.dumps(
                dict(
                    passed=True,
                    app=str(self.app),
                    binaries=[
                        dict(
                            path="Contents/Resources/Data/Managed/" + installer.XR, sha256=old_hash
                        )
                    ],
                )
            )
        )
        (accepted / "update.json").write_text(
            json.dumps(
                dict(
                    app=str(self.app),
                    changes=[
                        dict(
                            assembly=installer.XR,
                            beforeSha256=old_hash,
                            afterSha256=old_hash,
                            contract=dict(passed=True),
                        )
                    ],
                )
            )
        )
        candidate = self.root / "build/stereo-candidate"
        candidate.mkdir()
        output = candidate / installer.GAME
        output.write_bytes(b"MZpristine stereo correction fixture")
        xr = candidate / installer.XR
        xr.write_bytes(b"MZnew XR fixture")
        (self.managed / installer.XR).write_bytes(xr.read_bytes())
        self.audit_path.write_text(json.dumps(self.audit(self.app)))
        sources = {}
        for relative in installer.ENGINE_PAIR_SOURCES if pair else installer.ENGINE_SOURCES:
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("# source fixture " + relative)
            sources[relative] = installer.sha256(path)
        proof = dict(
            schema_version=1,
            kind="newengine-packed-stereo-getter-only",
            verification_mode="prepared-candidate",
            engine_define="NEWENGINE_UNITY_2021",
            target=installer.ENGINE_TARGET,
            helper_method=installer.ENGINE_HELPER_METHOD,
            expected_old_opcodes=["ldc.i4.1", "ret"],
            expected_new_opcodes=["call", "ret"],
            all_existing_types_fields_properties_events_and_method_metadata_preserved=True,
            all_other_method_bodies_preserved=True,
            xr_all_existing_type_and_field_layouts_preserved=True,
            checks=100,
            untouched_method_bodies=25,
            xr_existing_method_bodies_preserved=3,
            sources=sources,
        )
        if pair:
            proof.update(deepcopy(installer.ENGINE_PAIR_REQUIRED))
            proof["kind"] = installer.ENGINE_PAIR_KIND
        for key, path in [
            ("input", Path(existing["baseline"])),
            ("output", output),
            ("helper", xr),
            ("baseline_xr", old_xr),
        ]:
            proof[key] = str(path)
            proof[key + "_sha256"] = installer.sha256(path)
        path = candidate / "engine-baseline-proof.json"
        path.write_text(json.dumps(proof))
        return path, proof, existing

    def engine_install(self, path, proof):
        self.engine_verifiers = []

        def run(args, **kwargs):
            if "--verify" in args:
                self.engine_verifiers.append(Path(args[1]).name)
                index = args.index("--verify")
                checked = dict(proof, verification_mode="existing-candidate")
                Path(args[index + 4]).write_text(json.dumps(checked))
            return self.fake_run(args, **kwargs)

        with (
            patch.object(installer.subprocess, "run", side_effect=run),
            contextlib.redirect_stdout(io.StringIO()),
        ):
            installer.install(self.source, self.root / "Unity", path)

    def test_engine_baseline_becomes_pristine_and_survives_mod_updates(self):
        path, proof, _ = self.engine_fixture()
        self.engine_install(path, proof)
        self.assertEqual(self.inputs[-1], b"MZpristine stereo correction fixture")
        state = json.loads(self.state.read_text())
        self.assertEqual(state["baselineSha256"], proof["output_sha256"])
        self.assertEqual(state["engineBaseline"]["proofSha256"], installer.sha256(path))
        self.assertTrue(Path(state["engineBaseline"]["independentVerification"]).is_file())
        self.install()
        self.assertEqual(self.inputs[-1], b"MZpristine stereo correction fixture")
        self.assertEqual(
            json.loads(self.state.read_text())["engineBaseline"], state["engineBaseline"]
        )
        (self.managed / installer.GAME).write_bytes(b"MZfresh full engine build")
        self.audit_path.write_text(json.dumps(self.audit(self.app)))
        self.install()
        self.assertNotIn("engineBaseline", json.loads(self.state.read_text()))
        self.assertEqual(self.inputs[-1], b"MZfresh full engine build")

    def test_engine_proof_rejects_wrong_getter_metadata_flags_or_unrecorded_baseline(self):
        path, proof, existing = self.engine_fixture()
        variants = [
            dict(target="System.Boolean Other::get_Policy()"),
            dict(all_other_method_bodies_preserved=False),
            dict(all_existing_types_fields_properties_events_and_method_metadata_preserved=1),
            dict(expected_new_opcodes=["ldc.i4.0", "ret"]),
            dict(input_sha256="0" * 64),
            dict(untouched_method_bodies=0),
            dict(sources={}),
            dict(helper_method="System.Boolean Other::get_Fix()"),
        ]
        for change in variants:
            path.write_text(json.dumps(dict(proof, **change)))
            with self.subTest(change=change), self.assertRaises(ValueError):
                installer.verify_engine_baseline(
                    path, existing, Path(existing["baseline"]), self.managed
                )

    def test_engine_proof_requires_exact_already_installed_xr_and_accepted_origin(self):
        path, proof, existing = self.engine_fixture()
        (self.managed / installer.XR).write_bytes(b"MZdifferent helper")
        with self.assertRaisesRegex(ValueError, "exact proven XR"):
            installer.verify_engine_baseline(
                path, existing, Path(existing["baseline"]), self.managed
            )
        (self.managed / installer.XR).write_bytes(Path(proof["helper"]).read_bytes())
        accepted = self.root / installer.ENGINE_XR_ACCEPTANCE / "update.json"
        value = json.loads(accepted.read_text())
        value["changes"][0]["afterSha256"] = "other"
        accepted.write_text(json.dumps(value))
        with self.assertRaisesRegex(ValueError, "accepted extension"):
            installer.verify_engine_baseline(
                path, existing, Path(existing["baseline"]), self.managed
            )

    def test_engine_proof_rejects_source_changes_and_paths_outside_build(self):
        path, proof, existing = self.engine_fixture()
        outside = self.root / installer.GAME
        outside.write_bytes(Path(proof["output"]).read_bytes())
        path.write_text(json.dumps(dict(proof, output=str(outside))))
        with self.assertRaisesRegex(ValueError, "regular build artifact"):
            installer.verify_engine_baseline(
                path, existing, Path(existing["baseline"]), self.managed
            )
        path.write_text(json.dumps(proof))
        (self.root / next(iter(installer.ENGINE_SOURCES))).write_text("# changed after proof")
        with self.assertRaisesRegex(ValueError, "source changed"):
            installer.verify_engine_baseline(
                path, existing, Path(existing["baseline"]), self.managed
            )

    def test_engine_proof_cannot_override_a_fresh_full_build(self):
        path, proof, _ = self.engine_fixture()
        (self.managed / installer.GAME).write_bytes(b"MZfresh full engine build")
        before = (self.managed / installer.GAME).read_bytes()
        with self.assertRaisesRegex(ValueError, "currently recorded"):
            self.engine_install(path, proof)
        self.assertEqual((self.managed / installer.GAME).read_bytes(), before)

    def test_independent_engine_verifier_disagreement_rejected_before_app_mutation(self):
        path, proof, _ = self.engine_fixture()
        before = (self.managed / installer.GAME).read_bytes()
        checked = dict(proof, target="Other")
        with self.assertRaisesRegex(ValueError, "independent verification disagrees"):
            self.engine_install(path, checked)
        self.assertEqual((self.managed / installer.GAME).read_bytes(), before)

    def test_engine_source_change_during_mod_build_rejected_before_app_mutation(self):
        path, proof, _ = self.engine_fixture()
        before = (self.managed / installer.GAME).read_bytes()
        actual = self.fake_run

        def change_source(args, **kwargs):
            result = actual(args, **kwargs)
            if args[0] == "python3":
                (self.root / next(iter(installer.ENGINE_SOURCES))).write_text(
                    "# changed during mod preparation"
                )
            return result

        self.fake_run = change_source
        with self.assertRaisesRegex(ValueError, "source changed"):
            self.engine_install(path, proof)
        self.assertEqual((self.managed / installer.GAME).read_bytes(), before)

    def test_exact_pair_repair_uses_only_its_fixed_verifier_and_new_pristine(self):
        path, proof, _ = self.engine_fixture(pair=True)
        self.engine_install(path, proof)
        self.assertEqual(self.engine_verifiers, ["ScopePairPatch.dll"])
        self.assertEqual(self.inputs[-1], b"MZpristine stereo correction fixture")
        recorded = json.loads(self.state.read_text())["engineBaseline"]["proof"]
        self.assertEqual(recorded["scope_setter_callsites"], installer.ENGINE_PAIR_CALLSITES)
        self.assertEqual(
            recorded["changed_game_methods"],
            [installer.ENGINE_TARGET, installer.ENGINE_SCOPE_TARGET],
        )
        self.install()
        self.assertEqual(self.inputs[-1], b"MZpristine stereo correction fixture")

    def test_pair_proof_rejects_callsite_index_opcode_signature_missing_and_extra_changes(self):
        path, proof, existing = self.engine_fixture(pair=True)
        variants = []
        for key, value in [
            ("instruction_index", 332),
            ("old_opcode", "call"),
            ("old_method", "System.Void Other::set_enabled(System.Boolean)"),
            ("new_opcode", "callvirt"),
            ("new_method", "System.Void Other::SetEnabled(UnityEngine.Behaviour,System.Boolean)"),
        ]:
            calls = deepcopy(proof["scope_setter_callsites"])
            calls[0][key] = value
            variants.append(dict(scope_setter_callsites=calls))
        variants.extend(
            [
                dict(scope_setter_callsites=proof["scope_setter_callsites"][:-1]),
                dict(
                    scope_setter_callsites=proof["scope_setter_callsites"]
                    + [proof["scope_setter_callsites"][0]]
                ),
                dict(scope_setter_original_body_sha256="0" * 64),
                dict(
                    changed_game_methods=proof["changed_game_methods"]
                    + ["System.Void Other::Update()"]
                ),
                dict(kind="arbitrary-engine-overlay"),
            ]
        )
        for change in variants:
            path.write_text(json.dumps(dict(proof, **change)))
            with self.subTest(change=change), self.assertRaises(ValueError):
                installer.verify_engine_baseline(
                    path, existing, Path(existing["baseline"]), self.managed
                )

    def test_pair_proof_rejects_other_xr_methods_helper_additions_or_body_fingerprints(self):
        path, proof, existing = self.engine_fixture(pair=True)
        changed_hashes = deepcopy(proof["xr_changed_body_fingerprints"])
        changed_hashes[0]["after_sha256"] = "0" * 64
        variants = [
            dict(changed_existing_xr_methods=[]),
            dict(
                changed_existing_xr_methods=proof["changed_existing_xr_methods"]
                + ["System.Void Other::Render()"]
            ),
            dict(xr_changed_body_fingerprints=changed_hashes),
            dict(xr_added_types=proof["xr_added_types"] + ["UnexpectedHelper"]),
            dict(xr_added_methods=proof["xr_added_methods"] + ["System.Void Other::Method()"]),
            dict(xr_added_properties=[]),
            dict(xr_iterator_instruction_delta=5),
            dict(xr_iterator_finally_delta=True),
        ]
        for change in variants:
            path.write_text(json.dumps(dict(proof, **change)))
            with self.subTest(change=change), self.assertRaises(ValueError):
                installer.verify_engine_baseline(
                    path, existing, Path(existing["baseline"]), self.managed
                )

    def test_pair_proof_requires_its_exact_source_set_and_current_helper_source(self):
        path, proof, existing = self.engine_fixture(pair=True)
        sources = dict(proof["sources"])
        sources.pop("native/managed/NativeScopeActivation.cs")
        path.write_text(json.dumps(dict(proof, sources=sources)))
        with self.assertRaisesRegex(ValueError, "required port sources"):
            installer.verify_engine_baseline(
                path, existing, Path(existing["baseline"]), self.managed
            )
        path.write_text(json.dumps(proof))
        (self.root / "native/managed/NativeScopeActivation.cs").write_text("# stale helper source")
        with self.assertRaisesRegex(ValueError, "source changed"):
            installer.verify_engine_baseline(
                path, existing, Path(existing["baseline"]), self.managed
            )

    def test_pair_candidate_byte_tampering_and_independent_body_disagreement_rejected(self):
        path, proof, existing = self.engine_fixture(pair=True)
        output = Path(proof["output"])
        original = output.read_bytes()
        output.write_bytes(original + b"tampered")
        with self.assertRaisesRegex(ValueError, "assembly hash changed: output"):
            installer.verify_engine_baseline(
                path, existing, Path(existing["baseline"]), self.managed
            )
        output.write_bytes(original)
        checked = deepcopy(proof)
        checked["scope_setter_callsites"][0]["instruction_index"] = 332
        before = (self.managed / installer.GAME).read_bytes()
        with self.assertRaisesRegex(ValueError, "independent verification disagrees"):
            self.engine_install(path, checked)
        self.assertEqual((self.managed / installer.GAME).read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
