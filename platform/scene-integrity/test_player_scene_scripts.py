import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "script"))
from verify_player_scene_scripts import compare_scene, stable


class SceneAuditTests(unittest.TestCase):
    def fixture(self):
        def scene(gid, cid):
            return {
                "objects": {
                    gid: {
                        "name": "Root",
                        "components": [("native", 4), ("script", "Game", "Gun"), None],
                        "componentIDs": [cid, cid + 1, cid + 2],
                    }
                },
                "scripts": {},
                "problems": [],
                "roots": [
                    {
                        "id": gid,
                        "name": "Root",
                        "pose": [[0, 0, 0], [0, 0, 0, 1], [1, 1, 1]],
                        "shape": stable(["Root", []]),
                        "children": [],
                    }
                ],
            }

        return (
            scene(10, 100),
            scene(20, 200),
            {"baseline": [{"gameObjectFileID": 10, "componentIndex": 2, "componentFileID": 102}]},
            {"Game": {"Gun"}},
        )

    def occlusion_collision(self):
        a, b, c, d = self.fixture()
        for scene, gid, cid in ((a, 10, 100), (b, 20, 200)):
            scene["objects"][gid]["components"].append(("native", 192))
            scene["objects"][gid]["componentIDs"].append(cid + 3)
            scene["objects"][gid]["occlusionAreas"] = [
                {"slot": 3, "center": [1, 2, 3], "size": [4, 5, 6], "viewVolume": True}
            ]
            scene["objects"][gid + 1] = copy.deepcopy(scene["objects"][gid])
            scene["objects"][gid + 1]["componentIDs"] = [
                v + 10 for v in scene["objects"][gid]["componentIDs"]
            ]
            scene["objects"][gid + 1]["occlusionAreas"][0]["center"] = [0, 0, 0]
            root = copy.deepcopy(scene["roots"][0])
            root["id"] = gid + 1
            scene["roots"].append(root)
        c["baseline"].append({"gameObjectFileID": 11, "componentIndex": 2, "componentFileID": 112})
        b["roots"].reverse()  # Storage order and local IDs must not determine identity.
        return a, b, c, d

    def test_distinct_occlusion_bounds_disambiguate_reordered_roots(self):
        result = compare_scene(*self.occlusion_collision())
        self.assertTrue(result["passed"], result["errors"])
        self.assertEqual(result["mappedGameObjects"], 2)
        self.assertEqual(
            {
                (x["sourceGameObject"], x["builtGameObject"])
                for x in result["preservedNullComponents"]
            },
            {(10, 20), (11, 21)},
        )

    def test_changed_occlusion_runtime_fields_fail(self):
        for field, value in (("center", [9, 9, 9]), ("size", [9, 9, 9]), ("viewVolume", False)):
            with self.subTest(field=field):
                a, b, c, d = self.occlusion_collision()
                b["objects"][20]["occlusionAreas"][0][field] = value
                self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_identical_occlusion_anchors_still_fail(self):
        a, b, c, d = self.occlusion_collision()
        for scene, gid in ((a, 10), (b, 20)):
            scene["objects"][gid + 1]["occlusionAreas"] = copy.deepcopy(
                scene["objects"][gid]["occlusionAreas"]
            )
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_collision_root_loss_fails(self):
        a, b, c, d = self.occlusion_collision()
        b["roots"] = [r for r in b["roots"] if r["id"] != 21]
        del b["objects"][21]
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_collision_root_duplication_fails(self):
        a, b, c, d = self.occlusion_collision()
        b["roots"].append(copy.deepcopy(b["roots"][0]))
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_collision_missing_anchor_fails(self):
        a, b, c, d = self.occlusion_collision()
        del b["objects"][20]["occlusionAreas"]
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_collision_cannot_hide_wrong_script_assembly(self):
        a, b, c, d = self.occlusion_collision()
        b["objects"][20]["components"][1] = ("script", "Other", "Gun")
        d["Other"] = {"Gun"}
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_collision_cannot_hide_null_slot_relocation(self):
        a, b, c, d = self.occlusion_collision()
        v = b["objects"][20]["components"]
        v[1], v[2] = v[2], v[1]
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_secondary_fields_unused_without_collision(self):
        a, b, c, d = self.fixture()
        a["objects"][10]["occlusionAreas"] = [{"center": [1, 2, 3]}]
        self.assertTrue(compare_scene(a, b, c, d)["passed"])

    def test_renumbered_ids_and_exact_original_null_pass(self):
        args = self.fixture()
        r = compare_scene(*args)
        self.assertTrue(r["passed"], r["errors"])
        self.assertEqual(len(r["preservedNullComponents"]), 1)

    def test_new_null_cannot_use_original_null_allowance(self):
        a, b, c, d = self.fixture()
        b["objects"][20]["components"][1] = None
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_type_present_in_scene_but_absent_from_shipped_dll_fails(self):
        a, b, c, d = self.fixture()
        d["Game"].clear()
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_same_named_script_from_wrong_assembly_fails(self):
        a, b, c, d = self.fixture()
        b["objects"][20]["components"][1] = ("script", "OtherGame", "Gun")
        d["OtherGame"] = {"Gun"}
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_original_null_relocated_to_different_slot_fails(self):
        a, b, c, d = self.fixture()
        components = b["objects"][20]["components"]
        components[1], components[2] = components[2], components[1]
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_removed_component_fails(self):
        a, b, c, d = self.fixture()
        b["objects"][20]["components"].pop(1)
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_unbaselined_source_null_fails(self):
        a, b, c, d = self.fixture()
        c["baseline"] = []
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_ambiguous_root_identity_fails(self):
        a, b, c, d = self.fixture()
        a["roots"].append(copy.deepcopy(a["roots"][0]))
        b["roots"].append(copy.deepcopy(b["roots"][0]))
        self.assertFalse(compare_scene(a, b, c, d)["passed"])

    def test_different_script_class_fails(self):
        a, b, c, d = self.fixture()
        b["objects"][20]["components"][1] = ("script", "Game", "OtherGun")
        d["Game"].add("OtherGun")
        self.assertFalse(compare_scene(a, b, c, d)["passed"])


if __name__ == "__main__":
    unittest.main()
