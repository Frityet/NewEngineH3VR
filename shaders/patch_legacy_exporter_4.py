from pathlib import Path

root = Path(__file__).resolve().parents[1] / ".tools/assetripper-source-1.0.15/Source"
base = root / "AssetRipper.Export.Modules.Shader/UltraShaderConverter"
p = base / "UShader/DirectX/DirectXProgramToUSIL.cs"
s = p.read_text().replace(
    "{ Opcode.or, new InstHandler(HandleOr) },",
    "{ Opcode.or, new InstHandler(HandleOr) },\n                { Opcode.xor, new InstHandler(HandleXor) },\n                { Opcode.ineg, new InstHandler(HandleIntegerNegate) },",
)
a = s.index("\t\tprivate void HandleOr(")
b = s.index("\t\tprivate void HandleNot(", a)
xor = (
    s[a:b]
    .replace("HandleOr", "HandleXor")
    .replace("USILInstructionType.Or", "USILInstructionType.Xor")
)
s = (
    s[:b]
    + xor
    + """        private void HandleIntegerNegate(SHDRInstruction inst) {
            var dest=new USILOperand();var src=new USILOperand();
            FillUSILOperand(inst.operands[0],dest,inst.operands[0].swizzle,true);
            FillUSILOperand(inst.operands[1],src,MapMask(inst.operands[0].swizzle,inst.operands[1].swizzle),true);
            src.negative=!src.negative;
            Instructions.Add(new USILInstruction{instructionType=USILInstructionType.Move,destOperand=dest,srcOperands=new(){src}});
        }
"""
    + s[b:]
)
p.write_text(s)
p = base / "UShader/Function/UShaderFunctionToHLSL.cs"
s = p.read_text().replace(
    "{ USILInstructionType.Or, new InstHandler(HandleOr) },",
    "{ USILInstructionType.Or, new InstHandler(HandleOr) },\n                { USILInstructionType.Xor, new InstHandler(HandleXor) },",
)
a = s.index("\t\tprivate void HandleOr(")
b = s.index("\t\tprivate void HandleNot(", a)
xor = s[a:b].replace("HandleOr", "HandleXor").replace("}) | uint", "}) ^ uint")
s = s[:b] + xor + s[b:]
p.write_text(s)
