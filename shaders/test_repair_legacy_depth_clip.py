import importlib.util
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "legacy_depth_clip", Path(__file__).with_name("repair_legacy_depth_clip.py")
)
TOOL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TOOL)


def original(*states):
    return {
        "m_Name": "Unlit/PIPScope",
        "m_SubShaders": [{"m_Passes": [{"m_State": state} for state in states]}],
    }


def shader(first="ZClip Off", second="ZClip Off", newline="\n"):
    return (
        (
            """Shader "Unlit/PIPScope" {
 Properties { _Depth ("ZClip Off is a label", Float) = 0 }
 SubShader {
  Pass {
   Name "FORWARDBASE"
   %s // preserve comment café
   ZWrite On
   // ZClip Off is not an active directive
CGPROGRAM
float4 frag() { return 0; } // braces must not become ShaderLab blocks
ENDCG
  }
  Pass { Name "SHADOWCASTER"
   %s
   ZTest LEqual
  }
 }
}
"""
            % (first, second)
        )
        .replace("\n", newline)
        .encode()
    )


class DepthClipChecks(unittest.TestCase):
    def baseline(self):
        return original({"m_Name": "FORWARDBASE", "zTest": {"val": 4}}, {"m_Name": "SHADOWCASTER"})

    def test_missing_legacy_field_changes_only_two_literal_tokens(self):
        before = shader()
        after, rows = TOOL.repair_bytes(before, self.baseline())
        expected = before.replace(b"ZClip Off // preserve", b"ZClip On // preserve").replace(
            b"ZClip Off\n   ZTest", b"ZClip On\n   ZTest"
        )
        self.assertEqual(after, expected)
        self.assertEqual(sum(r["action"] == "invented-off-to-default-on" for r in rows), 2)
        self.assertIn(b'"ZClip Off is a label"', after)
        self.assertIn(b"// ZClip Off is not an active directive", after)

    def test_explicit_authored_off_remains_off(self):
        data = self.baseline()
        data["m_SubShaders"][0]["m_Passes"][0]["m_State"]["zClip"] = {"val": 0, "name": ""}
        after, rows = TOOL.repair_bytes(shader(), data)
        self.assertIn(b"ZClip Off // preserve", after)
        self.assertEqual(rows[0]["action"], "preserved-explicit-original-state")

    def test_explicit_property_and_null_original_states_preserved(self):
        for state in ({"val": 0, "name": "_Clip"}, None):
            data = self.baseline()
            data["m_SubShaders"][0]["m_Passes"][0]["m_State"]["zClip"] = state
            after, rows = TOOL.repair_bytes(shader("ZClip [_Clip]"), data)
            self.assertIn(b"ZClip [_Clip]", after)
            self.assertEqual(rows[0]["action"], "preserved-explicit-original-state")

    def test_candidate_property_not_replaced_when_original_field_absent(self):
        after, rows = TOOL.repair_bytes(shader("ZClip [_Clip]"), self.baseline())
        self.assertIn(b"ZClip [_Clip]", after)
        self.assertEqual(rows[0]["action"], "preserved-property-or-nonliteral-candidate")

    def test_already_on_and_omitted_default_are_idempotent(self):
        before = shader("ZClip On", "")
        after, rows = TOOL.repair_bytes(before, self.baseline())
        self.assertEqual(before, after)
        self.assertEqual(
            [r["action"] for r in rows], ["default-already-on", "default-already-omitted"]
        )

    def test_full_repair_is_idempotent(self):
        once, _ = TOOL.repair_bytes(shader(), self.baseline())
        twice, _ = TOOL.repair_bytes(once, self.baseline())
        self.assertEqual(once, twice)

    def test_pass_identity_mismatch_rejected(self):
        data = self.baseline()
        data["m_SubShaders"][0]["m_Passes"][1]["m_State"]["m_Name"] = "OTHER"
        with self.assertRaises(ValueError):
            TOOL.repair_bytes(shader(), data)

    def test_pass_count_mismatch_rejected(self):
        with self.assertRaises(ValueError):
            TOOL.repair_bytes(shader(), original({"m_Name": "FORWARDBASE"}))

    def test_ambiguous_duplicate_directive_rejected(self):
        with self.assertRaises(ValueError):
            TOOL.repair_bytes(shader("ZClip Off\n ZClip [_Clip]"), self.baseline())

    def test_subshader_inheritance_rejected(self):
        before = shader().replace(b" SubShader {", b" SubShader {\n ZClip Off")
        with self.assertRaises(ValueError):
            TOOL.repair_bytes(before, self.baseline())

    def test_other_shader_family_rejected(self):
        with self.assertRaises(ValueError):
            TOOL.repair_bytes(
                shader().replace(b'"Unlit/PIPScope"', b'"Other/Shader"', 1), self.baseline()
            )

    def test_crlf_and_nonascii_unrelated_bytes_preserved(self):
        before = shader(newline="\r\n")
        after, _ = TOOL.repair_bytes(before, self.baseline())
        self.assertEqual(after.count(b"\r\n"), before.count(b"\r\n"))
        self.assertIn("café".encode(), after)
        self.assertEqual(
            after,
            before.replace(b"ZClip Off // preserve", b"ZClip On // preserve").replace(
                b"ZClip Off\r\n   ZTest", b"ZClip On\r\n   ZTest"
            ),
        )

    def test_output_inside_input_or_original_game_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            for output in (
                base / "input/candidate",
                base / "H3VR/candidate",
                base / "H3VR/h3vr_Data/candidate",
            ):
                with self.assertRaises(ValueError):
                    TOOL.validate_output(output, base / "input", base / "H3VR/h3vr_Data")

    def test_output_inside_steam_library_or_app_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            for output in (base / "Steam/new", base / "Other.app/Contents/candidate"):
                with self.assertRaises(ValueError):
                    TOOL.validate_output(
                        output, base / "input", base / "Steam/steamapps/common/H3VR/h3vr_Data"
                    )

    def test_existing_output_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            with self.assertRaises(ValueError):
                TOOL.validate_output(base, base / "input", base / "original")

    def test_isolated_new_output_allowed_without_creating_it(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            output = base / "candidate"
            TOOL.validate_output(output, base / "input", base / "original")
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
