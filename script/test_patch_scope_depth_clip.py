#!/usr/bin/env python3
"""Small synthetic state/LZ4 fixtures only; no installed bundle or player."""

from copy import deepcopy
from pathlib import Path
import struct
import tempfile
import unittest
from UnityPy.helpers import CompressionHelper as compression
import patch_scope_depth_clip as patcher


def tree(modern):
    states = [
        dict(m_Name=name, zTest=dict(val=4.0, name="<noninit>")) for name in patcher.PASS_NAMES
    ]
    if modern:
        for state in states:
            state["zClip"] = dict(val=0.0, name="<noninit>")
    return dict(
        m_ParsedForm=dict(
            m_Name=patcher.SHADER, m_SubShaders=[dict(m_Passes=[dict(m_State=s) for s in states])]
        ),
        compressedBlob=b"unchanged compiled shader fixture",
    )


def write_bundle(path, blocks):
    packed = [compression.compress_lz4(b) for b in blocks]
    info = bytearray(b"\0" * 16 + struct.pack(">i", len(blocks)))
    for raw, data in zip(blocks, packed):
        info.extend(struct.pack(">IIH", len(raw), len(data), 3))
    info.extend(struct.pack(">iqqI", 1, 0, sum(map(len, blocks)), 4) + patcher.CAB.encode() + b"\0")
    compressed_info = compression.compress_lz4(info)
    prefix = bytearray(b"UnityFS\0" + struct.pack(">I", 8) + b"5.x.x\0" + b"2021.3.45f2\0")
    fields_at = len(prefix)
    prefix.extend(b"\0" * 20)
    prefix.extend(b"\0" * (-len(prefix) % 16))
    data_at = (len(prefix) + len(compressed_info) + 15) // 16 * 16
    struct.pack_into(
        ">qIII",
        prefix,
        fields_at,
        data_at + sum(map(len, packed)),
        len(compressed_info),
        len(info),
        579,
    )
    path.write_bytes(
        prefix
        + compressed_info
        + b"\0" * (data_at - len(prefix) - len(compressed_info))
        + b"".join(packed)
    )


class ScopeDepthClipTests(unittest.TestCase):
    def test_only_two_absent_original_state_values_change(self):
        original, modern = tree(False), tree(True)
        before = deepcopy(modern)
        changed = patcher.patch_tree(original, modern)
        self.assertEqual(patcher.differences(modern, changed), patcher.expected_fields())
        self.assertEqual(modern, before)
        self.assertEqual(modern["compressedBlob"], changed["compressedBlob"])

    def test_explicit_original_state_even_null_and_modern_property_binding_are_rejected(self):
        for value in (None, dict(val=0.0, name="<noninit>")):
            original = tree(False)
            patcher.passes(original)[0]["m_State"]["zClip"] = value
            with self.assertRaisesRegex(ValueError, "explicit zClip"):
                patcher.patch_tree(original, tree(True))
        modern = tree(True)
        patcher.passes(modern)[0]["m_State"]["zClip"]["name"] = "_ZClip"
        with self.assertRaisesRegex(ValueError, "non-property"):
            patcher.patch_tree(tree(False), modern)

    def test_changed_or_already_corrected_passes_fail_closed(self):
        for value in (1.0, False, 0, -1.0):
            modern = tree(True)
            patcher.passes(modern)[1]["m_State"]["zClip"]["val"] = value
            with self.assertRaises(ValueError):
                patcher.patch_tree(tree(False), modern)
        modern = tree(True)
        modern["m_ParsedForm"]["m_SubShaders"][0]["m_Passes"].pop()
        with self.assertRaises(ValueError):
            patcher.patch_tree(tree(False), modern)

    def test_only_affected_compressed_block_is_rewritten(self):
        with tempfile.TemporaryDirectory() as directory:
            old, new = Path(directory) / "old", Path(directory) / "new"
            blocks = [
                bytes(range(64)),
                b"middle untouched resource bytes" * 3,
                b"last untouched resource bytes" * 3,
            ]
            write_bundle(old, blocks)
            changes = [dict(offset=5, before=5, after=128), dict(offset=6, before=6, after=63)]
            old_layout = patcher.read_layout(old)
            self.assertEqual(patcher.rewrite_blocks(old, new, old_layout, changes), [0])
            result = patcher.verify_blocks(old, new, old_layout, patcher.read_layout(new), changes)
            self.assertEqual(result["unchangedCompressedBlocks"], 2)
            self.assertEqual([r["index"] for r in result["changedBlocks"]], [0])
            with self.assertRaises(FileExistsError):
                patcher.rewrite_blocks(old, new, old_layout, changes)

    def test_extra_resource_byte_change_and_wrong_original_byte_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            old, new = Path(directory) / "old", Path(directory) / "new"
            write_bundle(old, [bytes(range(64)), b"resource unchanged" * 4])
            changes = [dict(offset=5, before=5, after=128)]
            wrong = [dict(offset=5, before=99, after=128)]
            layout = patcher.read_layout(old)
            with self.assertRaisesRegex(ValueError, "Input bytes differ"):
                patcher.rewrite_blocks(old, new, layout, wrong)
            extra = changes + [dict(offset=70, before=ord("c"), after=ord("X"))]
            patcher.rewrite_blocks(old, new, layout, extra)
            with self.assertRaisesRegex(ValueError, "Unchanged compressed block"):
                patcher.verify_blocks(old, new, layout, patcher.read_layout(new), changes)

    def test_unknown_wrapper_flags_and_shader_field_changes_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            old = Path(directory) / "old"
            write_bundle(old, [bytes(range(64))])
            layout = patcher.read_layout(old)
            data = bytearray(old.read_bytes())
            struct.pack_into(">I", data, layout["fieldsAt"] + 16, 578)
            old.write_bytes(data)
            with self.assertRaisesRegex(ValueError, "Unsupported UnityFS"):
                patcher.read_layout(old)
        changed = tree(True)
        changed["unexpected"] = 1
        with self.assertRaisesRegex(ValueError, "field addition"):
            patcher.differences(tree(True), changed)


if __name__ == "__main__":
    unittest.main()
