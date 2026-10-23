from pathlib import Path
import json, shutil
from native_audio import native_file

root = Path("audio-clip-probe/Assets")
rows = json.load(open("build/audio-clips/playback-cases.json"))
resource = root / "CaseAudio.resS"
with resource.open("wb") as output:
    for i, row in enumerate(rows):
        with (Path("build/audio-clips/staged") / row["resource"]).open("rb") as f:
            f.seek(row["offset"])
            raw = f.read(row["size"])
        offset = output.tell()
        output.write(raw)
        (root / "Resources" / ("case" + str(i) + ".asset")).write_bytes(
            native_file(row["metadata"], "Assets/CaseAudio.resS", offset, len(raw))
        )
for p in (Path("unity-full") / "Assets").rglob("*.mixer"):
    target = root / "Resources/MixerCases" / p.relative_to("unity-full/Assets")
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(p, target)
    shutil.copyfile(str(p) + ".meta", str(target) + ".meta")
print("Prepared", len(rows), "native audio cases and36 mixers")
