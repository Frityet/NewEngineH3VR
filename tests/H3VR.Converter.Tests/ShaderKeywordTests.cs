using NativeH3VR.ShaderOverlay;

namespace H3VR.Converter.Tests;

internal static class ShaderKeywordTests
{
    public static IEnumerable<(string, Action)> Cases()
    {
        yield return ("exclusive variant directives describe exactly the original product", ExactProduct);
        yield return ("pairwise independence does not certify a missing higher-order variant", Parity);
        yield return ("irregular lighting retains each original row when restoring the required mode", IrregularLighting);
        yield return ("a generated variant guard must assign each declared keyword once", CompleteGuard);
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

    private static void CompleteGuard()
    {
        var positions = new Dictionary<string, ulong> { ["A"] = 1, ["B"] = 2 };
        Check(KeywordRepair.TryReadGuard("defined(A) && !defined(B)", positions, out var mask) && mask == 1);
        Check(!KeywordRepair.TryReadGuard("defined(A)", positions, out _));
        Check(!KeywordRepair.TryReadGuard("defined(A) && !defined(A)", positions, out _));
        Check(!KeywordRepair.TryReadGuard("defined(A) && defined(B) && defined(C)", positions, out _));
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
