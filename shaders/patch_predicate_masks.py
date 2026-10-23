#!/usr/bin/env python3
"""Install conservative predicate-mask lowering without modifying texture handlers."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
base = (
    ROOT
    / ".tools/assetripper-source-1.0.15/Source/AssetRipper.Export.Modules.Shader/UltraShaderConverter"
)
(base / "USIL/Optimizers/USILAndOptimizer.cs").write_text(
    (ROOT / "shaders/predicate-tests/USILAndOptimizer.cs.txt").read_text()
)
p = base / "USIL/USILInstructionType.cs"
s = p.read_text()
if "LogicalNot" not in s:
    old = "\t\tComment\n"
    assert old in s
    s = s.replace(
        old,
        "\t\tComment,\n\t\tLogicalNot // proven comparison-mask complement; not an arbitrary integer NOT\n",
    )
    p.write_text(s)
p = base / "UShader/Function/UShaderFunctionToHLSL.cs"
s = p.read_text()
if "{ USILInstructionType.LogicalNot," not in s:
    old = "{ USILInstructionType.Not, new InstHandler(HandleNot) },"
    assert old in s
    s = s.replace(
        old,
        old
        + "\n                { USILInstructionType.LogicalNot, new InstHandler(HandleLogicalNot) },",
    )
if "void HandleLogicalNot(" not in s:
    old = "\t\tprivate void HandleNot(USILInstruction inst)"
    assert old in s
    s = s.replace(
        old,
        '\t\tprivate void HandleLogicalNot(USILInstruction inst)\n\t\t{\n\t\t\tAppendLine($"{CommentString(inst)}{inst.destOperand} = !({inst.srcOperands[0]});");\n\t\t}\n\n'
        + old,
    )
p.write_text(s)
print("Predicate-mask optimizer and LogicalNot handler installed; texture handlers untouched.")
