using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET.Extra;

namespace H3VR.Converter;

/// <summary>Preserves original compressed cubemaps used by stock material samplers.</summary>
internal static class LegacyMaterialCubemaps
{
    private const int Faces = 6;
    private const int Bc6H = 24;
    private const RegexOptions Line = RegexOptions.Multiline | RegexOptions.CultureInvariant;
    private static readonly Regex Guid = new(@"^guid: ([0-9a-f]{32})\r?$", Line);
    private static readonly Regex Name = new(@"^  m_Name: ([^\r\n]+)", Line);
    private static readonly Regex CubeImporter = new(@"^  textureShape: 2\r?$", Line);
    private static readonly Regex Sampler = new(
        @"^      (?:- )?(?<slot>_Tex|_Cubemap):\r?\n        m_Texture: \{fileID: 8900000, guid: (?<guid>[0-9a-f]{32}), type: (?<type>3)\}", Line);

    private sealed record Source(string Name, string File, long PathId, string Sha256, string[] Materials,
        int Format = Bc6H, int ColorSpace = 0, string Slot = "_Tex");
    private static readonly Source[] Inputs =
    [
        new("UGS_FreightHouse_CustomSkybox", "resources.assets", 8238,
            "46265437fe296f02779b4a7c6e2c0e6a7bc52e377bf883bcb4d3654b98ea4dcb", ["CynSky_Day Sun Low Summersky"]),
        new("ReflectionProbe-0", "resources.assets", 8239,
            "b2088ab41e7f13dfbe72076b892740287d617f7e0937cb369c1cb092fbaff8cc", ["CynSky_Day Twilight Summersky"]),
        new("Sierra_Madre_B_Ref", "sharedassets1.assets", 506,
            "a4f092f34bf42422e1ee52185333f6707731e107af16649cba9116da3694a199", ["Testing1_Sky"]),
        new("04-23_Day_A", "sharedassets6.assets", 393,
            "9b66d9125a3dd2e2dbb63237299656d7ca65e9a2eea29c68aaaf17c8570f4859", ["Sky"]),
        new("starmap_2020_8k", "sharedassets9.assets", 10,
            "d5a2c7532c7d6de147e05b24be1b80bafda2fc7e381f0b98dbbd780c93dd6886", ["StarLitSky1", "StarLitSky2"]),
        new("ReflectionProbe-0", "sharedassets44.assets", 21,
            "b433e399d04927268d8654c8dae8667bd3b5e432ea05cb07478a86185ea61619", ["Facility_skybox"]),
        new("LivingRoom", "sharedassets14.assets", 453,
            "7e3274148fd1187d77b6f3106b33e13a51bca6537eb8dde86194496de9f736b5", ["LivingRoom"],
            Format: 10, ColorSpace: 1),
        new("Sky_Night Moon High HorizonRich_Packed", "sharedassets28.assets", 963,
            "9459ade4b301f57216de5a4edc882005447cfa395a72fc6d6af36c433dc92f58",
            ["Night Moon High HorizonRich 3"], Format: 25, ColorSpace: 1, Slot: "_Cubemap"),
    ];

    internal sealed record CubeData(int Width, int Mips, int Format, int ColorSpace, int Filter, int Aniso,
        float MipBias, int Wrap, int FaceBytes, byte[] Pixels);
    private sealed record Binding(string Path, string Text, Match Sampler, Source Source);

