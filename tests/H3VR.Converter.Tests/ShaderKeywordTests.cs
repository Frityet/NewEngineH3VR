using System.Text;
using NativeH3VR.ShaderOverlay;

namespace H3VR.Converter.Tests;

internal static class ShaderKeywordTests
{
    public static IEnumerable<(string, Action)> Cases()
    {
        yield return ("exclusive variant directives describe exactly the original product", ExactProduct);
        yield return ("pairwise independence does not certify a missing higher-order variant", Parity);
        yield return ("irregular lighting retains each original row when restoring the required mode", IrregularLighting);
        yield return ("irregular SpeedTree variants still require a geometry mode", SpeedTreeGeometry);
        yield return ("a generated variant guard must assign each declared keyword once", CompleteGuard);
        yield return ("split shaders preserve each program guard and keep entry point directives in ShaderLab", SplitPrograms);
        yield return ("verified instancing variants rebind only the recognized engine layout", InstancingBindings);
        yield return ("shader catalog requires every original pass with the same callback key", CompleteVariantGroups);
    }

    private static void ExactProduct()
    {
        var names = new[] { "FIXED", "LEFT", "RIGHT", "OPTION_A", "OPTION_B", "EXTRA" };
        var rows = from orientation in new[] { 4UL, 2UL }
                   from option in new[] { 0UL, 8UL, 16UL }
                   from extra in new[] { 0UL, 32UL }
                   select 1UL | orientation | option | extra;
        var originals = rows.ToArray();
        var plan = ShaderKeywordPlan.Create(names, originals);
        Check(plan.Exact && plan.ChangesDeclarations);
        Check(plan.Directives().Contains("#pragma multi_compile RIGHT LEFT"));
        Check(plan.Directives().Contains("#pragma multi_compile __ OPTION_A OPTION_B"));
        Check(Expand(names, plan).SetEquals(originals));
    }

    private static void Parity()
    {
        var names = new[] { "A", "B", "C" };
        var originals = new[] { 0UL, 3UL, 5UL, 6UL };
        var plan = ShaderKeywordPlan.Create(names, originals);
        Check(!plan.Exact && !plan.ChangesDeclarations);
    }

    private static void IrregularLighting()
    {
        var names = new[] { "DIRECTIONAL", "POINT", "SHADOWS_DEPTH", "LD" };
        var originals = new[] { 1UL, 5UL, 9UL, 6UL };
        var plan = ShaderKeywordPlan.Create(names, originals);
        Check(!plan.Exact && plan.ChangesDeclarations);
        Check(plan.Directives().Contains("#pragma multi_compile DIRECTIONAL POINT"));
        var compiled = Expand(names, plan);
        Check(originals.All(compiled.Contains) && !compiled.Contains(0UL) && !compiled.Contains(3UL));
    }

    private static void SpeedTreeGeometry()
    {
        var names = new[] { "GEOM_TYPE_BRANCH", "GEOM_TYPE_LEAF", "GEOM_TYPE_FROND", "WIND", "LIGHTMAP_ON" };
        var originals = new[] { 1UL, 2UL, 4UL, 9UL, 17UL, 10UL, 20UL };
        var plan = ShaderKeywordPlan.Create(names, originals);
        Check(!plan.Exact && plan.ChangesDeclarations);
        Check(plan.Directives().Contains("#pragma multi_compile GEOM_TYPE_BRANCH GEOM_TYPE_LEAF GEOM_TYPE_FROND"));
        var compiled = Expand(names, plan);
        Check(originals.All(compiled.Contains) && !compiled.Contains(0UL) && !compiled.Contains(3UL));
    }

    private static void CompleteGuard()
    {
        var positions = new Dictionary<string, ulong> { ["A"] = 1, ["B"] = 2 };
        Check(KeywordRepair.TryReadGuard("defined(A) && !defined(B)", positions, out var mask) && mask == 1);
        Check(!KeywordRepair.TryReadGuard("defined(A)", positions, out _));
        Check(!KeywordRepair.TryReadGuard("defined(A) && !defined(A)", positions, out _));
        Check(!KeywordRepair.TryReadGuard("defined(A) && defined(B) && defined(C)", positions, out _));
    }

