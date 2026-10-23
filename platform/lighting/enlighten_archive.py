#!/usr/bin/env python3
"""Losslessly package original shipped GI files; never synthesize solver variants."""

from pathlib import Path
import struct, hashlib, json
import UnityPy


def archive(files):
    """UnityFS v6 uncompressed archive with exact original relative member names."""
    payload = b""
    entries = b""
    for name, data in sorted(files.items()):
        entries += struct.pack(">qqI", len(payload), len(data), 0) + name.encode() + b"\0"
        payload += data
    info = (
        bytes(16)
        + struct.pack(">I", 1)
        + struct.pack(">IIH", len(payload), len(payload), 0)
        + struct.pack(">I", len(files))
        + entries
    )
    head = b"UnityFS\0" + struct.pack(">I", 6) + b"5.x.x\0" + b"5.6.7f1\0"
    result = (
        head
        + struct.pack(">qIII", len(head) + 20 + len(info) + len(payload), len(info), len(info), 64)
        + info
        + payload
    )
    env = UnityPy.load(result)
    bundle = next(iter(env.files.values()))
    assert set(bundle.files) == set(files)
    for name, f in bundle.files.items():
        assert f.bytes == files[name], name
    return result


def patch(data, payload):
    """Replace only Enlighten byte-vector; retain original NaN bit patterns."""
    env = UnityPy.load(data)
    obj = next(iter(env.objects))
    assert obj.type.value == 1120
    tree = obj.read_typetree()
    old = bytes(tree["m_EnlightenData"])
    raw = obj.get_raw_data()
    endian = obj.reader.endian
    needle = struct.pack(endian + "i", len(old)) + old
    assert raw.count(needle) == 1, "Ambiguous Enlighten vector"
    start = raw.index(needle)
    end = (start + len(needle) + 3) & ~3
    replacement = struct.pack(endian + "i", len(payload)) + payload
    replacement += bytes((-(start + len(replacement))) % 4)
    after = raw[:start] + replacement + raw[end:]
    obj.set_raw_data(after)
    result = obj.assets_file.save()
    check = next(iter(UnityPy.load(result).objects))
    assert (
        check.get_raw_data() == after and bytes(check.read_typetree()["m_EnlightenData"]) == payload
    )
    assert after[:start] == raw[:start] and after[start + len(replacement) :] == raw[end:]
    return result


if __name__ == "__main__":
    import argparse

    p = argparse.ArgumentParser()
    p.add_argument("gi", type=Path)
    p.add_argument("asset", type=Path)
    p.add_argument("output", type=Path)
    a = p.parse_args()
    files = {str(f.relative_to(a.gi)): f.read_bytes() for f in a.gi.rglob("*") if f.is_file()}
    result = patch(a.asset.read_bytes(), archive(files))
    a.output.parent.mkdir(parents=True, exist_ok=True)
    a.output.write_bytes(result)
    print(
        json.dumps(
            {
                "source": str(a.gi),
                "files": len(files),
                "originalBytes": sum(map(len, files.values())),
                "assetBytes": len(result),
                "sha256": hashlib.sha256(result).hexdigest(),
            }
        )
    )
