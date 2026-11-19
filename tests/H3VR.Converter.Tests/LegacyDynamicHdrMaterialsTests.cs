using H3VR.Converter;

namespace H3VR.Converter.Tests;

internal static class LegacyDynamicHdrMaterialsTests
{
    internal static (string Name, Action Run)[] Cases() =>
    [
        ("stock dynamic HDR tags require the exact source and verified property", MatchProvenance),
        ("stock dynamic HDR tags preserve other tags and reimport idempotently", AddToTagMap)
    ];

    private static void MatchProvenance()
    {
        const string path = "Material/Snowbluster.mat";
        var stock = LegacyDynamicHdrMaterials.Lookup(path) ?? throw new Exception("Missing stock identity");
        Require(stock.Property == "_TintColor");
        Require(LegacyDynamicHdrMaterials.Lookup("Other/Snowbluster.mat") is null);
        LegacyDynamicHdrMaterials.Verify(stock, path, stock.MaterialSha256, stock.ShaderGuid, ["_TintColor"]);
        Reject(() => LegacyDynamicHdrMaterials.Verify(stock, path, new string('0', 64), stock.ShaderGuid, ["_TintColor"]));
        Reject(() => LegacyDynamicHdrMaterials.Verify(stock, path, stock.MaterialSha256, new string('0', 32), ["_TintColor"]));
        Reject(() => LegacyDynamicHdrMaterials.Verify(stock, path, stock.MaterialSha256, stock.ShaderGuid, ["_Color"]));
    }

    private static void AddToTagMap()
    {
        var stock = LegacyDynamicHdrMaterials.Lookup("Material/TAHReticle_HealthBar.mat") ??
            throw new Exception("Missing stock identity");
        const string begin = "Material:\r\n  stringTagMap:\r\n";
        const string end = "  disabledShaderPasses: []\r\n";
        const string tag = "    NativeH3VRLegacyHdrColor: _EmissionColor\r\n";
        const string transparent = "    RenderType: Transparent\r\n";
        var withTags = LegacyDynamicHdrMaterials.AddTag(begin + transparent + end, stock);
        Require(withTags == begin + tag + transparent + end);
        Require(LegacyDynamicHdrMaterials.AddTag(withTags, stock) == withTags);
        Require(LegacyDynamicHdrMaterials.AddTag("Material:\r\n  stringTagMap: {}\r\n" + end, stock) == begin + tag + end);
        Reject(() => LegacyDynamicHdrMaterials.AddTag(begin + tag.Replace("_EmissionColor", "_Color") + end, stock));
        Reject(() => LegacyDynamicHdrMaterials.AddTag("Material:\r\n" + end, stock));
    }

    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected invalid stock dynamic HDR provenance to be rejected.");
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Stock dynamic HDR material assertion failed.");
    }
}
