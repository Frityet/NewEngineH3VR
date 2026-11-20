using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace H3VR.Converter;

/// <summary>
/// Stages a probe-set-only Steam GI repair for an unsigned Unity player. It never
/// modifies either input. A separate, receipt-pinned installation retains rollback evidence.
/// </summary>
public static class PlayerEnlighten
{
    internal const string ClassDatabaseName = "legacy-unity-classdata.tpk";
    internal const string ClassDatabaseSha256 = "129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6";
    internal const string ClassDatabaseUrl = "https://raw.githubusercontent.com/nesrak1/UABEA/057e2f6ae67ebc94a38faca9f946f8577162fa99/ReleaseFiles/classdata.tpk";
    private const uint SharedSubtreeFormat = 26;
    private const int SerializedFileHeaderSize = 48;
    private const string UnitySixLightmapSettingsHash = "2f93a6ca3848c1af1d1062d35b580284";

    public sealed record SceneReceipt(string Scene, string SourceLevel, string TargetLevel, int ProbeSets,
        string SourceLevelSha256, string OriginalTargetSha256, string StagedTargetSha256, string[] ProbeHashes);
    public sealed record Receipt(int SchemaVersion, string SourceData, string TargetData, string ClassDatabaseSha256,
        string SourceBuildSettingsSha256, string TargetBuildSettingsSha256, SceneReceipt[] Scenes, Dictionary<string, string> Files);
    private sealed record SourceScene(string Scene, string Level, string[] Hashes, byte[] Mapping, string SourceLevelSha256, Dictionary<string, string> Files);
    private static readonly string[] MappingFields = ["m_Renderers", "m_Systems", "m_Probesets", "m_SystemAtlases", "m_TerrainChunks"];

    internal static string RequireClassDatabase(string cacheDirectory) =>
        CheckClassDatabase(cacheDirectory, required: true)!;

    internal static string? CheckClassDatabase(string cacheDirectory, bool required)
    {
        string database = Path.Combine(cacheDirectory, ClassDatabaseName);
        var file = new FileInfo(database);
        if (file.LinkTarget is not null || Directory.Exists(database))
            throw new InvalidDataException("The Unity class database cache path must be a regular file: " + database);
        if (file.Exists)
        {
            if (file.Length == 0 || SafeFiles.Hash(database) != ClassDatabaseSha256)
                throw new InvalidDataException($"Cached Unity class database failed SHA-256 verification at {database}. Expected {ClassDatabaseSha256}. Choose a different --cache or move the invalid file aside.");
            return database;
        }
        if (required)
            throw new InvalidDataException($"The pinned Unity class database is missing at {database}. Run once online to populate this cache, or choose a cache with the verified database.");
        return null;
    }

