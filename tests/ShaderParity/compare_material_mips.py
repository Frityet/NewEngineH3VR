#!/usr/bin/env python3
"""Compare complete GPU texture payloads and samplers bound to named material slots."""
from argparse import ArgumentParser
from hashlib import sha256
import json
from pathlib import Path

from compare_baked_lighting import open_player_data


def capture(root, bindings):
    result = {}
    with open_player_data(root) as (serialized, read_texture):
        for binding in bindings:
            collection, material, slot = binding.split("/", 2)
            owner = serialized(collection)
            matching = [obj for obj in owner.objects.values()
                        if obj.type.name == "Material" and obj.peek_name() == material]
            if len(matching) != 1:
                raise ValueError(f"Expected a unique {material!r} in {root / collection}, found {len(matching)}")
            properties = dict(matching[0].read().m_SavedProperties.m_TexEnvs)
            if slot not in properties or not properties[slot].m_Texture:
                raise ValueError(f"{binding} has no bound texture in {root}")
            texture, payload = read_texture(owner, properties[slot].m_Texture)
            settings = texture.m_TextureSettings
            wrap = [getattr(settings, "m_Wrap" + axis, None) for axis in "UVW"]
            if wrap[0] is None:
                wrap = [settings.m_WrapMode] * 3
            result[binding] = {
                "width": texture.m_Width, "height": texture.m_Height, "mips": texture.m_MipCount,
                "format": texture.m_TextureFormat, "colorSpace": texture.m_ColorSpace,
                "filter": settings.m_FilterMode, "aniso": settings.m_Aniso, "bias": settings.m_MipBias,
                "wrap": wrap, "bytes": len(payload), "sha256": sha256(payload).hexdigest(),
            }
    return result


def main():
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("--stock", type=Path, required=True)
    parser.add_argument("--player", type=Path, required=True)
    parser.add_argument("--binding", action="append", required=True, metavar="COLLECTION/MATERIAL/SLOT")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    stock, player = (capture(root, args.binding) for root in (args.stock, args.player))
    differences = {binding: [k for k in before if before[k] != player[binding][k]]
                   for binding, before in stock.items() if before != player[binding]}
    result = {"stock": stock, "player": player, "differences": differences}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, indent=2) + "\n")
    print(f"{len(stock)} material bindings checked, {len(differences)} different")
    if differences:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
