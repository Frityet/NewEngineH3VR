using H3VR.Converter;

namespace H3VR.Converter.Tests;

internal static class LegacyDynamicHdrMaterialsTests
{
    internal static (string Name, Action Run)[] Cases() =>
    [
        ("stock dynamic HDR tags require the exact source and verified property", MatchProvenance),
        ("stock material hashes identify the same references across independent extractions", MatchStableReferences),
        ("stock dynamic HDR tags preserve other tags and reimport idempotently", AddToTagMap)
    ];

    private static void MatchProvenance()
    {
        const string path = "Material/Snowbluster.mat";
        var stock = LegacyDynamicHdrMaterials.Lookup(path) ?? throw new Exception("Missing stock identity");
        Require(stock.Property == "_TintColor");
        Require(LegacyDynamicHdrMaterials.Lookup("Other/Snowbluster.mat") is null);
        var source = stock.Shader;
        var shader = new LegacyDynamicHdrMaterials.ExtractedShader(new string('a', 32), source.AssetPath,
            source.SerializedName, ["GameBundle"], source.PathId);
        LegacyDynamicHdrMaterials.Verify(stock, path, stock.MaterialSha256, shader, source.SourceSha256, ["_TintColor"]);
        LegacyDynamicHdrMaterials.Verify(stock, path, stock.MaterialSha256, shader with { Guid = new string('b', 32) },
            source.SourceSha256, ["_TintColor"]);
        Reject(() => LegacyDynamicHdrMaterials.Verify(stock, path, new string('0', 64), shader, source.SourceSha256, ["_TintColor"]));
        Reject(() => LegacyDynamicHdrMaterials.Verify(stock, path, stock.MaterialSha256, shader with { PathId = 0 },
            source.SourceSha256, ["_TintColor"]));
        Reject(() => LegacyDynamicHdrMaterials.Verify(stock, path, stock.MaterialSha256, shader, new string('0', 64), ["_TintColor"]));
        Reject(() => LegacyDynamicHdrMaterials.Verify(stock, path, stock.MaterialSha256, shader, source.SourceSha256, ["_Color"]));
    }

    private static void MatchStableReferences()
    {
        string first = new('a', 32);
        string second = new('b', 32);
        const string asset = "Shader/Stock.shader";
        string Material(string guid) => "  m_Shader: {fileID: 4800000, guid: " + guid + ", type: 3}\n";
        var original = LegacyDynamicHdrMaterials.MaterialHash(Material(first), new Dictionary<string, string> { [first] = asset });
        var reexport = LegacyDynamicHdrMaterials.MaterialHash(Material(second), new Dictionary<string, string> { [second] = asset });
        var wrongAsset = LegacyDynamicHdrMaterials.MaterialHash(Material(second),
            new Dictionary<string, string> { [second] = "Shader/Unrelated.shader" });
        Require(original == reexport && original != wrongAsset);
        Reject(() => LegacyDynamicHdrMaterials.MaterialHash(Material(second), new Dictionary<string, string>()));
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