    internal static int Stage(string source, string assets, string classDatabase)
    {
        string cubeDirectory = Path.Combine(assets, "Cubemap");
        string materialDirectory = Path.Combine(assets, "Material");
        if (!Directory.Exists(cubeDirectory) || !Directory.Exists(materialDirectory)) return 0;
        var recoveredCubes = Directory.EnumerateFiles(cubeDirectory, "*.png.meta")
            .Select(path => (Path: path, Content: File.ReadAllText(path)))
            .Where(cube => CubeImporter.IsMatch(cube.Content))
            .ToDictionary(cube => Guid.Match(cube.Content).Groups[1].Value, cube => cube.Path, StringComparer.Ordinal);
        var bindings = new List<Binding>();
        var selectedNames = Inputs.SelectMany(input => input.Materials).ToHashSet(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(materialDirectory, "*.mat"))
        {
            string text = File.ReadAllText(path);
            string material = Name.Match(text).Groups[1].Value;
            if (!selectedNames.Contains(material)) continue;
            foreach (Match sampler in Sampler.Matches(text))
            {
                if (!recoveredCubes.TryGetValue(sampler.Groups["guid"].Value, out string? cube)) continue;
                string importedName = Path.GetFileName(cube)[..^".png.meta".Length];
                var input = Inputs.SingleOrDefault(input => input.Slot == sampler.Groups["slot"].Value &&
                    input.Materials.Contains(material, StringComparer.Ordinal) &&
                    LegacyPackedTextureData.IsExportedCopy(importedName, input.Name));
                if (input is not null) bindings.Add(new(path, text, sampler, input));
            }
        }
        if (bindings.Count == 0) return 0;
        // Stock skybox materials bind one registered cube sampler each.
        if (bindings.Select(binding => binding.Path).Distinct(StringComparer.Ordinal).Count() != bindings.Count)
            throw new InvalidDataException("Ambiguous stock material cubemap binding.");

        string output = Path.Combine(assets, "NativeH3VR-Derived", "StockMaterialCubemaps");
        Directory.CreateDirectory(output);
        var manager = new AssetsManager();
        manager.LoadClassPackage(classDatabase);
        foreach (var group in bindings.GroupBy(binding => binding.Source))
        {
            Source input = group.Key;
            string guid = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                "NativeH3VR.StockMaterialCubemap:" + input.Sha256)))[..32];
            string path = Path.Combine(output, input.Name + "-" + input.Sha256[..12] + ".cubemap");
            var data = ReadSource(manager, source, input);
            WriteCube(path, input.Name, guid, data);
            foreach (var binding in group)
            {
                var oldGuid = binding.Sampler.Groups["guid"];
                var oldType = binding.Sampler.Groups["type"];
                File.WriteAllText(binding.Path, binding.Text[..oldGuid.Index] + guid +
                    binding.Text[(oldGuid.Index + oldGuid.Length)..oldType.Index] + "2" +
                    binding.Text[(oldType.Index + oldType.Length)..]);
            }
        }
        return bindings.Count;
    }

    private static CubeData ReadSource(AssetsManager manager, string source, Source input)
    {
        try
        {
            var file = manager.LoadAssetsFile(SafeFiles.Inside(source, input.File), false);
            PlayerEnlighten.RequireVersion(manager, file, "5.6.7f1");
            var info = file.file.GetAssetInfo(input.PathId);
            if (info is null || info.GetTypeId(file.file) != (int)AssetClassID.Cubemap)
                throw new InvalidDataException($"Stock cubemap is absent from {input.File}.");
            var cube = manager.GetBaseField(file, info);
            if (cube["m_Name"].AsString != input.Name || cube["m_TextureFormat"].AsInt != input.Format ||
                cube["m_ColorSpace"].AsInt != input.ColorSpace || cube["m_ImageCount"].AsInt != Faces ||
                cube["m_TextureDimension"].AsInt != 2)
                throw new InvalidDataException($"Unexpected original cubemap metadata for {input.Name}.");
            int width = cube["m_Width"].AsInt, mips = cube["m_MipCount"].AsInt;
            int faceBytes = MipFaceBytes(width, mips, input.Format);
            if (cube["m_Height"].AsInt != width || cube["m_CompleteImageSize"].AsInt != faceBytes)
                throw new InvalidDataException($"Original cubemap face extent differs for {input.Name}.");
            int length = checked(faceBytes * Faces);
            byte[] pixels = cube["image data"].AsByteArray;
            if (pixels.Length == 0)
            {
                var stream = cube["m_StreamData"];
                string resource = stream["path"].AsString;
                if (resource != input.File + ".resS" || stream["size"].AsUInt != length)
                    throw new InvalidDataException($"Unexpected cubemap stream extent for {input.Name}.");
                using var original = File.OpenRead(SafeFiles.Inside(source, resource));
                original.Position = checked((long)stream["offset"].AsULong);
                pixels = new byte[length];
                original.ReadExactly(pixels);
            }
            if (pixels.Length != length || Convert.ToHexStringLower(SHA256.HashData(pixels)) != input.Sha256)
                throw new InvalidDataException($"Original cubemap payload changed for {input.Name}.");
            var sampling = cube["m_TextureSettings"];
            return new(width, mips, input.Format, input.ColorSpace, sampling["m_FilterMode"].AsInt,
                sampling["m_Aniso"].AsInt, sampling["m_MipBias"].AsFloat, sampling["m_WrapMode"].AsInt, faceBytes, pixels);
        }
        finally
        {
            manager.UnloadAllAssetsFiles();
        }
    }

    internal static int MipFaceBytes(int width, int mips, int format)
    {
        if (width <= 0 || (width & (width - 1)) != 0 || mips < 1 ||
            mips > 1 + System.Numerics.BitOperations.Log2((uint)width))
            throw new InvalidDataException("Invalid cubemap mip chain.");
        int blockBytes = format switch
        {
            10 => 8, // BC1
            Bc6H or 25 => 16, // BC6H or BC7
            _ => throw new InvalidDataException($"Unexpected material cubemap format {format}."),
        };
        int bytes = 0;
        for (int mip = 0; mip < mips; mip++, width = Math.Max(1, width / 2))
        {
            int blocks = (width + 3) / 4;
            bytes = checked(bytes + blocks * blocks * blockBytes);
        }
        return bytes;
    }

    internal static void WriteCube(string path, string name, string guid, CubeData data)
    {
        if (data.Pixels.Length != checked(Faces * data.FaceBytes))
            throw new InvalidDataException("A material cubemap must contain six complete faces.");
        using (var writer = File.CreateText(path))
        {
            // Unity 6.7's native Cubemap representation keeps the six face-major streams.
            writer.WriteLine(FormattableString.Invariant($$"""
                %YAML 1.1
                %TAG !u! tag:unity3d.com,2011:
                --- !u!89 &8900000
                Cubemap:
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
                  m_Width: {{data.Width}}
                  m_Height: {{data.Width}}
                  m_CompleteImageSize: {{data.FaceBytes}}
                  m_MipsStripped: 0
                  m_TextureFormat: {{data.Format}}
                  m_MipCount: {{data.Mips}}
                  m_IsReadable: 0
                  m_IsPreProcessed: 0
                  m_IgnoreMipmapLimit: 0
                  m_MipmapLimitGroupName:
                  m_StreamingMipmaps: 0
                  m_StreamingMipmapsPriority: 0
                  m_VTOnly: 0
                  m_AlphaIsTransparency: 0
                  m_ImageCount: 6
                  m_TextureDimension: 4
                  m_TextureSettings:
                    serializedVersion: 2
                    m_FilterMode: {{data.Filter}}
                    m_Aniso: {{data.Aniso}}
                    m_MipBias: {{data.MipBias}}
                    m_WrapU: {{data.Wrap}}
                    m_WrapV: {{data.Wrap}}
                    m_WrapW: {{data.Wrap}}
                  m_LightmapFormat: 0
                  m_ColorSpace: {{data.ColorSpace}}
                  m_PlatformBlob:
                  image data: {{data.Pixels.Length}}
                """));
            writer.Write("  _typelessdata: ");
            const int chunk = 64 * 1024;
            for (int start = 0; start < data.Pixels.Length; start += chunk)
                writer.Write(Convert.ToHexStringLower(data.Pixels.AsSpan(start, Math.Min(chunk, data.Pixels.Length - start))));
            writer.WriteLine();
            writer.WriteLine("""
                  m_StreamData:
                    serializedVersion: 2
                    offset: 0
                    size: 0
                    path:
                  m_SourceTextures:
                  - {fileID: 0}
                  - {fileID: 0}
                  - {fileID: 0}
                  - {fileID: 0}
                  - {fileID: 0}
                  - {fileID: 0}
                """);
        }
        File.WriteAllText(path + ".meta", $$"""
            fileFormatVersion: 2
            guid: {{guid}}
            NativeFormatImporter:
              externalObjects: {}
              mainObjectFileID: 8900000
              userData:
              assetBundleName:
              assetBundleVariant:

            """);
    }
}
