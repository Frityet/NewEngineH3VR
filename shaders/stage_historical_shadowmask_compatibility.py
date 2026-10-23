"""Stage the tested historical Alloy include at an authorized idle checkpoint."""

from pathlib import Path
import sys, json, hashlib, shutil

if "--apply" not in sys.argv:
    raise SystemExit("Requires --apply at an authorized idle checkpoint")
from verify_shader_proof_logs import require_clean

require_clean(
    Path(__file__).resolve().parent / "unity2021-historical-shadowmask-isolated-build.log",
    Path(__file__).resolve().parent / "alloy-historical-shadowmask-native-player.log",
)
root = Path(__file__).resolve().parents[1]
record = json.loads((root / "shaders/historical-shadowmask-compatibility.json").read_text())
proof = json.loads((root / record["editor_validation"]).read_text())
assert proof["passed"] and len(proof["draws"]) == 435
native = json.loads((root / record["native_validation"]).read_text())
assert native["passed"] and native["draws"] == 435
source = root / record["source"]
assert hashlib.sha256(source.read_bytes()).hexdigest() == record["sha256"]
staged = []
for project in ["unity", "unity-full"]:
    target = root / project / "Assets/AlloyUnity56/Shaders/Framework/Forward.cginc"
    meta = target.with_suffix(".cginc.meta")
    prior_meta = meta.read_bytes() if meta.exists() else None
    before = hashlib.sha256(target.read_bytes()).hexdigest()
    assert before in [record["prior_sha256"], record["sha256"]]
    shutil.copyfile(source, target)
    assert not meta.exists() if prior_meta is None else meta.read_bytes() == prior_meta
    staged.append(str(target.relative_to(root)))
record["staged_targets"] = staged
record["stage_status"] = "staged"
(root / "shaders/historical-shadowmask-compatibility.json").write_text(json.dumps(record, indent=2))
print("Staged historical Alloy shared shadowmask include in both graphs; metadata preserved")