    internal static async Task EnsureClassDatabaseAsync(string repository, string cacheDirectory, bool offline,
        CancellationToken cancellation, HttpClient? downloader = null)
    {
        string path = Path.Combine(cacheDirectory, ClassDatabaseName);
        SafeFiles.RequireToolCachePath(repository, path);
        if (CheckClassDatabase(cacheDirectory, required: offline) is not null) return;

        Console.WriteLine("Downloading the pinned Unity class database into " + cacheDirectory);
        using var defaultClient = downloader is null ? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(1),
            MaxResponseContentBufferSize = 1024 * 1024
        } : null;
        byte[] bytes;
        try { bytes = await (downloader ?? defaultClient!).GetByteArrayAsync(ClassDatabaseUrl, cancellation); }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        {
            throw new IOException("The pinned Unity class database download timed out.", error);
        }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ClassDatabaseSha256)
            throw new InvalidDataException("Downloaded Unity class database failed SHA-256 verification. Expected " + ClassDatabaseSha256 + ".");

        SafeFiles.RequireToolCachePath(repository, path);
        Directory.CreateDirectory(cacheDirectory);
        string temporary = Path.Combine(cacheDirectory, ".classdata-" + Guid.NewGuid().ToString("N") + ".tmp");
        var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        try
        {
            await using (file)
                await file.WriteAsync(bytes, cancellation);
            cancellation.ThrowIfCancellationRequested();
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            {
                // A concurrent installer may have published the same pinned file.
                RequireClassDatabase(cacheDirectory);
            }
        }
        finally { File.Delete(temporary); }
    }

    public static Receipt Stage(string sourceData, string targetData, string classDatabase, string expectedDatabaseSha256, string output, string targetVersion)
    {
        ConversionConfig.RequireSupportedUnityVersion(targetVersion);
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
            var targetScenes = ReadScenePaths(manager, target, targetVersion);
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
                StageLevel(manager, input, staged, scene.Mapping, targetVersion);
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

    public sealed record InstalledFile(string Path, string InstalledSha256, string? PreviousSha256, string? Backup);
    public sealed record InstallReceipt(int SchemaVersion, string TargetData, string StageReceiptSha256,
        string TargetBuildSettingsSha256, InstalledFile[] Files);
    public sealed record Installation(string EvidenceDirectory, string ReceiptSha256);

    /// <summary>Installs a hash-pinned staged delta into a private unsigned player and retains backups.</summary>
    public static Installation Install(string stagedDelta, string targetData, string expectedStageReceiptSha256, string evidenceDirectory)
    {
        string stage = Path.GetFullPath(stagedDelta), target = Path.GetFullPath(targetData), evidence = Path.GetFullPath(evidenceDirectory);
        if (!Directory.Exists(stage) || !Directory.Exists(target)) throw new DirectoryNotFoundException("Stage or player data is missing.");
        SafeFiles.RequireSeparate(stage, target); SafeFiles.RequireSeparate(stage, evidence); SafeFiles.RequireSeparate(target, evidence);
        if (Directory.Exists(evidence) || File.Exists(evidence)) throw new IOException("Evidence destination already exists: " + evidence);
        RejectLinks(stage); RejectLinks(target);
        string stageReceipt = Path.Combine(stage, "enlighten-stage.json");
        RequireHash(stageReceipt, expectedStageReceiptSha256);
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(stageReceipt)) ?? throw new InvalidDataException("Empty stage receipt.");
        if (receipt.SchemaVersion != 1 || Path.GetFullPath(receipt.TargetData) != target || receipt.Scenes == null || receipt.Files == null)
            throw new InvalidDataException("Stage receipt does not identify this target.");
        RequireHash(Path.Combine(target, "globalgamemanagers"), receipt.TargetBuildSettingsSha256);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var levels = new Dictionary<string, SceneReceipt>(StringComparer.Ordinal);
        foreach (var scene in receipt.Scenes)
        {
            CanonicalScene(scene.Scene);
            if (!System.Text.RegularExpressions.Regex.IsMatch(scene.TargetLevel, @"^level(0|[1-9][0-9]*)$") ||
                scene.ProbeHashes == null || scene.ProbeSets != scene.ProbeHashes.Length || !levels.TryAdd(scene.TargetLevel, scene))
                throw new InvalidDataException("Invalid staged scene record.");
            CreateProbeMapping(scene.ProbeHashes);
            expected.Add(scene.TargetLevel);
            foreach (string hash in scene.ProbeHashes)
            {
                expected.Add("GI/" + scene.TargetLevel + "/" + hash[..2] + "/" + hash + ".pos");
                expected.Add("GI/" + scene.TargetLevel + "/" + hash[..2] + "/" + hash + ".rpc.sse");
            }
        }
        if (!expected.SetEquals(receipt.Files.Keys)) throw new InvalidDataException("Stage receipt file set differs from its probe records.");
        var actual = SafeFiles.Files(stage).Select(p => Path.GetRelativePath(stage, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        expected.Add("enlighten-stage.json");
        if (!expected.SetEquals(actual)) throw new InvalidDataException("Staged directory has missing or unexpected files.");
        expected.Remove("enlighten-stage.json");
        var work = new List<InstalledFile>();
        foreach (string relative in expected.Order(StringComparer.Ordinal))
        {
            string from = SafeFiles.Inside(stage, relative), to = SafeFiles.Inside(target, relative);
            RequireDirectoryAncestors(target, to);
            string hash = receipt.Files[relative]; RequireHash(from, hash);
            if (levels.TryGetValue(relative, out var scene))
            {
                if (hash != scene.StagedTargetSha256) throw new InvalidDataException("Level hash differs from scene receipt: " + relative);
                RequireHash(to, scene.OriginalTargetSha256);
                work.Add(new(relative, hash, scene.OriginalTargetSha256, "backup/" + relative));
            }
            else if (File.Exists(to))
            {
                // Equal pre-existing files are left alone and never removed by cleanup.
                RequireHash(to, hash);
            }
            else if (Directory.Exists(to)) throw new IOException("GI destination is a directory: " + relative);
            else work.Add(new(relative, hash, null, null));
        }
        string parent = Path.GetDirectoryName(evidence)!; Directory.CreateDirectory(parent);
        string pending = Path.Combine(parent, ".enlighten-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pending);
        var committed = new List<InstalledFile>();
        bool keepEvidence = false;
        try
        {
            foreach (var item in work.Where(x => x.Backup != null))
            {
                string backup = SafeFiles.Inside(pending, item.Backup!);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(SafeFiles.Inside(target, item.Path), backup, false);
                RequireHash(backup, item.PreviousSha256!);
            }
            var installReceipt = new InstallReceipt(1, target, expectedStageReceiptSha256.ToLowerInvariant(), receipt.TargetBuildSettingsSha256, work.ToArray());
            string journal = Path.Combine(pending, "enlighten-install.json");
            File.WriteAllText(journal, JsonSerializer.Serialize(installReceipt, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Directory.Move(pending, evidence); keepEvidence = true;
            RequireHash(stageReceipt, expectedStageReceiptSha256);
            RequireHash(Path.Combine(target, "globalgamemanagers"), receipt.TargetBuildSettingsSha256);
            foreach (var item in work)
            {
                string destination = SafeFiles.Inside(target, item.Path);
                if (item.PreviousSha256 != null) RequireHash(destination, item.PreviousSha256);
                else if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("GI destination appeared during install: " + item.Path);
                CopyVerified(SafeFiles.Inside(stage, item.Path), destination, item.InstalledSha256, item.PreviousSha256);
                committed.Add(item);
            }
            return new(evidence, SafeFiles.Hash(Path.Combine(evidence, "enlighten-install.json")));
        }
        catch (Exception installError)
        {
            try
            {
                if (committed.Count > 0) RestoreFiles(target, evidence, committed.AsEnumerable().Reverse().ToArray());
                if (Directory.Exists(evidence)) Directory.Delete(evidence, true);
                keepEvidence = false;
            }
            catch (Exception rollbackError) { throw new AggregateException("GI installation failed and rollback was incomplete; evidence retained at " + evidence, installError, rollbackError); }
            throw;
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); if (!keepEvidence && Directory.Exists(evidence)) Directory.Delete(evidence, true); }
    }

    /// <summary>Restores the previous levels and removes only GI files created by this installation.</summary>
    public static void Cleanup(string targetData, string evidenceDirectory, string expectedReceiptSha256)
    {
        string target = Path.GetFullPath(targetData), evidence = Path.GetFullPath(evidenceDirectory);
        SafeFiles.RequireSeparate(target, evidence);
        RejectLinks(target); RejectLinks(evidence);
        string path = Path.Combine(evidence, "enlighten-install.json"); RequireHash(path, expectedReceiptSha256);
        var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty installation receipt.");
        if (receipt.SchemaVersion != 1 || Path.GetFullPath(receipt.TargetData) != target || receipt.Files == null) throw new InvalidDataException("Installation receipt does not identify this target.");
        RequireHash(Path.Combine(target, "globalgamemanagers"), receipt.TargetBuildSettingsSha256);
        // Validate the complete cleanup before removing or restoring anything.
        foreach (var item in receipt.Files)
        {
            RequireHash(SafeFiles.Inside(target, item.Path), item.InstalledSha256);
            if (item.Backup != null) RequireHash(SafeFiles.Inside(evidence, item.Backup), item.PreviousSha256 ?? throw new InvalidDataException("Missing backup hash."));
            else if (item.PreviousSha256 != null) throw new InvalidDataException("Missing backup path.");
        }
        RestoreFiles(target, evidence, receipt.Files.Reverse().ToArray());
    }

    private static void RestoreFiles(string target, string evidence, IEnumerable<InstalledFile> files)
    {
        foreach (var item in files)
        {
            string destination = SafeFiles.Inside(target, item.Path); RequireHash(destination, item.InstalledSha256);
            if (item.Backup == null) File.Delete(destination);
            else CopyVerified(SafeFiles.Inside(evidence, item.Backup), destination, item.PreviousSha256!, item.InstalledSha256);
        }
    }
    private static void RequireDirectoryAncestors(string root, string destination)
    {
        string? path = Path.GetDirectoryName(destination);
        while (path != null && path != root)
        {
            if (File.Exists(path)) throw new IOException("A destination parent is a file: " + path);
            path = Path.GetDirectoryName(path);
        }
        if (path == null) throw new InvalidDataException("Destination is outside player data.");
    }
    private static void RequireHash(string path, string expected)
    {
        if (expected == null || expected.Length != 64 || !expected.All(Uri.IsHexDigit) || !File.Exists(path) ||
            !SafeFiles.Hash(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Missing file or SHA-256 mismatch: " + path);
    }
    private static void CopyVerified(string source, string destination, string expected, string? expectedDestination)
    {
        RequireHash(source, expected); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = destination + ".nativeh3vr-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp, false); RequireHash(temp, expected);
            if (expectedDestination != null) RequireHash(destination, expectedDestination);
            File.Move(temp, destination, expectedDestination != null);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static string[] ReadScenePaths(AssetsManager manager, string root, string expectedVersion)
    {
        string path = Path.Combine(root, "globalgamemanagers");
        var file = manager.LoadAssetsFile(path, false);
        try
        {
            RequireVersion(manager, file, expectedVersion);
            var objects = file.file.GetAssetsOfType(AssetClassID.BuildSettings);
            if (objects.Count != 1) throw new InvalidDataException("Expected exactly one BuildSettings: " + path);
            var info = objects[0];
            var template = manager.GetTemplateBaseField(file, info).Children[0];
            if (template.Name != "scenes") throw new InvalidDataException("Missing leading BuildSettings scenes: " + path);
            var sceneField = template.MakeValue(file.file.Reader, info.GetAbsoluteByteOffset(file.file));
            var array = sceneField["Array"];
            if (array.IsDummy || !ReadObjectBytes(file, info).AsSpan().StartsWith(sceneField.WriteToByteArray()))
                throw new InvalidDataException("Cannot prove the BuildSettings scene table extent: " + path);
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

    private static void StageLevel(AssetsManager manager, string input, string output, byte[] mappingBytes, string targetVersion)
    {
        var file = manager.LoadAssetsFile(input, false);
        long pathId; byte[] originalObject, patchedObject; int oldLength;
        Dictionary<long, (string Metadata, long Size, string Hash)> untouched;
        try
        {
            RequireVersion(manager, file, targetVersion);
            var objects = file.file.GetAssetsOfType(AssetClassID.LightmapSettings);
            if (objects.Count != 1) throw new InvalidDataException("Expected one target LightmapSettings: " + input);
            var info = objects[0]; pathId = info.PathId;
            untouched = FingerprintObjects(input, file.file, pathId);
            var mapping = ReadTargetMapping(manager, file, info);
            var arrays = mapping.Children.Select(x => x["Array"]).ToArray();
            if (arrays.Any(x => x.IsDummy) || arrays.Any(x => x.Children.Count != 0)) throw new InvalidDataException("Target mapping is not empty: " + input);
            byte[] emptyMapping = mapping.WriteToByteArray();
            originalObject = ReadObjectBytes(file, info);
            oldLength = emptyMapping.Length;
            if (oldLength != 20 || emptyMapping.Any(x => x != 0) || !originalObject.AsSpan().StartsWith(emptyMapping))
                throw new InvalidDataException("Unexpected empty target mapping encoding: " + input);
            patchedObject = new byte[checked(originalObject.Length - oldLength + mappingBytes.Length)];
            mappingBytes.CopyTo(patchedObject, 0);
            originalObject.AsSpan(oldLength).CopyTo(patchedObject.AsSpan(mappingBytes.Length));
            info.SetNewData(patchedObject);
            WritePlayerLevel(file, output);
        }
        finally { manager.UnloadAssetsFile(file); }
        var staged = manager.LoadAssetsFile(output, false);
        try
        {
            RequireVersion(manager, staged, targetVersion);
            var stagedUntouched = FingerprintObjects(output, staged.file, pathId);
            if (untouched.Count != stagedUntouched.Count || untouched.Any(p => !stagedUntouched.TryGetValue(p.Key, out var value) || value != p.Value))
                throw new InvalidDataException("Unrelated player objects changed during serialization: " + input);
            var info = staged.file.GetAssetInfo(pathId) ?? throw new InvalidDataException("Staged LightmapSettings is missing.");
            byte[] actual = ReadObjectBytes(staged, info);
            if (!actual.AsSpan().SequenceEqual(patchedObject)) throw new InvalidDataException("Staged LightmapSettings failed byte roundtrip.");
            var parsed = ReadTargetMapping(manager, staged, info);
            if (!parsed.WriteToByteArray().AsSpan().SequenceEqual(mappingBytes)) throw new InvalidDataException("Staged probe mapping failed parse roundtrip.");
            if (!actual.AsSpan(mappingBytes.Length).SequenceEqual(originalObject.AsSpan(oldLength))) throw new InvalidDataException("Unrelated target object bytes changed.");
        }
        finally { manager.UnloadAssetsFile(staged); }
    }

    private static AssetTypeValueField ReadTargetMapping(AssetsManager manager, AssetsFileInstance file, AssetFileInfo info)
    {
        RequireTypeHash(file, AssetClassID.LightmapSettings, UnitySixLightmapSettingsHash);
        // The 6.7 player keeps the mapping first but changed later GI settings fields.
        // Decode just the mapping so the remaining serialized object stays byte-identical.
        var template = manager.GetTemplateBaseField(file, info).Children[0];
        if (template.Name != "m_EnlightenSceneMapping" || !template.Children.Select(x => x.Name).SequenceEqual(MappingFields))
            throw new InvalidDataException("Unsupported target mapping schema: " + file.path);
        return template.MakeValue(file.file.Reader, info.GetAbsoluteByteOffset(file.file));
    }

    internal static byte[] ReadObjectBytes(AssetsFileInstance file, AssetFileInfo info)
    {
        using var stream = File.OpenRead(file.path);
        stream.Position = info.GetAbsoluteByteOffset(file.file);
        var bytes = new byte[checked((int)info.ByteSize)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static void WritePlayerLevel(AssetsFileInstance file, string output)
    {
        var header = file.file.Header;
        var metadata = file.file.Metadata;
        if (header.Version != SharedSubtreeFormat || metadata.TypeTreeEnabled || !string.IsNullOrEmpty(metadata.UserInformation))
            throw new InvalidDataException("Unsupported Unity 6.7 player scene metadata: " + file.path);
        VerifyEmptySharedSubtreeTable(file.path, header);
        // The pinned AssetsTools writer omits the v26 shared-subtree count.
        // An empty table is four zero bytes before the empty user-info string.
        metadata.UserInformation = new string('\0', sizeof(uint));
        try
        {
            using var writer = new AssetsFileWriter(output);
            file.file.Write(writer, 0);
        }
        finally { metadata.UserInformation = string.Empty; }

        using var reader = new AssetsFileReader(output);
        var writtenHeader = new AssetsFileHeader();
        writtenHeader.Read(reader);
        VerifyEmptySharedSubtreeTable(output, writtenHeader);
    }

    private static void VerifyEmptySharedSubtreeTable(string path, AssetsFileHeader header)
    {
        using var reader = new AssetsFileReader(path) { BigEndian = header.Endianness };
        reader.Position = SerializedFileHeaderSize;
        var metadata = new AssetsFileMetadata();
        metadata.Read(reader, header.Version);
        if (metadata.TypeTreeEnabled || !string.IsNullOrEmpty(metadata.UserInformation) ||
            reader.Position + sizeof(uint) != SerializedFileHeaderSize + (long)header.MetadataSize)
            throw new InvalidDataException("Unity 6.7 scene requires an empty shared-subtree table: " + path);
        reader.Position--;
        if (reader.ReadUInt32() != 0 || reader.ReadByte() != 0)
            throw new InvalidDataException("Unexpected shared-subtree table or user information: " + path);
    }

    internal static void RequireTypeHash(AssetsFileInstance file, AssetClassID type, string expected)
    {
        int index = file.file.Metadata.FindTypeTreeTypeIndexByID((int)type, ushort.MaxValue);
        if (index < 0 || file.file.Metadata.TypeTreeTypes[index].TypeHash.ToString() != expected)
            throw new InvalidDataException($"Unsupported {type} layout in {file.file.Metadata.UnityVersion}: {file.path}");
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

    internal static void RequireVersion(AssetsManager manager, AssetsFileInstance file, string version)
    {
        if (file.file.Metadata.UnityVersion != version) throw new InvalidDataException("Unsupported Unity version: " + file.file.Metadata.UnityVersion);
        if (manager.LoadClassDatabaseFromPackage(version) is null)
            throw new InvalidDataException("Missing class database for Unity " + version);
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