    private static void CompleteVariantGroups()
    {
        var first = ShaderVariantCatalog.FromRows(0, 0, ["DIRECTIONAL", "FOG_EXP2"], ["DIRECTIONAL"], [3, 1]);
        var second = ShaderVariantCatalog.FromRows(1, 0, ["POINT", "SHADOWS_CUBE"], ["POINT"], [1]);
        var original = new ShaderVariantCatalog.Pass("FORWARDBASE", "FORWARD", first);
        var inherited = new ShaderVariantCatalog.Pass("FORWARDBASE", "FORWARD", second);
        var additive = new ShaderVariantCatalog.Pass("FORWARDADD", "FORWARD", second);
        var complete = ShaderVariantCatalog.CompleteGroups([original, inherited, additive]);
        Check(complete.Length == 2 && complete[0].Domains.SequenceEqual([first, second]));
        Check(first.Fixed.SequenceEqual(["DIRECTIONAL"]) && first.Rows.SequenceEqual(["1", "3"]));
        var partial = ShaderVariantCatalog.CompleteGroups([original, inherited with { Domain = null }, additive]);
        Check(partial.Length == 1 && partial[0].LightMode == "FORWARDADD");
        Check(ShaderVariantCatalog.NormalizeMode(" ForwardBase ") == "FORWARDBASE");
        Check(ShaderVariantCatalog.NormalizeMode("CustomPipeline") is null);
    }

