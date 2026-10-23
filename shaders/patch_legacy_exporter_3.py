from pathlib import Path

root = Path(__file__).resolve().parents[1] / ".tools/assetripper-source-1.0.15/Source"
base = root / "AssetRipper.Export.Modules.Shader/UltraShaderConverter"
p = base / "UShader/DirectX/DirectXProgramToUSIL.cs"
s = p.read_text().replace(
    "FillUSILOperand(srcReferenceValue, usilSrcReferenceValue, mask, false);",
    "FillUSILOperand(srcReferenceValue, usilSrcReferenceValue, new[] {srcReferenceValue.swizzle[0]}, false);",
)
p.write_text(s)
