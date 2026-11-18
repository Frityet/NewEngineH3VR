using System.Numerics;

namespace NativeH3VR.ShaderOverlay;

internal sealed record ShaderKeywordGroup(string[] Names, bool Optional);

internal sealed record ShaderKeywordPlan(string[] Fixed, ShaderKeywordGroup[] Groups, bool Exact)
{
    private static readonly string[][] LegacyLightingModes =
    [
        ["DIRECTIONAL", "DIRECTIONAL_COOKIE", "POINT", "POINT_COOKIE", "SPOT"],
        ["SHADOWS_CUBE", "SHADOWS_DEPTH"]
    ];

    internal static ShaderKeywordPlan Create(string[] names, IReadOnlyCollection<ulong> variants)
    {
        if (variants.Count == 0) throw new ArgumentException("Expected at least one original shader variant.");
        var declared = variants.Aggregate(0UL, (mask, row) => mask | row);
        var fixedMask = variants.Aggregate(declared, (mask, row) => mask & row);
        var variable = Bits(declared & ~fixedMask).ToArray();
        var components = variable.ToList();
        for (var i = 0; i < variable.Length; i++)
        {
            foreach (var right in variable.Skip(i + 1))
            {
                var left = variable[i];
                var first = components.FindIndex(mask => (mask & left) != 0);
                var second = components.FindIndex(mask => (mask & right) != 0);
                if (first == second || Independent(left, right, variants)) continue;
                components[first] |= components[second];
                components.RemoveAt(second);
            }
        }

        var exact = true;
        var tuples = 1;
        var domains = new List<(ulong Mask, bool Optional)>();
        foreach (var component in components)
        {
            var projection = variants.Select(row => row & component).ToHashSet();
            if (tuples > variants.Count / projection.Count) exact = false;
            else tuples *= projection.Count;
            if (projection.All(row => BitOperations.PopCount(row) <= 1))
            {
                domains.Add((component, projection.Contains(0)));
                continue;
            }
            exact = false;
            var remainder = component;
            foreach (var modeNames in LegacyLightingModes)
            {
                var mode = NamesMask(names, modeNames) & remainder;
                if (BitOperations.PopCount(mode) < 2 || !variants.All(row => BitOperations.PopCount(row & mode) == 1))
                    continue;
                domains.Add((mode, false));
                remainder &= ~mode;
            }
            domains.AddRange(Bits(remainder).Select(bit => (bit, true)));
        }
        exact &= tuples == variants.Count;

        // Keep the first occurrence of each option in the original program table.
        var groups = domains.OrderBy(group => BitOperations.TrailingZeroCount(group.Mask))
            .Select(group => new ShaderKeywordGroup(variants.Select(row => row & group.Mask)
                .SelectMany(Bits).Distinct().Select(bit => names[BitOperations.TrailingZeroCount(bit)]).ToArray(),
                group.Optional)).ToArray();
        return new ShaderKeywordPlan(Bits(fixedMask).Select(bit => names[BitOperations.TrailingZeroCount(bit)]).ToArray(), groups, exact);
    }

    internal bool ChangesDeclarations => Fixed.Length != 0 || Groups.Any(group => !group.Optional || group.Names.Length > 1);

    internal IEnumerable<string> Directives()
    {
        foreach (var name in Fixed) yield return $"#define {name} 1";
        foreach (var group in Groups)
        {
            var directive = !Exact && group.Optional ? "shader_feature" : "multi_compile";
            yield return $"#pragma {directive} {(group.Optional ? "__ " : "")}{string.Join(" ", group.Names)}";
        }
    }

    private static ulong NamesMask(string[] names, string[] group)
    {
        var result = 0UL;
        for (var index = 0; index < names.Length; index++)
            if (group.Contains(names[index], StringComparer.Ordinal)) result |= 1UL << index;
        return result;
    }

    private static bool Independent(ulong left, ulong right, IReadOnlyCollection<ulong> variants)
    {
        var seen = 0;
        foreach (var row in variants)
        {
            seen |= 1 << (((row & left) != 0 ? 1 : 0) | ((row & right) != 0 ? 2 : 0));
            if (seen == 15) return true;
        }
        return false;
    }

    private static IEnumerable<ulong> Bits(ulong mask)
    {
        while (mask != 0)
        {
            var bit = 1UL << BitOperations.TrailingZeroCount(mask);
            yield return bit;
            mask &= ~bit;
        }
    }
}
