using System.Text.Json.Nodes;
using H3VR.Converter;

namespace NativeH3VR.ShaderOverlay;

internal static class ShaderLinks
{
    private sealed record Recovery(string ValidationState, string InputManifest, string InputManifestSha256,
        string ExportIdentityManifest, string ExportIdentityManifestSha256, Dictionary<string, string> Sha256);
    private sealed record Inventory(int SchemaVersion, Original[] Files);
    private sealed record Original(string Source, int? BundleEntry, string SerializedName, long[] PathIds,
        string Sha256, string Copy, string CopySha256);
    private sealed record RawIndex(JsonObject[] Bindings);
    private sealed record ProjectIndex(int SchemaVersion, Target[] Shaders);
    private sealed record Target(long PathId, string Guid, string ExportPath, string[] BundleChain, string CollectionName);
    private sealed record Match(Target Target, JsonObject Metadata, string RawPath, string RawHash, string MetaFile,
        string MetaHash, Original Original);
    private sealed record Patch(string Path, string Before, string After, KeywordRepair.PassRepair[] Passes);
    private sealed record Correspondence(int SchemaVersion, string ValidationState, string ExtractionFingerprint,
        string ShaderInputManifestSha256, string RawIdentityManifestSha256, JsonObject[] Links,
        string[] UnmappedProjectPaths, int UnusedRawObjects);
    private readonly record struct Key(string Bundle, string Member, long PathId);

    public static int Run(LinkOptions options, CancellationToken cancellation)
    {
        if (options.RepairExclusiveKeywords && options.OverlayCandidate is null)
            throw new ArgumentException("--repair-exclusive-keywords requires --overlay-candidate.");
        var classDatabase = options.RepairExclusiveKeywords
            ? SafeFiles.Canonical(options.ClassDatabase ?? Path.Combine(Repository.Find(), "cache", "legacy-unity-classdata.tpk"))
            : null;
        if (classDatabase is not null && !File.Exists(classDatabase))
            throw new FileNotFoundException("Install the Unity type tree package or pass --class-database.", classDatabase);
        var workspace = SafeFiles.Canonical(options.Workspace);
        var recovery = SafeFiles.Canonical(options.Recovery);
        var steam = SafeFiles.Canonical(options.SteamInstall);
        var output = Path.GetFullPath(options.Output);
        SafeFiles.RequireSeparate(output, steam);
        SafeFiles.RequireSeparate(output, Path.Combine(workspace, "recovery"));
        SafeFiles.RequireSeparate(output, recovery);
        if (File.Exists(output)) throw new IOException("Correspondence output already exists: " + output);

        var raw = Receipt.Read<Recovery>(Receipt.Child(recovery, "recovery-receipt.json"));
        if (raw.ValidationState != "raw-export-unvalidated")
            throw new InvalidDataException("Expected the raw recovery receipt from recover-shaders.");
        var identityPath = Receipt.Child(recovery, raw.ExportIdentityManifest);
        var inventoryPath = Receipt.Child(recovery, raw.InputManifest);
        Receipt.Verify(identityPath, raw.ExportIdentityManifestSha256);
        Receipt.Verify(inventoryPath, raw.InputManifestSha256);
        var inventory = Receipt.Read<Inventory>(inventoryPath);
        if (inventory.SchemaVersion != 3)
            throw new InvalidDataException("The raw inventory needs schema 3 serialized member identities.");

        var extract = Receipt.Extraction(workspace);
        var mapPath = Receipt.Child(workspace, "recovery/AuxiliaryFiles/shader-identity-v2.json");
        Receipt.Verify(mapPath, extract.Files[mapPath]);
        var projectIndex = Receipt.Read<ProjectIndex>(mapPath);
        if (projectIndex.SchemaVersion != 1) throw new InvalidDataException("Unsupported project identity schema.");

        var gameData = Receipt.Child(steam, "h3vr_Data");
        var originalByKey = SourceKeys(gameData, inventory, cancellation);
        var rawByKey = RawKeys(identityPath, originalByKey);
        var project = Receipt.Child(workspace, "recovery/ExportedProject");
        var rawProject = Receipt.Child(recovery, "assetripper-export/ExportedProject");
        var matches = new List<Match>();
        var misses = new List<string>();
        var used = new HashSet<Key>();
        foreach (var target in projectIndex.Shaders)
        {
            cancellation.ThrowIfCancellationRequested();
            var key = TargetKey(target);
            if (!rawByKey.TryGetValue(key, out var exported))
            {
                misses.Add(target.ExportPath);
                continue;
            }
            if (!used.Add(key)) throw new InvalidDataException("Ambiguous recovered project identity: " + key);
            matches.Add(CheckTarget(project, rawProject, target, exported, originalByKey[key], extract.Files, raw.Sha256));
        }

        var correspondence = new Correspondence(1, "identity-only-unvalidated", extract.Fingerprint, raw.InputManifestSha256,
            raw.ExportIdentityManifestSha256, matches.Select(match => match.Metadata).ToArray(), misses.ToArray(), rawByKey.Count - used.Count);
        Receipt.WriteNew(output, correspondence);
        if (options.OverlayCandidate is not null)
        {
            var overlay = Path.GetFullPath(options.OverlayCandidate);
            SafeFiles.RequireSeparate(overlay, steam);
            SafeFiles.RequireSeparate(overlay, workspace);
            SafeFiles.RequireSeparate(overlay, recovery);
            if (matches.Count == 0 || misses.Count > 0)
                throw new InvalidDataException("The shader overlay needs a complete match to the local extraction.");
            Stage(overlay, recovery, correspondence, matches, classDatabase, cancellation);
        }
        Console.WriteLine($"Linked {matches.Count} shader objects, {misses.Count} unmatched project shaders. Rendering remains unvalidated.");
        return misses.Count == 0 ? 0 : 1;
    }

