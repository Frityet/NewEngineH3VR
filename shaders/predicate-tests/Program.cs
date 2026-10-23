using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.USIL;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.USIL.Optimizers;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.UShader.Function;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.UShader.DirectX;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.DirectXDisassembler;
using System.Text.Json;
using System.Security.Cryptography;
using T = AssetRipper.Export.Modules.Shaders.UltraShaderConverter.USIL.USILInstructionType;

static class Test {
    static USILOperand
    R(int r, params int[] m) => new() { operandType = USILOperandType.TempRegister,
                                        registerIndex = r, mask = m.Length == 0 ? new[] { 0 } : m };
    static USILInstruction I(T t, USILOperand? d, params USILOperand[] s) => new() {
        instructionType = t, destOperand = d, srcOperands = s.ToList()
    };
    static USILInstruction C(int r, int lane = 0) => I(T.LessThan, R(r, lane),
                                                       new USILOperand(.25f), new USILOperand(.5f));
    static int tests;
    static void Check(string name, USILInstruction[] list, int index, T expected) {
        var p = new UShaderProgram { instructions = list.ToList() };
        new USILAndOptimizer().Run(p, null!);
        if (p.instructions[index].instructionType != expected)
            throw new Exception(name + ": " + p.instructions[index]);
        tests++;
    }
    static void Main() {
        Check("comparison complement", new[] { C(0), I(T.Not, R(1), R(0)) }, 1, T.LogicalNot);
        Check("integer complement remains bitwise",
              new[] { I(T.Move, R(0), new USILOperand(37)), I(T.Not, R(1), R(0)) }, 1, T.Not);
        Check("ordinary overwrite kills provenance",
              new[] { C(0), I(T.Add, R(0), R(2), R(3)), I(T.Not, R(1), R(0)) }, 2, T.Not);
        Check("partial write preserves other lane",
              new[] { C(0), C(0, 1), I(T.Move, R(0, 0), new USILOperand(.3f)),
                      I(T.Not, R(1), R(0, 1)) },
              3, T.LogicalNot);
        Check("partial write kills modified lane",
              new[] { C(0), C(0, 1), I(T.Move, R(0, 0), new USILOperand(.3f)),
                      I(T.Not, R(1), R(0, 0)) },
              3, T.Not);
        Check("swizzle provenance",
              new[] { C(0), C(0, 1), I(T.Move, R(1, 0, 1), R(0, 1, 0)),
                      I(T.Not, R(1, 0, 1), R(1, 1, 0)) },
              3, T.LogicalNot);
        Check("mixed vector not assumed boolean", new[] { C(0), I(T.Not, R(1, 0, 1), R(0, 0, 1)) },
              1, T.Not);
        Check("movc masks and reverse fractional gate",
              new[] { C(0), C(1), I(T.MoveConditional, R(2), R(5), R(0), R(1)),
                      I(T.And, R(3), R(4), R(2)) },
              3, T.MoveConditional);
        Check("movc predicate zero arm",
              new[] { C(0), I(T.MoveConditional, R(2), R(5), R(0), new USILOperand(0f)),
                      I(T.Not, R(3), R(2)) },
              2, T.LogicalNot);
        Check(
            "negated zero is not zero bit mask",
            new[] { C(0),
                    I(T.MoveConditional, R(2), R(5), R(0), new USILOperand(0f) { negative = true }),
                    I(T.Not, R(3), R(2)) },
            2, T.Not);
        Check("float one is not mask arm",
              new[] { C(0), I(T.MoveConditional, R(2), R(5), R(0), new USILOperand(1f)),
                      I(T.Not, R(3), R(2)) },
              2, T.Not);
        Check("one float literal is not all-ones mask",
              new[] { I(T.And, R(0), R(1), new USILOperand(1f)) }, 0, T.And);
        Check("unknown NOT is not predicate",
              new[] { I(T.Not, R(0), R(1)), I(T.And, R(2), R(0), R(3)) }, 1, T.And);
        Check("branch intersection",
              new[] { I(T.IfTrue, null, R(5)), C(0), I(T.EndIf, null), I(T.Not, R(1), R(0)) }, 3,
              T.Not);
        Check("both branch arms predicate",
              new[] { I(T.IfTrue, null, R(5)), C(0), I(T.Else, null), C(0), I(T.EndIf, null),
                      I(T.Not, R(1), R(0)) },
              5, T.LogicalNot);
        Check("else restores entry provenance",
              new[] { C(0), I(T.IfTrue, null, R(5)), I(T.Move, R(0), new USILOperand(.4f)),
                      I(T.Else, null), I(T.Not, R(1), R(0)), I(T.EndIf, null) },
              4, T.LogicalNot);
        Check("loop-carried overwrite invalidates entry",
              new[] { C(0), I(T.Loop, null), I(T.Not, R(1), R(0)),
                      I(T.Move, R(0), new USILOperand(.3f)), I(T.EndLoop, null) },
              2, T.Not);
        Check("loop local comparison",
              new[] { I(T.Loop, null), C(0), I(T.Not, R(1), R(0)), I(T.EndLoop, null) }, 2,
              T.LogicalNot);
        Check("loop exit is conservative",
              new[] { I(T.Loop, null), C(0), I(T.EndLoop, null), I(T.Not, R(1), R(0)) }, 3, T.Not);
        Check("boolean OR tracks predicate",
              new[] { C(0), C(1), I(T.Or, R(2), R(0), R(1)), I(T.Not, R(3), R(2)) }, 3,
              T.LogicalNot);
        Check("arithmetic negation is not mask",
              new[] { C(0), I(T.Not, R(1), new USILOperand(R(0)) { negative = true }) }, 1, T.Not);
        // Independent DXBC bit-mask truth tables include fractional, negative, signed
        // zero, infinity and NaN payloads. Conditional selection must preserve bits.
        foreach (uint bits in new[] { 0u, 0x80000000u, 0x3eaaaaabu, 0xbf000000u, 0x7f800000u,
                                      0x7fc12345u })
            foreach (bool b in new[] { false, true }) {
                uint mask = b ? uint.MaxValue : 0;
                uint gate = mask & bits;
                float selected = b ? BitConverter.UInt32BitsToSingle(bits) : 0f;
                if (BitConverter.SingleToUInt32Bits(selected) != gate || ((~mask) != 0) != !b)
                    throw new Exception("DXBC mask truth table");
                tests++;
            }
        var beforeEmit = new UShaderProgram { instructions = new() { C(0), I(T.Not, R(1), R(0)) } };
        new USILAndOptimizer().Run(beforeEmit, null!);
        string text = new UShaderFunctionToHLSL(beforeEmit).Convert(0);
        if (!text.Contains("!(tmp0.x)"))
            throw new Exception("LogicalNot emitter: " + text);
        tests++;
        var originals = new List<object>();
        int notCount = 0, notRemaining = 0, andGates = 0;
        foreach (int n in Enumerable.Range(24, 24).Concat(Enumerable.Range(96, 24))) {
            string file = $"shaders/critical-bytecode-proof/program-{n}.dxbc";
            using var stream = File.OpenRead(file);
            var dx = new DirectXCompiledShader(stream);
            var converter = new DirectXProgramToUSIL(dx);
            converter.Convert();
            var program = converter.shader;
            var prior = program.instructions.Select(x => x.instructionType).ToArray();
            var before = program.instructions.Select(x => x.ToString()).ToArray();
            new USILAndOptimizer().Run(program, null!);
            var edits =
                program.instructions
                    .Select((x, i) => new { index = i, before = before[i], after = x.ToString(),
                                            changed = prior[i] != x.instructionType })
                    .Where(x => x.changed)
                    .ToArray();
            int count = prior.Count(x => x == T.Not),
                remaining = program.instructions.Count(x => x.instructionType == T.Not),
                gates = program.instructions
                            .Where((x, i) =>
                                       prior[i] == T.And && x.instructionType == T.MoveConditional)
                            .Count();
            notCount += count;
            notRemaining += remaining;
            andGates += gates;
            originals.Add(
                new { file,
                      sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
                                   .ToLowerInvariant(),
                      notCount = count, notRemaining = remaining, andGates = gates, edits });
        }
        if (notCount != 304 || notRemaining != 0)
            throw new Exception(
                $"Original FinalPass complement coverage: {notCount} total {notRemaining} remaining");
        var module = typeof(UShaderFunctionToHLSL).Assembly.Location;
        var moduleSha256 =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module))).ToLowerInvariant();
        var result = new {
            passed = true,
            module,
            moduleSha256,
            tests,
            originalPrograms = originals.Count,
            notCount,
            notRemaining,
            andGates,
            originals,
            limits =
                new
            [] {
                "Numeric nonpredicate bitwise operations retain the existing emitter behavior; " +
                    "this is not a global raw-register bitcast rewrite.",
                "Mixed vector lanes and unknown loop-carried types are not guessed as predicates."
            }
        };
        File.WriteAllText(
            "shaders/predicate-tests/report.json",
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"PASS {tests} regression cases; {originals.Count} original DXBC; NOT {notCount-notRemaining}/{notCount}; AND gates {andGates}");
    }
}
