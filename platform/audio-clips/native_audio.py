"""Preserve original FSB payload and audio settings in native Unity2020 assets."""

from pathlib import Path
import UnityPy

TEMPLATE = Path(__file__).resolve().parents[2] / "build/audio-clips/AudioClip.asset"


def native_file(original_tree, source, offset, size):
    env = UnityPy.load(TEMPLATE.read_bytes())
    obj = next(iter(env.objects))
    assert obj.path_id == 8300000 and obj.type.value == 83
    tree = obj.read_typetree()
    for key, value in original_tree.items():
        if key in tree and key != "m_Resource":
            tree[key] = value
    tree["m_Resource"] = {"m_Source": source, "m_Offset": offset, "m_Size": size}
    tree["m_EditorResource"] = dict(tree["m_Resource"])
    tree["m_EditorCompressionFormat"] = tree["m_CompressionFormat"]
    obj.save_typetree(tree)
    return obj.assets_file.save()


if __name__ == "__main__":
    import json, shutil

    root = Path("audio-clip-probe/Assets/Resources")
    root.mkdir(exist_ok=True)
    shutil.copyfile("build/audio-clips/original.fsb", root / "Original.resS")
    tree = json.load(open("build/audio-clips/original.json"))
    (root / "Original.asset").write_bytes(
        native_file(
            tree, "Assets/Resources/Original.resS", 0, (root / "Original.resS").stat().st_size
        )
    )
