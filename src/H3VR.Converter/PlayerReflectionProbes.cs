using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace H3VR.Converter;

/// <summary>Restores baked reflection cubemaps missing from the recovered scene lighting data.</summary>
public static class PlayerReflectionProbes
{
    private const string SourceVersion = "5.6.7f1";
    private const int Bc6H = 24;
    private const int Faces = 6;
    private static readonly string[] ProbeIdentityFields = ["m_Mode", "m_Resolution", "m_Importance", "m_BoxSize",
        "m_BoxOffset", "m_IntensityMultiplier", "m_BlendDistance", "m_HDR", "m_BoxProjection"];

    public sealed record ProbeReceipt(long SourceProbe, long TargetProbe, string SourceAsset, long SourceCubemap,
        long TargetCubemap, int Width, int Mips, int Format, int ColorSpace, string Sha256, string[] FaceSha256);
    public sealed record SceneReceipt(string Scene, string SourceLevel, string TargetLevel, ProbeReceipt[] Probes);
    public sealed record Receipt(int SchemaVersion, SceneReceipt[] Scenes, Dictionary<string, string> Files);

    private sealed record Probe(long Id, string Identity, AssetTypeValueField Field);
    private sealed record Texture(string Name, int Width, int Mips, int FaceBytes, int Dimension,
        int Filter, int Aniso, float MipBias, int Wrap, byte[] Payload);

    // The entire delta is staged and read back before replacing any player files.
    public static Receipt Restore(string sourceData, string targetData, string classDatabase, string scratch, string receiptPath)
    {
        string source = Path.GetFullPath(sourceData), target = Path.GetFullPath(targetData);
        string staging = Path.GetFullPath(scratch), receipt = Path.GetFullPath(receiptPath);
        SafeFiles.RequireSeparate(source, target); SafeFiles.RequireSeparate(source, staging);
        SafeFiles.RequireSeparate(target, staging); SafeFiles.RequireSeparate(staging, receipt);
        SafeFiles.RequireSeparate(source, receipt); SafeFiles.RequireSeparate(target, receipt);
        if (File.Exists(receipt) || Directory.Exists(receipt) || Directory.Exists(staging) || File.Exists(staging))
            throw new IOException("Choose new reflection-probe staging and receipt paths.");
        if (new FileInfo(classDatabase).LinkTarget is not null || SafeFiles.Hash(classDatabase) != PlayerEnlighten.ClassDatabaseSha256)
            throw new InvalidDataException("Pinned Unity class database hash mismatch.");

        var original = new AssetsManager();
        var player = new AssetsManager();
        original.LoadClassPackage(classDatabase);
        player.LoadClassPackage(classDatabase);
        var fromScenes = PlayerEnlighten.ReadScenePaths(original, source, SourceVersion);
        var toScenes = PlayerEnlighten.ReadScenePaths(player, target, ConversionConfig.TargetVersion)
            .Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var playerType = FindCubemapType(player, target);
        var results = new List<SceneReceipt>();
        Directory.CreateDirectory(staging);
        try
        {
            for (int i = 0; i < fromScenes.Length; i++)
            {
                string sourceLevel = "level" + i;
                var oldLevel = original.LoadAssetsFile(Path.Combine(source, sourceLevel), false);
                try
                {
                    var probes = ReadProbes(original, oldLevel).Where(p => p.Field["m_BakedTexture"]["m_PathID"].AsLong != 0).ToArray();
                    if (probes.Length == 0) continue;
                    if (!toScenes.TryGetValue(fromScenes[i], out int targetIndex))
                        throw new InvalidDataException("Player is missing a stock reflection-probe scene: " + fromScenes[i]);
                    string targetLevel = "level" + targetIndex;
                    ProbeReceipt[] restored = StageScene(original, oldLevel, probes, player, source,
                        target, staging, targetLevel, playerType);
                    results.Add(new(fromScenes[i], sourceLevel, targetLevel, restored));
                }
                finally { original.UnloadAllAssetsFiles(); }
            }
            if (results.Count == 0) throw new InvalidDataException("No stock baked reflection probes were found.");
            var records = Directory.EnumerateFiles(staging).Order(StringComparer.Ordinal)
                .ToDictionary(file => Path.GetFileName(file)!, SafeFiles.Hash, StringComparer.Ordinal);
            var evidence = new Receipt(1, results.ToArray(), records);
            Install(staging, target, receipt, evidence);
            return evidence;
        }
        finally
        {
            original.UnloadAllAssetsFiles();
            player.UnloadAllAssetsFiles();
        }
    }

