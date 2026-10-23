"""Apply verified native clips to the working full project, preserving GUIDs.

All pristine exports and original Steam content remain read-only. Run with
--apply only while the working project's Unity Editor is stopped.
"""

from pathlib import Path
import json, re, shutil, hashlib, argparse, os

p = argparse.ArgumentParser()
p.add_argument("--project", default="unity-full")
p.add_argument("--apply", action="store_true")
a = p.parse_args()
base = Path("build/audio-clips")
stage = base / "staged"
project = Path(a.project)
manifest = json.load(open(base / "preservation-manifest.json"))
verification = json.load(open(base / "staged-verification.json"))
assert verification["checked"] == 13762 and verification["errors"] == []
rows = manifest["clips"]
guids = {r["guid"] for r in rows}
assert len(guids) == 13762
# Inspect all serialized YAML extensions, including .anim/.mixer/.fontsettings.
pattern = re.compile(rb"(\{fileID:\s*8300000,\s*guid:\s*([0-9a-f]{32}),\s*type:\s*)3(\s*\})")
yaml_changes = []
for path in (project / "Assets").rglob("*"):
    if not path.is_file() or path.suffix == ".meta":
        continue
    with path.open("rb") as f:
        head = f.read(16)
    if not head.startswith(b"%YAML"):
        continue
    data = path.read_bytes()
    n = 0

    def replace(m):
        global n
        if m[2].decode() not in guids:
            return m[0]
        n += 1
        return m[1] + b"2" + m[3]

    updated = pattern.sub(replace, data)
    if n:
        yaml_changes.append((path, data, updated, n))
# Validate every destination before making any changes.
for row in rows:
    old = project / row["original_export"]
    new = project / row["native_asset"]
    meta = Path(str(old) + ".meta")
    assert old.exists() and meta.exists(), str(old)
    assert not new.exists(), str(new)
    assert re.search(r"^guid: (\w+)", meta.read_text(), re.M)[1] == row["guid"]
print(
    "PREPARED",
    len(rows),
    "clips",
    len(yaml_changes),
    "YAML files",
    sum(x[3] for x in yaml_changes),
    "references",
    flush=True,
)
if not a.apply:
    raise SystemExit()
backup = base / "backup-before-native"
assert not backup.exists(), "Existing backup: refuse duplicate apply"
backup.mkdir()
report = dict(project=str(project), clips=[], yaml=[], resources=verification["resources"])
for row in rows:
    old = project / row["original_export"]
    new = project / row["native_asset"]
    oldmeta = Path(str(old) + ".meta")
    text = oldmeta.read_text()
    newmeta = (stage / (row["native_asset"] + ".meta")).read_text()
    for field in ("userData", "assetBundleName", "assetBundleVariant"):
        m = re.search(r"^  " + field + r":(.*)$", text, re.M)
        if m:
            newmeta = re.sub(
                r"^  " + field + r":.*$", lambda _: "  " + field + ":" + m[1], newmeta, flags=re.M
            )
    for src in (old, oldmeta):
        dst = backup / src.relative_to(project)
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(src, dst)
    new.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(stage / row["native_asset"], new)
    Path(str(new) + ".meta").write_text(newmeta)
    report["clips"].append(
        dict(guid=row["guid"], old=row["original_export"], new=row["native_asset"])
    )
for r in verification["resources"]:
    dst = project / r["path"]
    assert not dst.exists()
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(stage / r["path"], dst)
for path, before, after, count in yaml_changes:
    dst = backup / path.relative_to(project)
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_bytes(before)
    path.write_bytes(after)
    report["yaml"].append(
        dict(
            path=str(path.relative_to(project)),
            count=count,
            before_sha256=hashlib.sha256(before).hexdigest(),
            after_sha256=hashlib.sha256(after).hexdigest(),
        )
    )
(base / "applied.json").write_text(json.dumps(report, indent=2) + "\n")
print("APPLIED", len(rows), "clips", flush=True)
