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

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Keyword plan changed the expected variant relation.");
    }
}
