using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static class LegacyDynamicHdrMaterials
{
    internal const string TagName = "NativeH3VRLegacyHdrColor";
    internal sealed record StockBinding(string MaterialSha256, string ShaderGuid, string Property);

    // These assets reach named Material.SetColor calls in the original Steam
    // player. The full original material hash and shader GUID distinguish
    // similar stock materials and unrelated mods.
    private static readonly Dictionary<string, StockBinding> Bindings = new(StringComparer.Ordinal)
    {
        ["Material/OmniDisc_BlueStar.mat"] = new("f0ef78b5818ad108ffd8eff73a3c2661cd6b76373763d0292b35d9372b1c896f", "e876d5cba82bc13408a852d905a6a81d", "_Color"),
        ["Material/OmniDisc_FiftyFifty.mat"] = new("db4920e589fbb49ad7f77014b3ad6dd59e3eab39100e2d0f042eaaaf0f100a36", "e876d5cba82bc13408a852d905a6a81d", "_Color"),
        ["Material/OmniDisc_RedX.mat"] = new("d677ff583647392c11f3451b72b17942197ac4b09209f91e37158b10f21c6f82", "e876d5cba82bc13408a852d905a6a81d", "_Color"),
        ["Material/OmniDisc_Shield.mat"] = new("ec11af41b8985646d4b899fedc950e440b92a88b1c402380021ce79ff0708b21", "e876d5cba82bc13408a852d905a6a81d", "_Color"),
        ["Material/OmniDisc_ThreeRingBull.mat"] = new("794f178e7282e7a49ece78c404e50e48b30462dea376011eb0c729802e4dc65e", "e876d5cba82bc13408a852d905a6a81d", "_Color"),
        ["Material/OmniShape_Default.mat"] = new("9e0f93cb3ae527127a7de470eb514e862a7c355de714f8e0a263147c58bb5829", "e876d5cba82bc13408a852d905a6a81d", "_Color"),
        ["Material/Snowbluster.mat"] = new("42527cacac0de2b3b8e03667c48bd5f373862aeccbdb468dc8347918d2387b34", "32c86e03848e37f4fa21488945782acb", "_TintColor"),
        ["Material/StudioLight_Glow.mat"] = new("ef131b129c7ad79d306d428773119063dff43bab818fa3e54ea6f728363c83f1", "ce7f36aedb4985443a02c0b67a848bee", "_Color"),
        ["Material/TAHReticle_HealthBar.mat"] = new("178ef9e34cd2fd2832f1237611001c1c85bb20473ee22057fcf51db5ed809c48", "e876d5cba82bc13408a852d905a6a81d", "_EmissionColor"),
        ["Material/TAHReticle_HealthBar_NV.mat"] = new("05114fa4e31e38c2212fd4b97beb15bc2e1e6701b4e96c0f9c0c558c7ddcdfcd", "e876d5cba82bc13408a852d905a6a81d", "_EmissionColor"),
        ["Material/mf_fx_healBeam_A.mat"] = new("9d7c086f41ed0522ed29517c5cf8593c3f808c700fe48ea7a5d38dc9b3e47914", "4b45528588e261c4e88a378aba376509", "_Color"),
        ["Material/mtl_ENdingScreen_0.mat"] = new("055e1889041b5d6add9846dad3d06336e6010e2dc374cb12c66b2dc14555d11c", "137591c054cb683499d266769d94972c", "_EmissionColor"),
        ["Material/mtl_LAPD2019Glow_1.mat"] = new("666604e10ca122483fee8de7794ca750ea8bf9a339c52c0b41168c132f80e625", "5d83f835d9d2986418d92f13da0e6ee9", "_Color"),
        ["Material/mtl_LAPD2019Glow_Red_0.mat"] = new("9a79234978294dc01ccafe65a6e48cf511b9f6fb0bd5432c28dcac882ce692d7", "5d83f835d9d2986418d92f13da0e6ee9", "_Color"),
        ["Material/mtl_SlicerEye.mat"] = new("b6bb247e3c95a595f0a90fc98ada1729cea3767ca5f65dd58a9ed126ab36a73d", "e876d5cba82bc13408a852d905a6a81d", "_Color"),
        ["Material/openPBR_shader1_1.mat"] = new("9398140fe7e68c5dbdb40cedd5ac187cd56f7442129c6b9682c32f6072607eae", "d3dd654f8dd7a714387d99e5578073d3", "_EmissionColor"),
        ["Material/phong7_8.mat"] = new("35eba04ca48dc014fba9007888e111eae3dfb60c9dc82ad5a5c8ba8ffcadfd90", "001eaf593772ef74ab6d8f6f06d960bf", "_EmissionColor"),
        ["Material/unlit.mat"] = new("8e7ad3d6e0b460f2146f77ff0752b92fe5fd978e176b090f074269b7156d5cea", "e876d5cba82bc13408a852d905a6a81d", "_Color"),
    };

    private static readonly Regex TagMap = new(
        @"(?m)^  stringTagMap:[ \t]*(?<empty>\{\})?[ \t]*(?<newline>\r?\n)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ExistingTag = new(
        @"(?m)^    " + TagName + @":[ \t]*(?<value>\S+)[ \t]*\r?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static StockBinding? Lookup(string materialPath) => Bindings.GetValueOrDefault(materialPath);

    internal static void Verify(StockBinding binding, string path, string originalHash,
        string shaderGuid, HashSet<string> hdrColors)
    {
        if (binding.MaterialSha256 != originalHash || binding.ShaderGuid != shaderGuid ||
            !hdrColors.Contains(binding.Property))
            throw new InvalidDataException("Stock dynamic HDR material identity or shader property changed: " + path);
    }

    internal static string AddTag(string material, StockBinding binding)
    {
        var map = TagMap.Matches(material);
        if (map.Count != 1)
            throw new InvalidDataException("Expected one serialized stock material tag map.");
        var anchor = map[0];
        int afterAnchor = anchor.Index + anchor.Length;
        int mapEnd = afterAnchor;
        while (mapEnd < material.Length && material.AsSpan(mapEnd).StartsWith("    ", StringComparison.Ordinal))
        {
            int newlineIndex = material.IndexOf('\n', mapEnd);
            mapEnd = newlineIndex < 0 ? material.Length : newlineIndex + 1;
        }

        var existing = ExistingTag.Matches(material[afterAnchor..mapEnd]);
        if (existing.Count == 1 && existing[0].Groups["value"].Value == binding.Property)
            return material;
        if (existing.Count != 0)
            throw new InvalidDataException("Conflicting stock dynamic HDR material tag.");

        string newline = anchor.Groups["newline"].Value;
        string entry = "    " + TagName + ": " + binding.Property + newline;
        return anchor.Groups["empty"].Success
            ? material[..anchor.Index] + "  stringTagMap:" + newline + entry + material[afterAnchor..]
            : material.Insert(afterAnchor, entry);
    }
}
