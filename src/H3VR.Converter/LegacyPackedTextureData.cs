using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AssetsTools.NET.Extra;

namespace H3VR.Converter;

/// <summary>Restores stock BC3 bytes where a packed normal is also sampled as ordinary Alloy mask data.</summary>
internal static class LegacyPackedTextureData
{
    private const string SteamVersion = "5.6.7f1";
    private const int Dxt5 = 12;
    private const int NormalDxt5 = 3;
    private const RegexOptions Line = RegexOptions.Multiline | RegexOptions.CultureInvariant;
    private static readonly Regex GuidField = new(@"^guid: ([0-9a-f]{32})\r?$", Line);
    private static readonly Regex MaterialShader = new(@"^  m_Shader: \{fileID: 4800000, guid: ([0-9a-f]{32}), type: 3\}\r?$", Line);
    private static readonly Regex MaterialName = new(@"^  m_Name: ([^\r\n]+)", Line);
    private static readonly Regex SpecTexture = new(
        @"^      (?:- )?_SpecTex:\r?\n        m_Texture: \{fileID: 2800000, guid: (?<guid>[0-9a-f]{32}), type: (?<refType>3)\}", Line);
    private static readonly Regex NormalImporter = new(@"^  textureType: 1\r?$", Line);

    // The stock texture is sampled as a mask by SilverBulletCrater. Other
    // packed normals assigned to _SpecTex use the main texture's roughness recipe.
    private const string SourceName = "Sand_CoarseClumpySand_2k_n";
    private const string SourceFile = "sharedassets16.assets";
    private const long SourceId = 330;
    private const string SourceSha256 = "b7edc51bf50f4c397f790eefb825d46b34f351d61effefd1408b0cdc07109c1a";
    private static readonly Regex RoughnessRecipe = new(@"^      (?:- )?_MainRoughnessSource: 0\r?$", Line);
    private static readonly Regex AlphaSmoothness = new(
        @"^  m_ShaderKeywords:[^\r\n]*\b_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A\b", Line);

    internal sealed record PackedTexture(int Width, int Height, int Mips, int FilterMode, int Aniso, float MipBias,
        int Wrap, byte[] Bytes);
    private sealed record MaterialBinding(string Path, string Text, Match Reference);