    private static void SplitPrograms()
    {
        const string error = "#error Original shader has no recovered variant";
        var source = new StringBuilder();
        var names = Enumerable.Range(0, 9).Select(index => "FIXTURE_" + index).ToArray();
        var branches = new List<ShaderProgramSplitter.Branch>();
        for (var mask = 0; mask < 511; mask++)
        {
            var condition = string.Join(" && ", names.Select((name, bit) =>
                ((mask & (1 << bit)) == 0 ? "!" : "") + $"defined({name})"));
            var start = source.Length;
            source.AppendLine().AppendLine("#pragma vertex vert").AppendLine("#pragma fragment frag");
            source.AppendLine("#include \"UnityCG.cginc\"").AppendLine($"float fixture_{mask};");
            branches.Add(new((ulong)mask, condition, start, source.Length));
        }
        var fallback = source.Length;
        source.AppendLine().AppendLine(error);
        var program = new ShaderProgramSplitter.Program(branches, fallback, source.Length, source.Length);
        var directory = Path.Combine(Path.GetTempPath(), "shader-split-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = source.ToString();
            var split = ShaderProgramSplitter.Split(original, program, names, directory, "fixture", 0);
            Check(split is not null && split.Includes.Length > 1);
            var references = split!.Source.Split('\n').Where(line => line.StartsWith("#include ", StringComparison.Ordinal)).ToArray();
            Check(split.Source.StartsWith("#pragma vertex vert\n#pragma fragment frag\n", StringComparison.Ordinal));
            Check(references.Length == split.Includes.Length);
            var guards = new List<string>();
            foreach (var include in split.Includes)
            {
                var text = File.ReadAllText(Path.Combine(directory, include.Path));
                Check(text.Contains(error, StringComparison.Ordinal) && !text.Contains("#pragma", StringComparison.Ordinal));
                guards.AddRange(text.Split('\n').Where(line => line.StartsWith("#if ", StringComparison.Ordinal)
                    || line.StartsWith("#elif ", StringComparison.Ordinal)).Select(line => line[(line.IndexOf(' ') + 1)..].TrimEnd()));
            }
            Check(guards.Count == branches.Count && guards.ToHashSet().SetEquals(branches.Select(branch => branch.Condition)));
            var withRelativeInclude = original.Replace("UnityCG.cginc", "LocalCG.cginc", StringComparison.Ordinal);
            Check(ShaderProgramSplitter.Split(withRelativeInclude, program, names, directory, "fixture", 1) is null);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static HashSet<ulong> Expand(string[] names, ShaderKeywordPlan plan)
    {
        ulong Mask(string name) => 1UL << Array.IndexOf(names, name);
        var result = new HashSet<ulong> { plan.Fixed.Aggregate(0UL, (mask, name) => mask | Mask(name)) };
        foreach (var group in plan.Groups)
        {
            IEnumerable<ulong> choices = group.Names.Select(Mask);
            if (group.Optional) choices = choices.Prepend(0UL);
            result = result.SelectMany(row => choices.Select(choice => choice | row)).ToHashSet();
        }
        return result;
    }

    private static void InstancingBindings()
    {
        const string body = """
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            CBUFFER_START(UnityDrawCallInfo)
                int unity_BaseInstanceID;
            CBUFFER_END
            CBUFFER_START(UnityInstancing_PerDraw0)
                float4x4 unity_ObjectToWorldArray[500];
                float4x4 unity_WorldToObjectArray[500];
            CBUFFER_END
            CBUFFER_START(UnityDrawCallInfo)
            CBUFFER_END
            CBUFFER_START(UnityInstancing_PerDraw0)
            CBUFFER_END
            v2f vert(appdata_full v)
            {
                tmp7.y = v.sv_instanceid.x + unity_BaseInstanceID;
                tmp7.y = float1(int1(tmp7.y) << 2);
                o.position = unity_ObjectToWorldArray[((int)(tmp7.y) + 3) / 4]._m03_m13_m23_m33;
                o.normal = unity_WorldToObjectArray[((int)(tmp7.y) + 2) / 4]._m02_m12_m22;
                o.sv_instanceid.x = v.sv_instanceid.x;
                return o;
            }
            """;
        const string disabled = "#if !defined(INSTANCING_ON)";
        const string enabled = "#elif defined(INSTANCING_ON)";
        const string fallback = "#else\n#error Original shader has no recovered variant\n#endif";
        var repaired = Repair(body);
        Check(repaired is not null && repaired.Bindings is { VariantCount: 1, InstanceIdReads: 2,
            ObjectToWorldReads: 1, WorldToObjectReads: 1 });
        Check(repaired!.Source.StartsWith($"{disabled}\n{body}\n{enabled}", StringComparison.Ordinal));
        var active = repaired.Source[(repaired.Source.IndexOf(enabled, StringComparison.Ordinal) + enabled.Length)..];
        Check(!active.Contains("CBUFFER_START(UnityDrawCallInfo)", StringComparison.Ordinal));
        Check(!active.Contains("CBUFFER_START(UnityInstancing_PerDraw0)", StringComparison.Ordinal));
        Check(active.Contains("UNITY_GET_INSTANCE_ID(v) + unity_BaseInstanceID", StringComparison.Ordinal));
        Check(active.Contains("unity_Builtins0Array[((int)(tmp7.y) + 3) / 4].unity_ObjectToWorldArray._m03_m13_m23_m33", StringComparison.Ordinal));
        Check(active.Contains("NATIVEH3VR_INSTANCE_INVERSE_ARRAY[((int)(tmp7.y) + 2) / 4].unity_WorldToObjectArray._m02_m12_m22", StringComparison.Ordinal));
        Check(active.EndsWith(fallback, StringComparison.Ordinal));
        Check(Repair(body.Replace("[500]", "[499]", StringComparison.Ordinal)) is null);
        Check(Repair(body.Replace("UnityCG.cginc", "LocalCG.cginc", StringComparison.Ordinal)) is null);
        Check(Repair(body.Replace("((int)(tmp7.y) + 3) / 4", "unrelatedIndex", StringComparison.Ordinal)) is null);

        InstancingBindingRepair.Result? Repair(string enabledBody)
        {
            var second = disabled.Length + body.Length + 2;
            var tail = second + enabled.Length + enabledBody.Length + 2;
            var source = $"{disabled}\n{body}\n{enabled}\n{enabledBody}\n{fallback}";
            var pass = new ShaderProgramSplitter.Program(
                [new(0, disabled["#if ".Length..], disabled.Length, second),
                    new(1, enabled["#elif ".Length..], second + enabled.Length, tail)],
                tail + "#else".Length, source.Length - "#endif".Length, source.Length);
            return InstancingBindingRepair.Rewrite(source, 0, pass, new() { ["INSTANCING_ON"] = 1 });
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Keyword plan changed the expected variant relation.");
    }
}
