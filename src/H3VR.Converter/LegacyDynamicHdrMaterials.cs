using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static class LegacyDynamicHdrMaterials
{
    internal const string TagName = "NativeH3VRLegacyHdrColor";
    internal sealed record ShaderOrigin(string Source, string SourceSha256, string SerializedName, long PathId, string AssetPath);
    internal sealed record ExtractedShader(string Guid, string ExportPath, string CollectionName, string[] BundleChain, long PathId);
    internal sealed record StockBinding(string MaterialSha256, ShaderOrigin Shader, string Property, string[] Textures);

    // The shader identity is the original serialized object. AssetRipper assigns a new project GUID each run.
    private static readonly ShaderOrigin PistolCarPaint = new(
        "StreamingAssets/assets_resources_objectids_weaponry_pistols", "f774cf63820b7e11d0a8e3e0435a9871811a2bb7d439093c1eb7de2f4bd6af45",
        "CAB-726b635c55aa8767eb0c3c9ea4a4ea63", 137405621934460710, "Assets/Shader/Alloy_Car Paint_4.shader");
    private static readonly ShaderOrigin AlloyCore = new(
        "globalgamemanagers.assets", "00488ca28993fbb439598deff5399dbfe45698d4d67abb69e498c17a896e55b0",
        "globalgamemanagers.assets", 42, "Assets/Shader/Alloy_Core.shader");
    private static readonly ShaderOrigin AlloyAdditiveSoft = new(
        "globalgamemanagers.assets", "00488ca28993fbb439598deff5399dbfe45698d4d67abb69e498c17a896e55b0",
        "globalgamemanagers.assets", 94, "Assets/Shader/Alloy_Particles_Additive (Soft).shader");
    private static readonly ShaderOrigin AlloyUnlit = new(
        "globalgamemanagers.assets", "00488ca28993fbb439598deff5399dbfe45698d4d67abb69e498c17a896e55b0",
        "globalgamemanagers.assets", 97, "Assets/Shader/Alloy_Unlit.shader");
    private static readonly ShaderOrigin AttachmentUnlit = new(
        "StreamingAssets/assets_resources_objectids_weaponry_attachments", "bacc0855f77b8888ee09b75c1fbc5e3f296b4dda0721051b63fd07701b2bfc68",
        "CAB-7e606817e71b728d724c09231f65a661", -8392369584805702168, "Assets/Shader/Alloy_Unlit_10.shader");
    private static readonly ShaderOrigin PistolUnlit = new(
        "StreamingAssets/assets_resources_objectids_weaponry_pistols", "f774cf63820b7e11d0a8e3e0435a9871811a2bb7d439093c1eb7de2f4bd6af45",
        "CAB-726b635c55aa8767eb0c3c9ea4a4ea63", -8392369584805702168, "Assets/Shader/Alloy_Unlit_13.shader");
    private static readonly ShaderOrigin ToyToolsUnlit = new(
        "StreamingAssets/assets_resources_objectids_toytargetstools", "6da685d361c685af69d1c6baec69dc8771b89c8a81b3b6fd450dbb180af966cc",
        "CAB-a13a09ce97b4e4ca6dc5e2ba6ac2883c", -8392369584805702168, "Assets/Shader/Alloy_Unlit_8.shader");
    private static readonly ShaderOrigin MedigunBeam = new(
        "StreamingAssets/assets_resources_objectids_weaponry_mf2", "d71921523ee596d24761b77c01d0b7fa2b88ef20d1d258fb98b299a3bb293b11",
        "CAB-e13288a5e6d00242bdeebcf8ceffd37e", -6085052595785732060, "Assets/Shader/MedigunBeam.shader");

    // These materials reach named Material.SetColor calls in the original player.
    // Hash the full saved data after resolving its references to extraction paths.
    private static readonly Dictionary<string, StockBinding> Bindings = new(StringComparer.Ordinal)
    {
        ["Material/OmniDisc_BlueStar.mat"] = new("bd42879dcab54d77aeb738c2e1969f375786cb02f6ee9aeec32cea68ecd48699",
            AlloyUnlit, "_Color", ["Texture2D/BlueStar.png"]),
        ["Material/OmniDisc_FiftyFifty.mat"] = new("075e8f4c02aa8b89fd6e2137ee61b73a6da959ca57d2366a37ca86adb32b957f",
            AlloyUnlit, "_Color", ["Texture2D/FiftyFiftyRedBorder.png"]),
        ["Material/OmniDisc_RedX.mat"] = new("d8a0fd558346fb72efa2c1fa1cfccb224b9e0fca2d6b4021036ea239419ed8a2",
            AlloyUnlit, "_Color", ["Texture2D/RedX.png"]),
        ["Material/OmniDisc_Shield.mat"] = new("6ebec793624ae939c897810837fbb642cc21e7f75aeaedbc2056a111204e0797",
            AlloyUnlit, "_Color", ["Texture2D/ShieldDropShadow.png"]),
        ["Material/OmniDisc_ThreeRingBull.mat"] = new("dbb59bbd466e837473b854cd52c402d1e07f7b04f5ea8f3228fd4a50a6f0ae10",
            AlloyUnlit, "_Color", ["Texture2D/ThreeRingBlackCenter.png"]),
        ["Material/OmniShape_Default.mat"] = new("40f057ed6b313d571a8300b18c95d473e84dd9620e5d4c0fa404c472f1f85ae1",
            AlloyUnlit, "_Color", []),
        ["Material/Snowbluster.mat"] = new("5085b0084ec6313d11e64292850d1d5d5208b38396fe08211987f42fc2460fc2",
            AlloyAdditiveSoft, "_TintColor", ["Texture2D/Snow_8x8_04.png"]),
        ["Material/StudioLight_Glow.mat"] = new("89c9fddab3356e374211c328498e4fbbca939b3dce88b85c3732bc61753e793d",
            ToyToolsUnlit, "_Color", []),
        ["Material/TAHReticle_HealthBar.mat"] = new("06d4fbf1865529357dafe17717a55c0914b9126b4c7cece9585789638d17e2e2",
            AlloyUnlit, "_EmissionColor", ["Texture2D/TAHReticle_HealthMask.png", "Texture2D/TAHReticle_HealthPip.png"]),
        ["Material/TAHReticle_HealthBar_NV.mat"] = new("31975e5be87a12009303e90efb8432c9c4c22ad54e23f8e8e247afa74edf4eaa",
            AlloyUnlit, "_EmissionColor", ["Texture2D/TAHReticle_HealthMask.png", "Texture2D/TAHReticle_HealthPip.png"]),
        ["Material/mf_fx_healBeam_A.mat"] = new("580f4fe65f99d815391ace47b1a50ff9ec7ff6936aca29f4e0921b21e59bcb5c",
            MedigunBeam, "_Color", ["Texture2D/mf_fx_healBeam_A_emissive.png", "Texture2D/mf_fx_healBeam_A_mask.png"]),
        ["Material/mtl_ENdingScreen_0.mat"] = new("c06ec20a0bee956c8ac42504a81cc7efd53eafc30cb2090eecec2355ffeb77f3",
            AlloyCore, "_EmissionColor", ["Texture2D/Nar.png", "Texture2D/StrangeNoise3.png"]),
        ["Material/mtl_LAPD2019Glow_1.mat"] = new("2b6eb6a8a2f6509b9737baf14e12fc556265224248a2c1ec0d431a1faedb73af",
            PistolUnlit, "_Color", []),
        ["Material/mtl_LAPD2019Glow_Red_0.mat"] = new("3f169e459b91db0a6a97391f8f132ba49a133206db2f0cc07bc676c8c50bb44f",
            PistolUnlit, "_Color", []),
        ["Material/mtl_SlicerEye.mat"] = new("b430bc2bcc9483427141c9873fa244d043a99559732a1267b38bd0a701cb4490",
            AlloyUnlit, "_Color", []),
        ["Material/openPBR_shader1_1.mat"] = new("914f53e4709303fb8d7e5b7ad4f4c917ef25698cd8d5731ea917cfe84aa5ce57",
            AttachmentUnlit, "_EmissionColor", ["Texture2D/AmmoCounter_Digits_1.png"]),
        ["Material/phong7_8.mat"] = new("44221cf1801d1843f186327c49998c13d03d434b2ea391c22bceea9970266f23",
            PistolCarPaint, "_EmissionColor", ["Texture2D/TAHReticle_HealthMask_1.png", "Texture2D/cPistol_AlloyPM.png", "Texture2D/cPistol_occ.png"]),
        ["Material/unlit.mat"] = new("4b1d15fa644e0773512e57bd09e45f9557c18763935ef486b70aa78ef31adb00",
            AlloyUnlit, "_Color", []),
    };

    private static readonly Regex TagMap = new(
        @"(?m)^  stringTagMap:[ \t]*(?<empty>\{\})?[ \t]*(?<newline>\r?\n)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ExistingTag = new(
        @"(?m)^    " + TagName + @":[ \t]*(?<value>\S+)[ \t]*\r?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GuidReference = new(@"\bguid: ([a-fA-F0-9]{32})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MetaGuid = new(@"(?m)^guid: ([a-fA-F0-9]{32})\r?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static StockBinding? Lookup(string materialPath) => Bindings.GetValueOrDefault(materialPath);

    internal static string MaterialHash(string authored, IReadOnlyDictionary<string, string> references)
    {
        string normalized = GuidReference.Replace(authored, match =>
        {
            string guid = match.Groups[1].Value;
            if (!references.TryGetValue(guid, out var relative))
                throw new InvalidDataException("Unrecognized stock material asset reference " + guid);
            return "guid: " + relative;
        });
        return SafeFiles.HashText(normalized);
    }

    internal static void Verify(StockBinding binding, string path, string materialHash,
        ExtractedShader shader, string shaderSourceHash, HashSet<string> hdrColors)
    {
        var source = binding.Shader;
        string[] bundle = source.Source.Contains('/')
            ? [Path.GetFileName(source.Source), "GameBundle"] : ["GameBundle"];
        if (binding.MaterialSha256 != materialHash || shaderSourceHash != source.SourceSha256 ||
            shader.ExportPath != source.AssetPath || shader.PathId != source.PathId ||
            !shader.CollectionName.Equals(source.SerializedName, StringComparison.OrdinalIgnoreCase) ||
            !shader.BundleChain.SequenceEqual(bundle, StringComparer.OrdinalIgnoreCase) || !hdrColors.Contains(binding.Property))
            throw new InvalidDataException("Stock dynamic HDR material identity or shader property changed: " + path);
    }

    internal sealed class ExtractionVerification
    {
        private sealed record ShaderMap(int SchemaVersion, ExtractedShader[] Shaders);

        private readonly ConversionConfig config;
        private readonly StepReceipt extraction;
        private readonly Dictionary<string, ExtractedShader> shaders;
        private readonly Dictionary<string, string> sourceHashes = new(StringComparer.Ordinal);

        public ExtractionVerification(ConversionConfig config, StepReceipt extraction)
        {
            this.config = config;
            this.extraction = extraction;
            string path = Path.Combine(Path.GetDirectoryName(config.Export)!, "AuxiliaryFiles", "shader-identity-v2.json");
            var map = JsonSerializer.Deserialize<ShaderMap>(ReadVerified(path), ConversionConfig.Json);
            if (map?.SchemaVersion != 1 || map.Shaders.Length == 0)
                throw new InvalidDataException("Expected a verified source shader identity map for stock HDR materials.");
            shaders = map.Shaders.ToDictionary(shader => shader.Guid, StringComparer.OrdinalIgnoreCase);
        }

        public void VerifyMaterial(StockBinding binding, string relative, string authored,
            string shaderGuid, HashSet<string> hdrColors)
        {
            if (!shaders.TryGetValue(shaderGuid, out var shader))
                throw new InvalidDataException("Missing original shader identity for " + relative);

            var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in binding.Textures.Prepend(binding.Shader.AssetPath["Assets/".Length..]))
            {
                string meta = SafeFiles.Inside(config.Export, "Assets/" + path + ".meta");
                var matches = MetaGuid.Matches(Encoding.UTF8.GetString(ReadVerified(meta)));
                if (matches.Count != 1 || !references.TryAdd(matches[0].Groups[1].Value, path))
                    throw new InvalidDataException("Missing or ambiguous stock material reference " + path);
            }

            string source = binding.Shader.Source;
            if (!sourceHashes.TryGetValue(source, out var sourceHash))
            {
                sourceHash = SafeFiles.Hash(SafeFiles.Inside(config.GameData, source));
                sourceHashes.Add(source, sourceHash);
            }
            Verify(binding, relative, MaterialHash(authored, references), shader, sourceHash, hdrColors);
        }

        private byte[] ReadVerified(string path)
        {
            if (!extraction.Files.TryGetValue(path, out var expected) || SafeFiles.Canonical(path) != path)
                throw new InvalidDataException("Stock material reference is missing from the extraction receipt " + path);
            var bytes = File.ReadAllBytes(path);
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != expected)
                throw new InvalidDataException("Stock material reference differs from the extraction receipt " + path);
            return bytes;
        }
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
