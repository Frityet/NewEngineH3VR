"""Retire generated legacy runtime assets without losing their contents."""

from pathlib import Path
import hashlib
import json


def reject_symlinks(path):
    if path.is_symlink() or (path.exists() and any(p.is_symlink() for p in path.rglob("*"))):
        raise ValueError("Refusing symlink runtime tree: " + str(path))


def retire_legacy_runtime(streaming, receipt):
    streaming, receipt = Path(streaming), Path(receipt)
    if streaming.is_symlink() or streaming.parent.is_symlink() or receipt.is_symlink():
        raise ValueError("Refusing symlink Assets/StreamingAssets or receipt path")
    if receipt.resolve().is_relative_to(streaming.parent.resolve()):
        raise ValueError("Runtime backup must be outside generated Assets")
    legacy, current = streaming / "NativeRuntime", streaming / "NewEngine"
    meta = streaming / "NativeRuntime.meta"
    for path in (legacy, current, meta):
        reject_symlinks(path)
    if not legacy.exists() and not meta.exists():
        return None
    if legacy.exists() and (not legacy.is_dir() or current.exists()):
        raise ValueError("Legacy/new runtime root collision; refusing to merge generated trees")
    if receipt.exists():
        raise ValueError("Runtime retirement receipt already exists: " + str(receipt))
    files = (
        {
            p.relative_to(legacy).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in legacy.rglob("*")
            if p.is_file()
        }
        if legacy.exists()
        else {}
    )
    metadata_hash = hashlib.sha256(meta.read_bytes()).hexdigest() if meta.exists() else None
    receipt.mkdir(parents=True)
    record = {
        "source": str(legacy),
        "backup": str(receipt / legacy.name),
        "fileSha256": files,
        "folderMetadataSha256": metadata_hash,
        "retired": False,
    }
    report = receipt / "retirement.json"
    report.write_text(json.dumps(record, indent=2) + "\n")
    if legacy.exists():
        legacy.rename(receipt / legacy.name)
    if meta.exists():
        meta.rename(receipt / meta.name)
    archived = {
        p.relative_to(receipt / legacy.name).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
        for p in (receipt / legacy.name).rglob("*")
        if p.is_file()
    }
    if archived != files:
        raise ValueError("Retired runtime contents changed; preserved receipt requires inspection")
    record["retired"] = True
    report.write_text(json.dumps(record, indent=2) + "\n")
    return report
