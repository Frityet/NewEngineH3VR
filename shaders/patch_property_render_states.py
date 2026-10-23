"""Repair named render-state emission in the legacy exporter; run once on source."""

from pathlib import Path

root = Path(__file__).resolve().parents[1] / ".tools/assetripper-source-1.0.15/Source"
p = root / "AssetRipper.SourceGenerated.Extensions/SerializedShaderFloatValueExtensions.cs"
s = p.read_text()
marker = "\t\tpublic static bool IsZero"
helper = """        public static string PropertyReferenceName(this ISerializedShaderFloatValue value)
        {
            string name = value.Has_Name_R_FastPropertyName()
                ? value.Name_R_FastPropertyName.Name.String
                : value.Name_R_Utf8String.String;
            return string.IsNullOrEmpty(name) || name == "<noninit>" ? string.Empty : name;
        }
        public static bool HasPropertyReference(this ISerializedShaderFloatValue value) => value.PropertyReferenceName().Length != 0;
"""
assert "PropertyReferenceName" not in s
s = s.replace(marker, helper + marker)
p.write_text(s)
p = root / "AssetRipper.SourceGenerated.Extensions/SerializedStencilOpExtensions.cs"
s = p.read_text()
s = s.replace(
    "return stencilOp.PassValue().IsKeep()",
    "return !stencilOp.Pass.HasPropertyReference() && !stencilOp.Fail.HasPropertyReference()\n                && !stencilOp.ZFail.HasPropertyReference() && !stencilOp.Comp.HasPropertyReference()\n                && stencilOp.PassValue().IsKeep()",
)
p.write_text(s)
p = root / "AssetRipper.Export.Modules.Shader/IO/SerializedExtensions.cs"
s = p.read_text()
s = s.replace(
    "using AssetRipper.SourceGenerated.Subclasses.SerializedShaderState;",
    "using AssetRipper.SourceGenerated.Subclasses.SerializedShaderState;\nusing AssetRipper.SourceGenerated.Subclasses.SerializedShaderFloatValue;",
)
marker = "\t\tpublic static void Export(this ISerializedPass"
helper = """        private static string StateExpression(ISerializedShaderFloatValue value, object fallback)
            => value.HasPropertyReference() ? $"[{value.PropertyReferenceName()}]" : fallback.ToString()!;
"""
s = s.replace(marker, helper + marker)
fields = [
    "SrcBlend",
    "DestBlend",
    "SrcBlendAlpha",
    "DestBlendAlpha",
    "BlendOp",
    "BlendOpAlpha",
    "ZTest",
    "ZWrite",
    "Culling",
]
# Change only emitted expressions, preserving enum/default checks below.
lines = s.splitlines(keepends=True)
for i, line in enumerate(lines):
    if "writer.Write" not in line:
        continue
    for field in fields:
        line = line.replace(
            "{_this." + field + "Value()}",
            "{StateExpression(_this." + field + ", _this." + field + "Value())}",
        )
    for field in [
        "OffsetFactor",
        "OffsetUnits",
        "StencilRef",
        "StencilReadMask",
        "StencilWriteMask",
    ]:
        line = line.replace(
            "{_this." + field + ".Val}",
            "{StateExpression(_this." + field + ", _this." + field + ".Val.ToStringInvariant())}",
        )
    lines[i] = line