    private static Dictionary<Key, Original> SourceKeys(string game, Inventory inventory, CancellationToken cancellation)
    {
        var result = new Dictionary<Key, Original>();
        foreach (var group in inventory.Files.GroupBy(entry => entry.Source, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var hash = group.First().Sha256;
            Receipt.Verify(Receipt.Child(game, group.Key), hash);
            foreach (var entry in group)
            {
                if (entry.Sha256 != hash) throw new InvalidDataException("Conflicting original file hash: " + entry.Source);
                foreach (var id in entry.PathIds)
                {
                    var key = SourceKey(entry.Source, entry.BundleEntry, entry.SerializedName, id);
                    if (!result.TryAdd(key, entry)) throw new InvalidDataException("Duplicate original shader: " + key);
                }
            }
        }
        return result;
    }

    private static Dictionary<Key, JsonObject> RawKeys(string path, Dictionary<Key, Original> originalByKey)
    {
        var result = new Dictionary<Key, JsonObject>();
        foreach (var row in Receipt.Read<RawIndex>(path).Bindings)
        {
            var source = Text(row, "source");
            var bundle = row["bundleEntry"]?.GetValue<int>();
            var key = SourceKey(source, bundle, Text(row, "serializedName"), row["PathId"]!.GetValue<long>());
            if (!originalByKey.TryGetValue(key, out var original) || original.Source != source ||
                original.BundleEntry != bundle || original.Sha256 != Text(row, "sourceSha256") || !result.TryAdd(key, row))
                throw new InvalidDataException("Unproven raw shader identity: " + key);
        }
        return result;
    }

    private static Match CheckTarget(string project, string rawProject, Target target, JsonObject exported, Original original,
        Dictionary<string, string> projectHashes, Dictionary<string, string> rawHashes)
    {
        var recovered = Receipt.Child(project, target.ExportPath);
        var meta = Receipt.Child(project, target.ExportPath + ".meta");
        Receipt.Verify(recovered, projectHashes[recovered]);
        Receipt.Verify(meta, projectHashes[meta]);
        var rawRelative = Text(exported, "rawPath");
        var rawPath = Receipt.Child(rawProject, rawRelative);
        var rawHash = rawHashes["ExportedProject/" + rawRelative];
        Receipt.Verify(rawPath, rawHash);
        var row = (JsonObject)exported.DeepClone();
        row["rawSha256"] = rawHash;
        row["projectGuid"] = target.Guid;
        row["projectPath"] = target.ExportPath;
        row["projectSha256"] = projectHashes[recovered];
        row["projectMetaSha256"] = projectHashes[meta];
        return new(target, row, rawPath, rawHash, meta, projectHashes[meta], original);
    }

    private static void Stage(string output, string recovery, Correspondence report, List<Match> matches,
        string? classDatabase, CancellationToken cancellation)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Choose a new overlay output: " + output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var candidate = output + ".stage-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(candidate);
        try
        {
            var fixes = new List<Patch>();
            var classDatabaseHash = classDatabase is null ? null : SafeFiles.Hash(classDatabase);
            foreach (var group in matches.GroupBy(match => match.Original.Copy, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                using var keywords = classDatabase is null ? null : OpenOriginal(recovery, group.First().Original, classDatabase);
                foreach (var match in group)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var outputPath = SafeFiles.Inside(candidate, match.Target.ExportPath);
                    SafeFiles.Copy(match.RawPath, outputPath);
                    SafeFiles.Copy(match.MetaFile, outputPath + ".meta");
                    Receipt.Verify(outputPath, match.RawHash);
                    Receipt.Verify(outputPath + ".meta", match.MetaHash);
                    if (keywords is null) continue;
                    var patched = keywords.Repair(outputPath, match.Target.PathId, candidate, match.Target.Guid);
                    if (patched.Length != 0)
                        fixes.Add(new(match.Target.ExportPath, match.RawHash, SafeFiles.Hash(outputPath), patched));
                }
            }
            var metadata = JsonNode.Parse(Receipt.Serialize(report))!.AsObject();
            metadata["keywordRepairs"] = JsonNode.Parse(Receipt.Serialize(fixes));
            if (classDatabase is not null)
            {
                Receipt.Verify(classDatabase, classDatabaseHash!);
                metadata["typeTreeDatabaseSha256"] = classDatabaseHash;
            }
            Receipt.WriteNew(Path.Combine(candidate, "shader-overlay-candidate.json"), metadata);
            Directory.Move(candidate, output);
        }
        finally { if (Directory.Exists(candidate)) Directory.Delete(candidate, true); }
    }

    private static KeywordRepair OpenOriginal(string recovery, Original source, string classDatabase)
    {
        var path = Receipt.Child(Receipt.Child(recovery, "shader-inputs"), source.Copy);
        Receipt.Verify(path, source.CopySha256);
        return new KeywordRepair(path, classDatabase);
    }

    private static Key SourceKey(string source, int? bundleEntry, string member, long id) =>
        new(bundleEntry is null ? "" : Leaf(source), Leaf(member), id);

    private static Key TargetKey(Target target)
    {
        if (target.BundleChain.Length is not (1 or 2) || target.BundleChain[^1] != "GameBundle")
            throw new InvalidDataException("Cannot prove original shader collection: " + target.CollectionName);
        var bundle = target.BundleChain.Length == 2 ? Leaf(target.BundleChain[0]) : "";
        return new(bundle, Leaf(target.CollectionName), target.PathId);
    }

    private static string Leaf(string path) => path.Replace('\\', '/').Split('/')[^1].ToLowerInvariant();

    private static string Text(JsonObject node, string key) =>
        node[key]?.GetValue<string>() ?? throw new InvalidDataException("Missing raw shader identity field: " + key);
}
