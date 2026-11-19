using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace H3VR.Converter;

/// <summary>
/// Stages a probe-set-only Steam GI repair for an unsigned Unity player. It never
/// modifies either input; callers must separately review and install the staged delta.
/// </summary>
public static class PlayerEnlighten
{
    public sealed record SceneReceipt(string Scene, string SourceLevel, string TargetLevel, int ProbeSets,
        string SourceLevelSha256, string OriginalTargetSha256, string StagedTargetSha256, string[] ProbeHashes);
    public sealed record Receipt(int SchemaVersion, string SourceData, string TargetData, string ClassDatabaseSha256,
        string SourceBuildSettingsSha256, string TargetBuildSettingsSha256, SceneReceipt[] Scenes, Dictionary<string, string> Files);
    private sealed record SourceScene(string Scene, string Level, string[] Hashes, byte[] Mapping, string SourceLevelSha256, Dictionary<string, string> Files);
    private static readonly string[] MappingFields = ["m_Renderers", "m_Systems", "m_Probesets", "m_SystemAtlases", "m_TerrainChunks"];

    public static Receipt Stage(string sourceData, string targetData, string classDatabase, string expectedDatabaseSha256, string output)
    {
        string source = Path.GetFullPath(sourceData), target = Path.GetFullPath(targetData);
        string database = Path.GetFullPath(classDatabase), dest = Path.GetFullPath(output);
        if (!Directory.Exists(source) || !Directory.Exists(target) || !File.Exists(database)) throw new FileNotFoundException("Missing input data or class database.");
        if ((File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Symbolic class database is unsupported.");
        if (expectedDatabaseSha256.Length != 64 || !expectedDatabaseSha256.All(Uri.IsHexDigit) ||
            !SafeFiles.Hash(database).Equals(expectedDatabaseSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pinned Unity class database hash mismatch.");
        SafeFiles.RequireSeparate(source, target); SafeFiles.RequireSeparate(source, dest); SafeFiles.RequireSeparate(target, dest);
        if (Directory.Exists(dest) || File.Exists(dest)) throw new IOException("Staging destination already exists: " + dest);
        RejectLinks(source); RejectLinks(target);
        string parent = Path.GetDirectoryName(dest)!; Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".enlighten-" + Guid.NewGuid().ToString("N"));
        var manager = new AssetsManager();
        try
        {
            manager.LoadClassPackage(database);
            string sourceGlobalHash = SafeFiles.Hash(Path.Combine(source, "globalgamemanagers"));
            string targetGlobalHash = SafeFiles.Hash(Path.Combine(target, "globalgamemanagers"));
            var sourceScenes = ReadScenePaths(manager, source, "5.6.7f1");
            var targetScenes = ReadScenePaths(manager, target, "2021.3.45f2");
            var targetIndex = targetScenes.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
            var planned = ReadSourceScenes(manager, source, sourceScenes);
            foreach (var scene in planned) if (!targetIndex.ContainsKey(scene.Scene)) throw new InvalidDataException("Player is missing original GI scene: " + scene.Scene);
            Directory.CreateDirectory(staging);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            var receipts = new List<SceneReceipt>();
            foreach (var scene in planned)
            {
                string targetLevel = "level" + targetIndex[scene.Scene];
                string input = Path.Combine(target, targetLevel), staged = Path.Combine(staging, targetLevel);
                if (!File.Exists(input)) throw new FileNotFoundException("Player scene is missing: " + targetLevel);
                string beforeHash = SafeFiles.Hash(input);
                StageLevel(manager, input, staged, scene.Mapping);
                if (SafeFiles.Hash(input) != beforeHash) throw new IOException("Player input changed during staging: " + targetLevel);
                files.Add(targetLevel, SafeFiles.Hash(staged));
                foreach (var pair in scene.Files)
                {
                    string relative = Path.Combine("GI", targetLevel, pair.Key);
                    string original = Path.Combine(source, "GI", scene.Level, pair.Key);
                    string copy = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(original, copy, false);
                    if (SafeFiles.Hash(copy) != pair.Value || SafeFiles.Hash(original) != pair.Value) throw new IOException("Steam GI input changed during staging: " + original);
                    files.Add(relative.Replace('\\', '/'), pair.Value);
                }
                var finalNames = Directory.GetFiles(Path.Combine(source, "GI", scene.Level), "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(Path.Combine(source, "GI", scene.Level), p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
                if (!finalNames.SetEquals(scene.Files.Keys) || SafeFiles.Hash(Path.Combine(source, scene.Level)) != scene.SourceLevelSha256)
                    throw new IOException("Steam scene or GI inventory changed during staging: " + scene.Level);
                receipts.Add(new(scene.Scene, scene.Level, targetLevel, scene.Hashes.Length, scene.SourceLevelSha256, beforeHash, files[targetLevel], scene.Hashes));
            }
            var finalLevels = Directory.GetDirectories(Path.Combine(source, "GI")).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
            if (!finalLevels.SetEquals(planned.Select(s => s.Level)) || Directory.GetFiles(Path.Combine(source, "GI")).Length != 0)
                throw new IOException("Steam GI scene inventory changed during staging.");
            if (SafeFiles.Hash(Path.Combine(source, "globalgamemanagers")) != sourceGlobalHash || SafeFiles.Hash(Path.Combine(target, "globalgamemanagers")) != targetGlobalHash ||
                !SafeFiles.Hash(database).Equals(expectedDatabaseSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("BuildSettings or class database changed during staging.");
            var receipt = new Receipt(1, source, target, expectedDatabaseSha256.ToLowerInvariant(), sourceGlobalHash, targetGlobalHash, receipts.ToArray(), files);
            File.WriteAllText(Path.Combine(staging, "enlighten-stage.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Directory.Move(staging, dest);
            return receipt;
        }
        finally
        {
            manager.UnloadAllAssetsFiles(true);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static string[] ReadScenePaths(AssetsManager manager, string root, string expectedVersion)
    {
        string path = Path.Combine(root, "globalgamemanagers");
        var file = manager.LoadAssetsFile(path, false);
        try
        {
            RequireVersion(manager, file, expectedVersion);
            var objects = file.file.GetAssetsOfType(AssetClassID.BuildSettings);
            if (objects.Count != 1) throw new InvalidDataException("Expected exactly one BuildSettings: " + path);
            var array = manager.GetBaseField(file, objects[0])["scenes.Array"];
            if (array.IsDummy) throw new InvalidDataException("Missing BuildSettings scenes: " + path);
            var scenes = array.Children.Select(x => CanonicalScene(x.AsString)).ToArray();
            if (scenes.Length == 0 || scenes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != scenes.Length) throw new InvalidDataException("Empty or duplicate scene paths: " + path);
            return scenes;
        }
        finally { manager.UnloadAssetsFile(file); }
    }

    public static string CanonicalScene(string value)
    {
        string path = value.Replace('\\', '/');
        if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
            path.Split('/').Any(p => p.Length == 0 || p == "." || p == "..")) throw new InvalidDataException("Unsafe scene identity: " + value);
        return path;
    }

    private static List<SourceScene> ReadSourceScenes(AssetsManager manager, string root, string[] scenes)
    {
        string gi = Path.Combine(root, "GI");
        if (!Directory.Exists(gi)) throw new InvalidDataException("Steam GI directory is missing.");
        var result = new List<SourceScene>();
        if (Directory.GetFiles(gi).Length != 0) throw new InvalidDataException("Unexpected files in Steam GI root.");
        foreach (string directory in Directory.GetDirectories(gi).Order(StringComparer.Ordinal))
        {
            string level = Path.GetFileName(directory);
            if (!level.StartsWith("level", StringComparison.Ordinal) || !int.TryParse(level[5..], out int index) || index < 0 || index >= scenes.Length || level != "level" + index)
                throw new InvalidDataException("Unexpected Steam GI scene directory: " + level);
            string assetPath = Path.Combine(root, level);
            string levelHash = SafeFiles.Hash(assetPath);
            var file = manager.LoadAssetsFile(assetPath, false);
            try
            {
                RequireVersion(manager, file, "5.6.7f1");
                var objects = file.file.GetAssetsOfType(AssetClassID.LightmapSettings);
                if (objects.Count != 1) throw new InvalidDataException("Expected one source LightmapSettings: " + level);
                var mapping = manager.GetBaseField(file, objects[0])["m_EnlightenSceneMapping"];
                var hashes = ReadProbeHashes(mapping);
                byte[] expected = CreateProbeMapping(hashes);
                if (!mapping.WriteToByteArray().AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Unsupported source mapping encoding: " + level);
                var expectedFiles = hashes.SelectMany(h => new[] { h[..2] + "/" + h + ".pos", h[..2] + "/" + h + ".rpc.sse" }).ToHashSet(StringComparer.Ordinal);
                var actualFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
                if (!expectedFiles.SetEquals(actualFiles)) throw new InvalidDataException("Original GI files do not exactly match probe hashes: " + level);
                result.Add(new(scenes[index], level, hashes, expected, levelHash, expectedFiles.ToDictionary(p => p, p => SafeFiles.Hash(Path.Combine(directory, p)), StringComparer.Ordinal)));
            }
            finally { manager.UnloadAssetsFile(file); }
        }
        return result;
    }

    private static string[] ReadProbeHashes(AssetTypeValueField mapping)
    {
        if (mapping.IsDummy || !mapping.Children.Select(x => x.FieldName).SequenceEqual(MappingFields)) throw new InvalidDataException("Unsupported Enlighten mapping schema.");
        var arrays = mapping.Children.Select(x => x["Array"]).ToArray();
        if (arrays.Any(x => x.IsDummy) || arrays.Where((_, i) => i != 2).Any(x => x.Children.Count != 0)) throw new InvalidDataException("Only probe-set-only Enlighten mappings are supported.");
        var hashes = arrays[2].Children.Select(x =>
        {
            if (x.Children.Count != 16 || !x.Children.Select((v, i) => v.FieldName == "bytes[" + i + "]").All(x => x)) throw new InvalidDataException("Unsupported probe hash encoding.");
            return Convert.ToHexStringLower(x.Children.Select(v => v.AsByte).ToArray());
        }).ToArray();
        if (hashes.Length == 0 || hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Length) throw new InvalidDataException("Empty or duplicate probe hashes.");
        return hashes;
    }

    public static byte[] CreateProbeMapping(IReadOnlyList<string> hashes)
    {
        if (hashes.Count == 0 || hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Count || hashes.Any(h => h.Length != 32 || h.Any(c => !Uri.IsHexDigit(c)) || h != h.ToLowerInvariant()))
            throw new InvalidDataException("Invalid or duplicate probe hashes.");
        byte[] bytes = new byte[checked(20 + 16 * hashes.Count)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), hashes.Count);
        for (int i = 0; i < hashes.Count; i++) Convert.FromHexString(hashes[i]).CopyTo(bytes, 12 + i * 16);
        return bytes;
    }

    private static void StageLevel(AssetsManager manager, string input, string output, byte[] mappingBytes)
    {
        var file = manager.LoadAssetsFile(input, false);
        long pathId; byte[] originalObject, patchedObject; int offset, oldLength;
        Dictionary<long, (string Metadata, long Size, string Hash)> untouched;
        try
        {
            RequireVersion(manager, file, "2021.3.45f2");
            var objects = file.file.GetAssetsOfType(AssetClassID.LightmapSettings);
            if (objects.Count != 1) throw new InvalidDataException("Expected one target LightmapSettings: " + input);
            var info = objects[0]; pathId = info.PathId;
            untouched = FingerprintObjects(input, file.file, pathId);
            var field = manager.GetBaseField(file, info); var mapping = field["m_EnlightenSceneMapping"];
            if (mapping.IsDummy || !mapping.Children.Select(x => x.FieldName).SequenceEqual(MappingFields)) throw new InvalidDataException("Unsupported target mapping schema: " + input);
            var arrays = mapping.Children.Select(x => x["Array"]).ToArray();
            if (arrays.Any(x => x.IsDummy) || arrays.Any(x => x.Children.Count != 0)) throw new InvalidDataException("Target mapping is not empty: " + input);
            byte[] serialized = field.WriteToByteArray();
            byte[] concatenated = field.Children.SelectMany(x => x.WriteToByteArray()).ToArray();
            using (var read = File.OpenRead(input)) { read.Position = info.GetAbsoluteByteOffset(file.file); originalObject = new byte[checked((int)info.ByteSize)]; read.ReadExactly(originalObject); }
            if (!originalObject.AsSpan().SequenceEqual(serialized) || !serialized.AsSpan().SequenceEqual(concatenated)) throw new InvalidDataException("Cannot prove target serialized field extents: " + input);
            int fieldIndex = field.Children.FindIndex(x => x.FieldName == "m_EnlightenSceneMapping");
            if (fieldIndex < 0) throw new InvalidDataException("Target mapping field absent: " + input);
            offset = field.Children.Take(fieldIndex).Sum(x => x.WriteToByteArray().Length); oldLength = mapping.WriteToByteArray().Length;
            if (oldLength != 20 || mapping.WriteToByteArray().Any(x => x != 0)) throw new InvalidDataException("Unexpected empty target mapping encoding: " + input);
            patchedObject = new byte[checked(originalObject.Length - oldLength + mappingBytes.Length)];
            originalObject.AsSpan(0, offset).CopyTo(patchedObject); mappingBytes.CopyTo(patchedObject, offset);
            originalObject.AsSpan(offset + oldLength).CopyTo(patchedObject.AsSpan(offset + mappingBytes.Length));
            info.SetNewData(patchedObject);
            using var stream = File.Create(output); using var writer = new AssetsFileWriter(stream); file.file.Write(writer, 0);
        }
        finally { manager.UnloadAssetsFile(file); }
        var staged = manager.LoadAssetsFile(output, false);
        try
        {
            RequireVersion(manager, staged, "2021.3.45f2");
            var stagedUntouched = FingerprintObjects(output, staged.file, pathId);
            if (untouched.Count != stagedUntouched.Count || untouched.Any(p => !stagedUntouched.TryGetValue(p.Key, out var value) || value != p.Value))
                throw new InvalidDataException("Unrelated player objects changed during serialization: " + input);
            var info = staged.file.GetAssetInfo(pathId) ?? throw new InvalidDataException("Staged LightmapSettings is missing.");
            byte[] actual = new byte[checked((int)info.ByteSize)]; using var stream = File.OpenRead(output); stream.Position = info.GetAbsoluteByteOffset(staged.file); stream.ReadExactly(actual);
            if (!actual.AsSpan().SequenceEqual(patchedObject)) throw new InvalidDataException("Staged LightmapSettings failed byte roundtrip.");
            var parsed = manager.GetBaseField(staged, info)["m_EnlightenSceneMapping"];
            if (!parsed.WriteToByteArray().AsSpan().SequenceEqual(mappingBytes)) throw new InvalidDataException("Staged probe mapping failed parse roundtrip.");
            if (!actual.AsSpan(0, offset).SequenceEqual(originalObject.AsSpan(0, offset)) || !actual.AsSpan(offset + mappingBytes.Length).SequenceEqual(originalObject.AsSpan(offset + oldLength))) throw new InvalidDataException("Unrelated target object bytes changed.");
        }
        finally { manager.UnloadAssetsFile(staged); }
    }

    private static Dictionary<long, (string Metadata, long Size, string Hash)> FingerprintObjects(string path, AssetsFile file, long excluded)
    {
        var result = new Dictionary<long, (string, long, string)>();
        using var stream = File.OpenRead(path);
        byte[] buffer = new byte[1024 * 1024];
        foreach (var info in file.AssetInfos)
        {
            if (info.PathId == excluded) continue;
            stream.Position = info.GetAbsoluteByteOffset(file);
            long remaining = info.ByteSize;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (remaining > 0)
            {
                int length = (int)Math.Min(buffer.Length, remaining);
                stream.ReadExactly(buffer.AsSpan(0, length)); digest.AppendData(buffer, 0, length); remaining -= length;
            }
            if (!result.TryAdd(info.PathId, ($"{info.TypeId}|{info.TypeIdOrIndex}|{info.ScriptTypeIndex}|{info.Stripped}", info.ByteSize, Convert.ToHexStringLower(digest.GetHashAndReset()))))
                throw new InvalidDataException("Duplicate asset path ID: " + path);
        }
        return result;
    }

    private static void RequireVersion(AssetsManager manager, AssetsFileInstance file, string version)
    {
        if (file.file.Metadata.UnityVersion != version) throw new InvalidDataException("Unsupported Unity version: " + file.file.Metadata.UnityVersion);
        manager.LoadClassDatabaseFromPackage(version);
    }
    private static void RejectLinks(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
        {
            string path = pending.Pop();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Symbolic link input is unsupported: " + path);
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
        }
    }
}
