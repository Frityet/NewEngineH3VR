#!/usr/bin/env python3
"""Compare the baked textures actually bound to each scene in two private Unity players."""
from argparse import ArgumentParser
from contextlib import ExitStack
from functools import lru_cache
from hashlib import sha256
import json
from pathlib import Path

import UnityPy
from UnityPy.helpers.TypeTreeNode import TypeTreeNode

from inventory import STOCK_PARSE_BLOB, parse_67_blob
from player_archive import PlayerArchive


TEXTURE_FIELDS = ("m_Width", "m_Height", "m_TextureFormat", "m_MipCount", "m_ColorSpace", "m_LightmapFormat")
MAPS = ("m_Lightmap", "m_DirLightmap", "m_ShadowMask")


def survey(root):
    with ExitStack() as stack:
        bundle = root / "data.unity3d"
        archive = PlayerArchive(stack.enter_context(bundle.open("rb"))) if bundle.is_file() else None

        def read(name, offset=0, size=None):
            if archive and name in archive.members:
                return archive.read(name, offset, size)
            with (root / name).open("rb") as stream:
                stream.seek(offset)
                return stream.read() if size is None else stream.read(size)

        @lru_cache(maxsize=2)
        def serialized(name):
            return UnityPy.Environment().load_file(read(name), name=name)

        def texture(owner, pointer):
            name = Path(owner.externals[pointer.file_id - 1].path).name if pointer.file_id else owner.name
            obj = serialized(name).objects[pointer.path_id].read()
            data = obj.m_StreamData
            payload = (read(Path(data.path).name, data.offset, data.size)
                       if data and data.size else bytes(obj.image_data))
            return {"fields": {field: getattr(obj, field, None) for field in TEXTURE_FIELDS},
                    "bytes": len(payload), "sha256": sha256(payload).hexdigest()}

        manager = serialized("globalgamemanagers")
        # The scene list is the first serialized field. The 6.7 beta appends
        # eight trailing bytes that the current UnityPy class database omits.
        build = next(obj.read(check_read=False) for obj in manager.objects.values() if obj.type.name == "BuildSettings")
        scenes = {}
        for index, scene in enumerate(build.scenes):
            if scene in scenes:
                raise ValueError(f"Ambiguous scene name {scene!r} in {root}")
            owner = serialized(f"level{index}")
            settings = next((obj.read() for obj in owner.objects.values() if obj.type.name == "LightmapSettings"), None)
            scenes[scene] = [
                {kind: texture(owner, pointer) for kind in MAPS if (pointer := getattr(slot, kind))}
                for slot in (settings.m_Lightmaps if settings else [])]
        return scenes


def main():
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("--stock", type=Path, required=True, help="Retail player Data directory")
    parser.add_argument("--player", type=Path, required=True, help="Converted player Data directory")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    TypeTreeNode.parse_blob = classmethod(parse_67_blob)
    try:
        stock, player = (survey(root) for root in (args.stock, args.player))
    finally:
        TypeTreeNode.parse_blob = classmethod(STOCK_PARSE_BLOB)
    differences = []
    matched = 0
    for scene in sorted(stock.keys() | player.keys()):
        before, after = stock.get(scene), player.get(scene)
        if before is None or after is None or len(before) != len(after):
            differences.append({"scene": scene, "stockSlots": None if before is None else len(before),
                                "playerSlots": None if after is None else len(after)})
        for index, (a, b) in enumerate(zip(before or [], after or [])):
            for kind in sorted(a.keys() | b.keys()):
                if a.get(kind) == b.get(kind):
                    matched += 1
                else:
                    differences.append({"scene": scene, "slot": index, "kind": kind,
                                        "stock": a.get(kind), "player": b.get(kind)})
    report = {"stockScenes": len(stock), "playerScenes": len(player), "matchingTextures": matched,
              "differences": differences, "stock": stock, "player": player}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2) + "\n")
    print(f"{matched} matched textures, {len(differences)} differences across {len(stock)} retail scenes")
    if differences:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