    private static ProbeReceipt[] StageScene(AssetsManager original, AssetsFileInstance oldLevel, Probe[] sources,
        AssetsManager player, string source, string target, string staging, string name, TypeTreeType playerType)
    {
        string input = Path.Combine(target, name), output = Path.Combine(staging, name);
        string resource = name + ".resS", resourcePath = Path.Combine(staging, resource);
        string existingResource = Path.Combine(target, resource);
        if (File.Exists(existingResource)) File.Copy(existingResource, resourcePath);
        var current = player.LoadAssetsFile(input, false);
        var records = new List<ProbeReceipt>();
        try
        {
            var candidates = ReadProbes(player, current).ToLookup(p => p.Identity, StringComparer.Ordinal);
            if (current.file.Metadata.FindTypeTreeTypeIndexByID((int)AssetClassID.Cubemap, ushort.MaxValue) < 0)
                current.file.Metadata.TypeTreeTypes.Add(playerType);
            long nextId = current.file.AssetInfos.Max(a => a.PathId);
            var used = new HashSet<long>();
            using (var stream = new FileStream(resourcePath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                foreach (var probe in sources)
                {
                    var matches = candidates[probe.Identity].ToArray();
                    if (matches.Length != 1 || !used.Add(matches[0].Id))
                        throw new InvalidDataException($"Missing or ambiguous stock reflection probe {probe.Id} in {name}.");
                    var match = matches[0];
                    var baked = match.Field["m_BakedTexture"];
                    if (baked["m_PathID"].AsLong != 0)
                        throw new InvalidDataException($"Reflection probe {match.Id} in {name} already has a baked texture.");
                    var sourceRef = Resolve(original, oldLevel, probe.Field["m_BakedTexture"], AssetClassID.Cubemap);
                    string sourceAsset = Path.GetFileName(sourceRef.File.path);
                    var cubemap = ReadTexture(original.GetBaseField(sourceRef.File, sourceRef.Info), source);
                    ulong offset = checked((ulong)stream.Position);
                    stream.Write(cubemap.Payload);
                    long id = checked(++nextId);
                    var info = AssetFileInfo.Create(current.file, id, (int)AssetClassID.Cubemap, player.ClassDatabase)
                        ?? throw new InvalidDataException("Unity cubemap type unavailable in the player.");
                    info.SetNewData(CreateTexture(player, current, cubemap, resource, offset));
                    current.file.Metadata.AddAssetInfo(info);
                    baked["m_FileID"].AsInt = 0;
                    baked["m_PathID"].AsLong = id;
                    current.file.GetAssetInfo(match.Id).SetNewData(match.Field);
                    records.Add(new(probe.Id, match.Id, sourceAsset, sourceRef.Info.PathId, id,
                        cubemap.Width, cubemap.Mips, Bc6H, 0, Digest(cubemap.Payload), FaceDigests(cubemap.Payload, cubemap.FaceBytes)));
                }
            }
            using var writer = new AssetsFileWriter(output);
            current.file.Write(writer, 0);
        }
        finally { player.UnloadAssetsFile(current); }

        var staged = player.LoadAssetsFile(output, false);
        try
        {
            foreach (var probe in records)
            {
                var ptr = player.GetBaseField(staged, staged.file.GetAssetInfo(probe.TargetProbe))["m_BakedTexture"];
                var resolved = Resolve(player, staged, ptr, AssetClassID.Cubemap);
                var actual = ReadTexture(player.GetBaseField(resolved.File, resolved.Info), staging);
                if (resolved.File != staged || resolved.Info.PathId != probe.TargetCubemap ||
                    actual.Width != probe.Width || actual.Mips != probe.Mips || Digest(actual.Payload) != probe.Sha256 ||
                    !FaceDigests(actual.Payload, actual.FaceBytes).SequenceEqual(probe.FaceSha256))
                    throw new InvalidDataException($"Staged baked reflection cubemap failed its byte roundtrip in {name}.");
            }
        }
        finally { player.UnloadAssetsFile(staged); }
        return records.ToArray();
    }

    private static TypeTreeType FindCubemapType(AssetsManager player, string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "sharedassets*.assets").Order(StringComparer.Ordinal))
        {
            var shared = player.LoadAssetsFile(path, false);
            try
            {
                int index = shared.file.Metadata.FindTypeTreeTypeIndexByID((int)AssetClassID.Cubemap, ushort.MaxValue);
                if (index >= 0) return shared.file.Metadata.TypeTreeTypes[index];
            }
            finally { player.UnloadAssetsFile(shared); }
        }
        throw new InvalidDataException("The native player has no serialized cubemap type.");
    }

    private static Probe[] ReadProbes(AssetsManager manager, AssetsFileInstance file)
    {
        var probes = new List<Probe>();
        foreach (var info in file.file.GetAssetsOfType(AssetClassID.ReflectionProbe))
        {
            var probe = manager.GetBaseField(file, info);
            var owner = Resolve(manager, file, probe["m_GameObject"], AssetClassID.GameObject);
            if (owner.File != file) throw new InvalidDataException("ReflectionProbe owner belongs to another file.");
            var obj = manager.GetBaseField(file, owner.Info);
            var transform = obj["m_Component.Array"].Children.Select(pair => pair.Children[^1])
                .Where(ptr => ptr["m_FileID"].AsInt == 0)
                .Select(ptr => file.file.GetAssetInfo(ptr["m_PathID"].AsLong))
                .Single(a => a.TypeId is (int)AssetClassID.Transform or (int)AssetClassID.RectTransform);
            var pose = manager.GetBaseField(file, transform);
            var names = new List<string> { obj["m_Name"].AsString };
            var visited = new HashSet<long> { transform.PathId };
            var parent = pose["m_Father"];
            while (parent["m_PathID"].AsLong != 0)
            {
                if (parent["m_FileID"].AsInt != 0 || !visited.Add(parent["m_PathID"].AsLong))
                    throw new InvalidDataException("Unresolved or cyclic reflection probe hierarchy.");
                var ancestor = manager.GetBaseField(file, file.file.GetAssetInfo(parent["m_PathID"].AsLong));
                var ancestorObject = Resolve(manager, file, ancestor["m_GameObject"], AssetClassID.GameObject);
                names.Add(manager.GetBaseField(ancestorObject.File, ancestorObject.Info)["m_Name"].AsString);
                parent = ancestor["m_Father"];
            }
            string identity = JsonSerializer.Serialize(names) + "|" + string.Join(",",
                new[] { "m_LocalPosition", "m_LocalRotation", "m_LocalScale" }.SelectMany(k => pose[k].Children)
                    .Select(n => Math.Round(n.AsFloat, 4).ToString(CultureInfo.InvariantCulture))) + "|" +
                string.Join("|", ProbeIdentityFields.Select(k => Convert.ToHexString(probe[k].WriteToByteArray())));
            probes.Add(new(info.PathId, identity, probe));
        }
        return probes.ToArray();
    }

    private static (AssetsFileInstance File, AssetFileInfo Info) Resolve(AssetsManager manager, AssetsFileInstance file,
        AssetTypeValueField ptr, AssetClassID type)
    {
        var resolved = manager.GetExtAsset(file, ptr, true);
        if (resolved.file is null || resolved.info is null || resolved.info.TypeId != (int)type)
            throw new InvalidDataException("Unresolved " + type + " pointer in " + file.path);
        return (resolved.file, resolved.info);
    }

    private static Texture ReadTexture(AssetTypeValueField field, string root)
    {
        int width = field["m_Width"].AsInt, mips = field["m_MipCount"].AsInt;
        if (width != field["m_Height"].AsInt || field["m_TextureFormat"].AsInt != Bc6H ||
            field["m_ImageCount"].AsInt != Faces || field["m_ColorSpace"].AsInt != 0 || field["m_TextureDimension"].AsInt != 2)
            throw new InvalidDataException("Expected a linear BC6H baked reflection cubemap.");
        int faceBytes = CompleteFaceBytes(width, mips);
        if (field["m_CompleteImageSize"].AsInt != faceBytes)
            throw new InvalidDataException("Baked cubemap face extent disagrees with its mip chain.");
        var data = field["image data"].AsByteArray;
        var stream = field["m_StreamData"];
        int length = checked(Faces * faceBytes);
        if (data.Length == 0)
        {
            string name = stream["path"].AsString;
            if (!Regex.IsMatch(name, @"^(?:sharedassets\d+\.assets|level\d+)\.resS$", RegexOptions.CultureInvariant) || stream["size"].AsUInt != length)
                throw new InvalidDataException("Invalid baked cubemap stream extent or path.");
            string file = SafeFiles.Inside(root, name);
            if (new FileInfo(file).LinkTarget is not null) throw new InvalidDataException("Linked cubemap stream is unsupported.");
            using var read = File.OpenRead(file);
            read.Position = checked((long)stream["offset"].AsULong);
            data = new byte[length];
            read.ReadExactly(data);
        }
        if (data.Length != length) throw new InvalidDataException("Baked cubemap does not contain six complete faces.");
        var settings = field["m_TextureSettings"];
        int wrap = settings["m_WrapMode"].IsDummy ? settings["m_WrapU"].AsInt : settings["m_WrapMode"].AsInt;
        return new(field["m_Name"].AsString, width, mips, faceBytes, 2, settings["m_FilterMode"].AsInt,
            settings["m_Aniso"].AsInt, settings["m_MipBias"].AsFloat, wrap, data);
    }

    internal static int CompleteFaceBytes(int width, int mips)
    {
        if (width <= 0 || (width & (width - 1)) != 0 || mips != 1 + System.Numerics.BitOperations.Log2((uint)width))
            throw new InvalidDataException("Expected a power-of-two cubemap with a complete mip chain.");
        int size = 0;
        for (int edge = width; edge > 0; edge >>= 1)
        {
            int blocks = (edge + 3) / 4;
            size = checked(size + blocks * blocks * 16);
        }
        return size;
    }

    private static AssetTypeValueField CreateTexture(AssetsManager manager, AssetsFileInstance level, Texture cubemap,
        string resource, ulong offset)
    {
        var field = manager.CreateValueBaseField(level, (int)AssetClassID.Cubemap);
        field["m_Name"].AsString = cubemap.Name;
        field["m_ForcedFallbackFormat"].AsInt = 4;
        field["m_IsAlphaChannelOptional"].AsBool = true;
        field["m_Width"].AsInt = cubemap.Width;
        field["m_Height"].AsInt = cubemap.Width;
        field["m_CompleteImageSize"].AsInt = cubemap.FaceBytes;
        field["m_TextureFormat"].AsInt = Bc6H;
        field["m_MipCount"].AsInt = cubemap.Mips;
        field["m_ImageCount"].AsInt = Faces;
        field["m_TextureDimension"].AsInt = cubemap.Dimension;
        field["m_ColorSpace"].AsInt = 0;
        var settings = field["m_TextureSettings"];
        settings["m_FilterMode"].AsInt = cubemap.Filter;
        settings["m_Aniso"].AsInt = cubemap.Aniso;
        settings["m_MipBias"].AsFloat = cubemap.MipBias;
        foreach (string axis in new[] { "m_WrapU", "m_WrapV", "m_WrapW" }) settings[axis].AsInt = cubemap.Wrap;
        var stream = field["m_StreamData"];
        stream["offset"].AsULong = offset;
        stream["size"].AsUInt = checked((uint)cubemap.Payload.Length);
        stream["path"].AsString = resource;
        return field;
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string[] FaceDigests(byte[] payload, int faceBytes)
    {
        if (faceBytes <= 0 || payload.Length != checked(Faces * faceBytes))
            throw new InvalidDataException("Baked cubemap does not contain six complete faces.");
        return Enumerable.Range(0, Faces).Select(i => Digest(payload.AsSpan(i * faceBytes, faceBytes))).ToArray();
    }

    private static void Install(string staging, string target, string receipt, Receipt evidence)
    {
        string backup = Directory.CreateDirectory(Path.Combine(staging, "backup")).FullName;
        var installed = new List<(string Name, bool Replaced)>();
        try
        {
            foreach (string name in evidence.Files.Keys)
            {
                string destination = Path.Combine(target, name);
                bool replaced = File.Exists(destination);
                if (replaced) File.Copy(destination, Path.Combine(backup, name));
                File.Move(Path.Combine(staging, name), destination, replaced);
                installed.Add((name, replaced));
            }
            SafeFiles.AtomicJson(receipt, evidence);
        }
        catch
        {
            foreach (var change in installed.AsEnumerable().Reverse())
            {
                string destination = Path.Combine(target, change.Name);
                if (change.Replaced) File.Move(Path.Combine(backup, change.Name), destination, true);
                else File.Delete(destination);
            }
            throw;
        }
    }
}
