#!/usr/bin/env python3
# /// script
# requires-python = ">=3.10"
# dependencies = ["pillow"]
# ///
"""Compare matched material tiles from stock and converted runtime captures."""
from argparse import ArgumentParser
from collections import defaultdict
import json
from pathlib import Path
import re
from statistics import median
from urllib.parse import unquote

from PIL import Image


TEXTURES = ("texture", "normal", "packed", "emission")


def samples(root, scene):
    index = json.loads((root / (scene + ".json")).read_text())
    pages = []
    size = index["tilePixels"]
    width = index["columns"] * size
    for number in range(index["pages"]):
        with Image.open(root / f"{scene}-{number:03}.png") as image:
            if image.size != (width, width):
                raise ValueError(f"Unexpected swatch image size for {scene} page {number}")
            page = image.convert("RGB")
            if all(lo == hi for lo, hi in page.getextrema()):
                raise ValueError(f"Uniform swatch image for {scene} page {number}")
            pages.append(page)

    materials = defaultdict(list)
    for entry in index["materials"]:
        if entry["page"] < 0:
            continue
        key = tuple(entry[prop] for prop in ("material", *TEXTURES))
        prefix = {"shader": entry["shader"], "material": entry["material"], "keywords": entry["keywords"],
                  "page": entry["page"], "tile": entry["tile"],
                  "rendererMaterial": entry.get("rendererMaterial", False),
                  "meshMaterial": entry.get("meshMaterial", False),
                  **{slot: entry[slot] for slot in TEXTURES}}
        page = pages[entry["page"]]
        y, x = divmod(entry["tile"], index["columns"])
        clear = page.getpixel((x * size, y * size))
        x, y = x * size + size // 2, y * size + size // 2
        radius = size // 5
        pixels = [page.getpixel((x + dx, y + dy)) for dx in range(-radius, radius + 1)
                  for dy in range(-radius, radius + 1) if dx * dx + dy * dy <= radius * radius]
        peaks = sorted(max(pixel) for pixel in pixels)
        prefix["rgbMedian"] = [median(parts) for parts in zip(*pixels)]
        prefix["medianLuma"] = median(0.2126 * r + 0.7152 * g + 0.0722 * b for r, g, b in pixels)
        prefix["p90Peak"] = peaks[9 * (len(peaks) - 1) // 10]
        prefix["nearBlackFraction"] = sum(peak <= 8 for peak in peaks) / len(peaks)
        prefix["coverage"] = sum(max(abs(a - b) for a, b in zip(pixel, clear)) > 3 for pixel in pixels) / len(pixels)
        prefix["pinkFraction"] = sum(r > 230 and g < 20 and b > 230 for r, g, b in pixels) / len(pixels)
        materials[key].append(prefix)
    return index, materials


def measurement(original, converted):
    return {"stock": original, "player": converted,
            "medianLumaDelta": converted["medianLuma"] - original["medianLuma"],
            "nearBlackRegression": original["p90Peak"] >= 24 and converted["p90Peak"] <= 8,
            "missingDraw": original["coverage"] > 0.8 and converted["coverage"] < 0.03,
            "errorPink": original["pinkFraction"] < 0.05 and converted["pinkFraction"] > 0.8}


def by_name(entries):
    grouped = defaultdict(list)
    for entry in entries:
        grouped[entry["material"]].append(entry)
    return grouped


def compare_recipes(stock_index, player_index, stock_only, player_only):
    # Check the complete scope inventory, including materials excluded from the
    # swatch grid. An exact match elsewhere must not make a reused name unique.
    stock_names, player_names = (by_name(index["materials"]) for index in (stock_index, player_index))
    stock_rows, player_rows = (by_name(entries) for entries in (stock_only, player_only))
    report = []
    for name, rows in sorted(stock_rows.items()):
        target = "StockDefault" if name == "Default-Material" else name
        candidates = player_rows.get(target, [])
        if len(rows) != 1 or len(candidates) != 1 or len(stock_names[name]) != 1 or len(player_names[target]) != 1:
            continue
        original, converted = rows[0], candidates[0]
        if not original["rendererMaterial"] or not converted["rendererMaterial"]:
            continue
        differences = {slot: [original[slot], converted[slot]] for slot in TEXTURES
                       if original[slot] != converted[slot]}
        shaders = original["shader"], converted["shader"]
        if name != target:
            if (target in stock_names or shaders != ("Standard", "Universal Render Pipeline/Lit") or differences
                    or any(original[slot] for slot in TEXTURES)
                    or not original["meshMaterial"] or not converted["meshMaterial"]):
                continue
            basis = "generated-default-material"
        elif shaders == ("Alloy/Core", "Alloy/Core") and differences == {
            "normal": ["", "NativeH3VR neutral gear normal"]
        }:
            basis = "generated-neutral-normal"
        elif differences and (shaders[0] == shaders[1] or shaders == ("Alloy/Core", "NativeH3VR/Stock PBR")
                              or shaders[0] in ("Standard", "Standard (Specular setup)")
                              and shaders[1] == "Universal Render Pipeline/Lit"):
            if not all(before and re.fullmatch(re.escape(before) + r"_\d+", after)
                       for before, after in differences.values()):
                continue
            # Suffixes are consistent with exported names, but these captures
            # contain no texture identity to establish equivalence.
            basis = "numbered-texture-candidate"
        else:
            continue
        report.append({**measurement(original, converted), "basis": basis, "textureDifferences": differences})
    return report


def draw_slot_peers(stock_index, player_index):
    def index_slots(index):
        bindings = defaultdict(list)
        for entry in index["materials"]:
            for slot in entry.get("drawSlots") or []:
                # Unity's Mono versions escape punctuation differently. Decode
                # each component after splitting so escaped slashes stay in names.
                identity = tuple(unquote(part) for part in slot.split("/"))
                bindings[identity].append((entry["page"], entry["tile"]))
        return bindings

    stock, player = map(index_slots, (stock_index, player_index))
    stock_peers, player_peers = defaultdict(set), defaultdict(set)
    counts = defaultdict(int)
    for slot in stock.keys() & player.keys():
        if len(stock[slot]) != 1 or len(player[slot]) != 1:
            continue
        x, y = stock[slot][0], player[slot][0]
        stock_peers[x].add(y)
        player_peers[y].add(x)
        counts[x, y] += 1
    return stock_peers, player_peers, counts


def coordinates(row):
    return row["page"], row["tile"]


def compare(stock, player, scene):
    stock_index, stock_samples = samples(stock, scene)
    player_index, player_samples = samples(player, scene)
    empty = {tuple(unquote(part) for part in slot.split("/")) for slot in player_index.get("emptyMeshSlots", [])}
    lost_bindings = [
        {"slot": slot, "material": entry["material"], "shader": entry["shader"]}
        for entry in stock_index["materials"] for slot in entry.get("drawSlots") or []
        if tuple(unquote(part) for part in slot.split("/")) in empty
    ]
    matched, ambiguous, stock_only, player_only = [], [], [], []
    stock_peers, player_peers, slot_counts = draw_slot_peers(stock_index, player_index)
    for key in sorted(stock_samples.keys() | player_samples.keys()):
        expected, actual = stock_samples.get(key, []), player_samples.get(key, [])
        if not actual:
            stock_only.extend(expected)
        elif not expected:
            player_only.extend(actual)
        elif len(expected) != 1 or len(actual) != 1:
            ambiguous.append({"stock": expected, "player": actual})
        else:
            x, y = coordinates(expected[0]), coordinates(actual[0])
            if stock_peers[x] - {y} or player_peers[y] - {x}:
                ambiguous.append({"stock": expected, "player": actual, "reason": "conflicting-draw-slots"})
            else:
                matched.append({**measurement(expected[0], actual[0]), "sharedDrawSlots": slot_counts[x, y]})

    def residual(side, unmatched):
        return {coordinates(row): row for row in (*unmatched, *(r for group in ambiguous for r in group[side]))}

    stock_residual, player_residual = residual("stock", stock_only), residual("player", player_only)
    draw_slots = []
    for x, original in sorted(stock_residual.items()):
        if len(stock_peers[x]) != 1:
            continue
        y = next(iter(stock_peers[x]))
        if y in player_residual and player_peers[y] == {x}:
            draw_slots.append({**measurement(original, player_residual[y]), "sharedDrawSlots": slot_counts[x, y]})
    stock_used = {coordinates(row["stock"]) for row in draw_slots}
    player_used = {coordinates(row["player"]) for row in draw_slots}
    recipes = compare_recipes(stock_index, player_index,
                              [row for row in stock_only if coordinates(row) not in stock_used and not stock_peers[coordinates(row)]],
                              [row for row in player_only if coordinates(row) not in player_used and not player_peers[coordinates(row)]])
    return {
        "stock": {key: stock_index[key] for key in ("unity", "api", "colorSpace")},
        "player": {key: player_index[key] for key in ("unity", "api", "colorSpace")},
        "matched": matched, "ambiguous": ambiguous, "stockOnly": stock_only, "playerOnly": player_only,
        "recipeComparisons": recipes,
        "drawSlotComparisons": draw_slots,
        "lostMaterialBindings": lost_bindings,
    }


def main():
    parser = ArgumentParser(description=__doc__)
    for name in ("stock", "player", "out"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--scene", required=True, action="append", help="Scene or bundle name, or '*' for the complete sweep")
    args = parser.parse_args()
    if not all((root / "swatches-complete.txt").is_file() for root in (args.stock, args.player)):
        parser.error("Both captures must have completed")
    if "*" in args.scene:
        if args.scene != ["*"]:
            parser.error("Use '*' on its own for the complete sweep")

        def captured(root):
            return {p.stem for p in root.glob("*.json") if
                    {"materials", "tilePixels", "pages", "columns"}.issubset(json.loads(p.read_text()))}

        stock, player = captured(args.stock), captured(args.player)
        if not stock or stock != player:
            parser.error(f"Capture scopes differ. Stock only {sorted(stock - player)}, player only {sorted(player - stock)}")
        args.scene = sorted(stock)
    if any(Path(scene).name != scene for scene in args.scene):
        parser.error("Expected a scene name without directory components")
    reports = {}
    for scene in args.scene:
        report = reports[scene] = compare(args.stock, args.player, scene)
        print(scene, "matches", len(report["matched"]), "near-black candidates",
              sum(row["nearBlackRegression"] for row in report["matched"]),
              "additional recipe comparisons", len(report["recipeComparisons"]),
              "additional draw-slot comparisons", len(report["drawSlotComparisons"]))
    args.out.write_text(json.dumps(reports, indent=2) + "\n")


if __name__ == "__main__":
    main()