    internal static int Stage(string source, string assets, string classDatabase)
    {
        var textureDirectory = Path.Combine(assets, "Texture2D");
        if (!Directory.Exists(textureDirectory)) return 0;
        var normals = Directory.EnumerateFiles(textureDirectory, SourceName + "*.png.meta")
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(item => NormalImporter.IsMatch(item.Text))
            .ToDictionary(item => GuidField.Match(item.Text).Groups[1].Value, item => item.Path, StringComparer.Ordinal);
        var shaderGuids = new HashSet<string>(StringComparer.Ordinal);
        var bindings = new List<MaterialBinding>();
        var paths = SafeFiles.Files(assets).ToArray();
        foreach (var shader in paths.Where(path => path.EndsWith(".shader", StringComparison.Ordinal) && File.Exists(path + ".meta")))
        {
            using var text = File.OpenText(shader);
            string? header;
            do { header = text.ReadLine()?.Trim(); }
            while (header is not null && (header.Length == 0 || header.StartsWith("//", StringComparison.Ordinal)));
            if (header is not ("Shader \"Alloy/Core\" {" or "Shader \"Alloy/Core\"")) continue;
            string shaderGuid = GuidField.Match(File.ReadAllText(shader + ".meta")).Groups[1].Value;
            if (shaderGuid.Length > 0) shaderGuids.Add(shaderGuid);
        }
        foreach (var path in paths.Where(path => path.EndsWith(".mat", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(path);
            var reference = SpecTexture.Match(text);
            if (!reference.Success || !normals.TryGetValue(reference.Groups["guid"].Value, out string? normal))
                continue;
            if (MaterialName.Match(text).Groups[1].Value != "SilverBulletCrater" ||
                !shaderGuids.Contains(MaterialShader.Match(text).Groups[1].Value) ||
                !RoughnessRecipe.IsMatch(text) || AlphaSmoothness.IsMatch(text))
                continue;
            string textureName = Path.GetFileName(normal)[..^".png.meta".Length];
            if (IsExportedCopy(textureName, SourceName)) bindings.Add(new(path, text, reference));
        }
        if (bindings.Count == 0) return 0;

        var output = Path.Combine(assets, "NativeH3VR-Derived", "StockPackedTextures");
        Directory.CreateDirectory(output);
        var manager = new AssetsManager();
        manager.LoadClassPackage(classDatabase);
        var texture = ReadSource(manager, source);
        string alias = SourceName + "_StockPackedData";
        string guid = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            "NativeH3VR.StockPackedTexture:" + SourceSha256)))[..32];
        string asset = Path.Combine(output, alias + ".asset");
        File.WriteAllText(asset, TextureYaml(alias, texture));
        File.WriteAllText(asset + ".meta", $$"""
            fileFormatVersion: 2
            guid: {{guid}}
            NativeFormatImporter:
              externalObjects: {}
              mainObjectFileID: 2800000
              userData:
              assetBundleName:
              assetBundleVariant:

            """);
        foreach (MaterialBinding binding in bindings)
        {
            var originalGuid = binding.Reference.Groups["guid"];
            var originalType = binding.Reference.Groups["refType"];
            string rewritten = binding.Text[..originalGuid.Index] + guid +
                binding.Text[(originalGuid.Index + originalGuid.Length)..originalType.Index] + "2" +
                binding.Text[(originalType.Index + originalType.Length)..];
            File.WriteAllText(binding.Path, rewritten);
        }
        return bindings.Count;
    }

    internal static bool IsExportedCopy(string texture, string original) =>
        texture == original || Regex.IsMatch(texture, "^" + Regex.Escape(original) + @"_\d+$", RegexOptions.CultureInvariant);

    private static PackedTexture ReadSource(AssetsManager manager, string root)
    {
        try
        {
            string source = SafeFiles.Inside(root, SourceFile);
            var file = manager.LoadAssetsFile(source, false);
            PlayerEnlighten.RequireVersion(manager, file, SteamVersion);
            var info = file.file.GetAssetInfo(SourceId);
            if (info is null || info.GetTypeId(file.file) != (int)AssetClassID.Texture2D)
                throw new InvalidDataException($"Stock mask texture is absent from {SourceFile}.");
            var texture = manager.GetBaseField(file, info);
            if (texture["m_Name"].AsString != SourceName || texture["m_TextureFormat"].AsInt != Dxt5 ||
                texture["m_LightmapFormat"].AsInt != NormalDxt5 || texture["m_ColorSpace"].AsInt != 0 ||
                texture["m_ImageCount"].AsInt != 1 || texture["m_TextureDimension"].AsInt != 2)
                throw new InvalidDataException($"Unexpected stock DXT5nm metadata for {SourceName}.");
            int width = texture["m_Width"].AsInt;
            int height = texture["m_Height"].AsInt;
            int mips = texture["m_MipCount"].AsInt;
            int size = MipBytes(width, height, mips);
            byte[] data = texture["image data"].AsByteArray;
            if (data.Length == 0)
            {
                var stream = texture["m_StreamData"];
                string resource = stream["path"].AsString;
                if (resource != SourceFile + ".resS" || stream["size"].AsUInt != size)
                    throw new InvalidDataException($"Unexpected stream extent for {SourceName}.");
                using var read = File.OpenRead(SafeFiles.Inside(root, resource));
                read.Position = checked((long)stream["offset"].AsULong);
                data = new byte[size];
                read.ReadExactly(data);
            }
            if (data.Length != size || !Convert.ToHexStringLower(SHA256.HashData(data)).Equals(SourceSha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Stock packed texture payload changed for {SourceName}.");
            var sampling = texture["m_TextureSettings"];
            return new PackedTexture(width, height, mips, sampling["m_FilterMode"].AsInt,
                sampling["m_Aniso"].AsInt, sampling["m_MipBias"].AsFloat, sampling["m_WrapMode"].AsInt, data);
        }
        finally
        {
            manager.UnloadAllAssetsFiles();
        }
    }

    internal static int MipBytes(int width, int height, int mips)
    {
        if (width <= 0 || height <= 0 ||
            mips != 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(width, height)))
            throw new InvalidDataException("Expected a complete DXT5 mip chain.");
        int bytes = 0;
        for (int level = 0; level < mips; level++)
        {
            bytes = checked(bytes + checked((width + 3) / 4 * ((height + 3) / 4) * 16));
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        return bytes;
    }

    // Layout from a native Unity 6.7 Texture2D asset. Format 12 is BC3.
    // A default Texture2D asset leaves all four source channels available to the shader.
    internal static string TextureYaml(string name, PackedTexture texture) => FormattableString.Invariant($$"""
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!28 &2800000
        Texture2D:
          m_ObjectHideFlags: 0
          m_CorrespondingSourceObject: {fileID: 0}
          m_PrefabInstance: {fileID: 0}
          m_PrefabAsset: {fileID: 0}
          m_Name: {{name}}
          m_ImageContentsHash:
            serializedVersion: 2
            Hash: 00000000000000000000000000000000
          m_IsAlphaChannelOptional: 0
          serializedVersion: 4
          m_Width: {{texture.Width}}
          m_Height: {{texture.Height}}
          m_CompleteImageSize: {{texture.Bytes.Length}}
          m_MipsStripped: 0
          m_TextureFormat: 12
          m_MipCount: {{texture.Mips}}
          m_IsReadable: 0
          m_IsPreProcessed: 0
          m_IgnoreMipmapLimit: 1
          m_MipmapLimitGroupName:
          m_StreamingMipmaps: 0
          m_StreamingMipmapsPriority: 0
          m_VTOnly: 0
          m_AlphaIsTransparency: 0
          m_ImageCount: 1
          m_TextureDimension: 2
          m_TextureSettings:
            serializedVersion: 2
            m_FilterMode: {{texture.FilterMode}}
            m_Aniso: {{texture.Aniso}}
            m_MipBias: {{texture.MipBias}}
            m_WrapU: {{texture.Wrap}}
            m_WrapV: {{texture.Wrap}}
            m_WrapW: {{texture.Wrap}}
          m_LightmapFormat: 0
          m_ColorSpace: 0
          m_PlatformBlob:
          image data: {{texture.Bytes.Length}}
          _typelessdata: {{Convert.ToHexStringLower(texture.Bytes)}}
          m_StreamData:
            serializedVersion: 2
            offset: 0
            size: 0
            path:

        """);
}
