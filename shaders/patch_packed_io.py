from pathlib import Path

p = Path(
    ".tools/assetripper-source-1.0.15/Source/AssetRipper.Export.Modules.Shader/UltraShaderConverter/USIL/Metadders/USILInputOutputMetadder.cs"
)
s = p.read_text()
s = s.replace(
    "List<USILInstruction> instructions = shader.instructions;",
    """// DXBC packs unrelated semantics into different lanes of one output register.
            // Evaluate the original instruction once, then distribute every written lane.
            List<USILInstruction> instructions = new();
            int packedIndex = 0;
            foreach (var inst in shader.instructions) {
                var dest = inst.destOperand;
                var outputs = dest?.operandType == USILOperandType.OutputRegister
                    ? shader.outputs.Where(o => o.register == dest.registerIndex && dest.mask.Any(m => (o.mask & (1 << m)) != 0)).ToList()
                    : new List<USILInputOutput>();
                if (outputs.Count <= 1) { instructions.Add(inst); continue; }
                string name = "packedOutput" + packedIndex++;
                shader.locals.Add(new USILLocal("float4", name, USILLocalType.Vector4));
                var temporary = new USILOperand(dest!) { operandType = USILOperandType.TempRegister, metadataName = name, metadataNameAssigned = true };
                inst.destOperand = temporary;
                instructions.Add(inst);
                foreach (var output in outputs) {
                    int[] lanes = dest!.mask.Where(m => (output.mask & (1 << m)) != 0).ToArray();
                    instructions.Add(new USILInstruction {
                        instructionType = USILInstructionType.Move,
                        destOperand = new USILOperand(dest) { mask = lanes },
                        srcOperands = new() { new USILOperand(temporary) { mask = lanes } }
                    });
                }
            }
            shader.instructions = instructions;""",
)
# Cross-semantic vector reads must preserve the original component ordering.
s = s.replace(
    "int searchMask = operand.mask.Length != 0 ? 1 << operand.mask[0] : 0;",
    """var matches = operand.mask.Select(m => shader.inputs.First(i => i.register == operand.registerIndex && (i.mask & (1 << m)) != 0)).Distinct().ToList();
                if (matches.Count > 1) {
                    var components = operand.mask.Select(m => new USILOperand(operand) { mask = new[] { m }, negative = false, absoluteValue = false }).ToArray();
                    foreach (var component in components) UseMetadata(component, shader);
                    operand.operandType = USILOperandType.Multiple;
                    operand.children = components;
                    operand.mask = Array.Empty<int>();
                    operand.displayMask = false;
                    return;
                }
                int searchMask = operand.mask.Length != 0 ? 1 << operand.mask[0] : 0;""",
)
p.write_text(s)
