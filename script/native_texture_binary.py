"""Lossless original Texture2D/Cubemap payloads in Unity 2020 native binary files.

Use audit-assets/.venv/bin/python. Templates are real Unity 2020.3.49f1
ForceBinary/CreateAsset output, not hand-constructed serialized-file headers.
This module writes no files on import or in native_file().
"""

from pathlib import Path
from functools import lru_cache
from copy import deepcopy
import UnityPy
import texture2ddecoder

TEMPLATES = Path(__file__).resolve().parents[1] / "build/native-texture-template"


@lru_cache(maxsize=2)
def _template(class_id):
    if class_id not in (28, 89):
        raise ValueError("Only Texture2D (28) and Cubemap (89) are supported")
    return (TEMPLATES / ("Texture2D.asset" if class_id == 28 else "Cubemap.asset")).read_bytes()


def native_file(original_tree, raw, class_id):
    """Return a self-contained native binary asset with canonical main fileID.

    raw must be the original full payload, including all mip levels and, for a
    cubemap, all six faces. Legacy Crunch containers are losslessly unwrapped
    into their original DXT blocks. No pixels are decoded or re-encoded.
    Original sampler wrapMode is expanded into the newer three-axis fields.
    Unity 5's Cubemap textureDimension=2 is upgraded to Unity 2020's Cube=4.
    """
    class_id = int(class_id)
    if original_tree["m_TextureFormat"] in (28, 29):
        if class_id != 28:
            raise ValueError("Legacy Crunch cubemaps require explicit face handling")
        original_tree = deepcopy(original_tree)
        block_size = 8 if original_tree["m_TextureFormat"] == 28 else 16
        levels = []
        for mip in range(original_tree["m_MipCount"]):
            level = texture2ddecoder.unpack_crunch(raw, mip)
            width = max(1, original_tree["m_Width"] >> mip)
            height = max(1, original_tree["m_Height"] >> mip)
            if len(level) != max(1, (width + 3) // 4) * max(1, (height + 3) // 4) * block_size:
                raise ValueError("Legacy Crunch mip byte count mismatch")
            levels.append(level)
        raw = b"".join(levels)
        original_tree["m_TextureFormat"] = 10 if block_size == 8 else 12
        original_tree["m_CompleteImageSize"] = len(raw)
    env = UnityPy.load(_template(class_id))
    obj = next(iter(env.objects))
    expected_id = 2800000 if class_id == 28 else 8900000
    if obj.path_id != expected_id or obj.type.value != class_id:
        raise ValueError("Unexpected native texture template identity")
    tree = obj.read_typetree()
    fields = (
        "m_Name",
        "m_Width",
        "m_Height",
        "m_CompleteImageSize",
        "m_TextureFormat",
        "m_MipCount",
        "m_IsReadable",
        "m_AlphaIsTransparency",
        "m_LightmapFormat",
        "m_ColorSpace",
    )
    for key in fields:
        if key in original_tree:
            tree[key] = original_tree[key]
    if int(tree["m_Width"]) <= 0 or int(tree["m_Height"]) <= 0 or int(tree["m_MipCount"]) <= 0:
        raise ValueError("Invalid source dimensions or mip count")
    face_count = 6 if class_id == 89 else 1
    if original_tree.get("m_ImageCount", face_count) != face_count:
        raise ValueError("Unexpected image count for texture class")
    if not raw:
        raise ValueError("Original texture payload is empty")
    # m_CompleteImageSize is per face for original cubemaps. Some formats have
    # padding; retaining the original size and exact bytes avoids guessing it.
    tree["m_ImageCount"] = face_count
    tree["m_TextureDimension"] = 4 if class_id == 89 else 2
    settings = original_tree["m_TextureSettings"]
    for key in ("m_FilterMode", "m_Aniso", "m_MipBias"):
        if key in settings:
            tree["m_TextureSettings"][key] = settings[key]
    for axis in ("U", "V", "W"):
        key = "m_Wrap" + axis
        tree["m_TextureSettings"][key] = settings.get(key, settings.get("m_WrapMode", 0))
    tree["image data"] = bytes(raw)
    tree["m_StreamData"] = {"offset": 0, "size": 0, "path": ""}
    if class_id == 89:
        # Runtime cubemaps carry their pixels directly. Never retain external
        # editor source-image pointers from another serialized collection.
        tree["m_SourceTextures"] = [{"m_FileID": 0, "m_PathID": 0} for _ in range(6)]
    obj.save_typetree(tree)
    return obj.assets_file.save()
