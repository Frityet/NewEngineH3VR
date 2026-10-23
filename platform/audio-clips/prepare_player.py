from pathlib import Path
import UnityPy, json, shutil

root = Path("build/audio-clips/AudioPlaybackProof.app/Contents/Resources/Data")
env = UnityPy.load("dist/NewEngineH3VR.app/Contents/Resources/Data/resources.assets")
obj = next(o for o in env.objects if o.type.name == "AudioClip")
f = obj.assets_file
tree = obj.read_typetree()
original = json.load(open("build/audio-clips/original.json"))
tree.update(original)
tree["m_Ambisonic"] = False
tree["m_Resource"] = {
    "m_Source": "AudioOriginal.resource",
    "m_Offset": 0,
    "m_Size": Path("build/audio-clips/original.fsb").stat().st_size,
}
obj.path_id = 1
obj.save_typetree(tree)
f.objects = {1: obj}
(root / "resources.assets").write_bytes(f.save())
shutil.copyfile("build/audio-clips/original.fsb", root / "AudioOriginal.resource")
env = UnityPy.load(str(root / "globalgamemanagers"))
obj = next(o for o in env.objects if o.type.name == "ResourceManager")
tree = obj.read_typetree()
tree["m_Container"] = [("original", {"m_FileID": 5, "m_PathID": 1})]
tree["m_DependentAssets"] = []
obj.save_typetree(tree)
(root / "globalgamemanagers").write_bytes(obj.assets_file.save())
(root / "RuntimeInitializeOnLoads.json").write_text('{"root":[]}')