s = "".join(lines)
s = s.replace(
    "if (!_this.SrcBlendValue().IsOne() || !_this.DestBlendValue().IsZero()",
    "if (_this.SrcBlend.HasPropertyReference() || _this.DestBlend.HasPropertyReference() || _this.SrcBlendAlpha.HasPropertyReference() || _this.DestBlendAlpha.HasPropertyReference() || !_this.SrcBlendValue().IsOne() || !_this.DestBlendValue().IsZero()",
)
s = s.replace(
    "if (!_this.SrcBlendValue().IsOne() || !_this.DestBlendAlphaValue().IsZero())",
    "if (_this.SrcBlendAlpha.HasPropertyReference() || _this.DestBlendAlpha.HasPropertyReference() || _this.SrcBlendValue() != _this.SrcBlendAlphaValue() || _this.DestBlendValue() != _this.DestBlendAlphaValue())",
)
s = s.replace(
    "if (!_this.BlendOpValue().IsAdd() || !_this.BlendOpAlphaValue().IsAdd())",
    "if (_this.BlendOp.HasPropertyReference() || _this.BlendOpAlpha.HasPropertyReference() || !_this.BlendOpValue().IsAdd() || !_this.BlendOpAlphaValue().IsAdd())",
)
s = s.replace(
    "writer.Write(_this.BlendOpValue().ToString());",
    "writer.Write(StateExpression(_this.BlendOp, _this.BlendOpValue()));",
)
s = s.replace(
    "if (!_this.BlendOpAlphaValue().IsAdd())",
    "if (_this.BlendOpAlpha.HasPropertyReference() || !_this.BlendOpAlphaValue().IsAdd())",
)
s = s.replace(
    "if (!_this.ColMaskValue().IsRBGA())",
    "if (_this.ColMask.HasPropertyReference() || !_this.ColMaskValue().IsRBGA())",
)
s = s.replace(
    "if (_this.ColMaskValue().IsNone())",
    'if (_this.ColMask.HasPropertyReference()) { writer.Write($"[{_this.ColMask.PropertyReferenceName()}]"); }\n                else if (_this.ColMaskValue().IsNone())',
)
s = s.replace(
    "if (_this.AlphaToMaskValue())",
    "if (_this.AlphaToMask.HasPropertyReference() || _this.AlphaToMaskValue())",
).replace(
    'writer.Write("AlphaToMask On\\n");',
    'writer.Write($"AlphaToMask {StateExpression(_this.AlphaToMask, "On")}\\n");',
)
for field, condition in [
    ("ZTest", "!_this.ZTestValue().IsLEqual() && !_this.ZTestValue().IsNone()"),
    ("ZWrite", "!_this.ZWriteValue().IsOn()"),
    ("Culling", "!_this.CullingValue().IsBack()"),
]:
    s = s.replace(
        "if (" + condition + ")",
        "if (_this." + field + ".HasPropertyReference() || (" + condition + "))",
    )
# Keep existing static stencil behavior, but emit actual named properties first.
s = s.replace(
    "if (_this.CompValue() == StencilComp.Disabled)",
    'if (_this.Comp.HasPropertyReference()) { writer.Write($"Comp{type.ToSuffixString()} [{_this.Comp.PropertyReferenceName()}]\\n"); }\n            else if (_this.CompValue() == StencilComp.Disabled)',
)
for field in ["Pass", "Fail", "ZFail"]:
    s = s.replace(
        "{_this." + field + "Value()}",
        "{StateExpression(_this." + field + ", _this." + field + "Value())}",
    )
p.write_text(s)

# Property references only affect programmable fixed render states here; legacy Fog is unchanged.
p = root / "AssetRipper.Export.Modules.Shader/IO/SerializedExtensions.cs"
s = p.read_text()
s = s.replace(
    "if (!_this.OffsetFactor.IsZero() || !_this.OffsetUnits.IsZero())",
    "if (_this.OffsetFactor.HasPropertyReference() || _this.OffsetUnits.HasPropertyReference() || !_this.OffsetFactor.IsZero() || !_this.OffsetUnits.IsZero())",
)
s = s.replace(
    "if (!_this.StencilRef.IsZero() || !_this.StencilReadMask.IsMax() || !_this.StencilWriteMask.IsMax()",
    "if (_this.StencilRef.HasPropertyReference() || _this.StencilReadMask.HasPropertyReference() || _this.StencilWriteMask.HasPropertyReference() || !_this.StencilRef.IsZero() || !_this.StencilReadMask.IsMax() || !_this.StencilWriteMask.IsMax()",
)
for field, check in [
    ("StencilRef", "IsZero"),
    ("StencilReadMask", "IsMax"),
    ("StencilWriteMask", "IsMax"),
]:
    s = s.replace(
        "if (!_this." + field + "." + check + "())",
        "if (_this." + field + ".HasPropertyReference() || !_this." + field + "." + check + "())",
    )
p.write_text(s)
