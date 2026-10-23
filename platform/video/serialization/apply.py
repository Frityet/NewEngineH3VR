#!/usr/bin/env python3
"""Apply only a fully validated recovery plan to working unity-full."""

import pathlib, json, hashlib, shutil, os

out = pathlib.Path("build/video/serialization")
plan = json.loads((out / "restoration-plan.json").read_text())
validation = json.loads((out / "unity-validation.json").read_text())
assert (
    validation["components"] == 67
    and validation["properties"] == 10452
    and not validation["errors"]
), validation
for f in plan["changed_files"]:
    p = pathlib.Path(f["target"])
    s = pathlib.Path(f["staged"])
    assert str(p).startswith("unity-full/Assets/")
    assert hashlib.sha256(p.read_bytes()).hexdigest() == f["before_sha256"], f["target"]
    assert hashlib.sha256(s.read_bytes()).hexdigest() == f["after_sha256"], f["staged"]
for f in plan["changed_files"]:
    p = pathlib.Path(f["target"])
    backup = out / "backups" / p.relative_to("unity-full")
    backup.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(p, backup)
    temp = p.with_name(p.name + ".avpro-recovery.tmp")
    shutil.copyfile(f["staged"], temp)
    os.replace(temp, p)
(out / "restoration-applied.json").write_text(json.dumps(plan, indent=2) + "\n")
print("Applied67components to34workingfull files; prior files backed up under", out / "backups")
