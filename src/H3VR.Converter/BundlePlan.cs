using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Security.Cryptography;
using System.Text.Json;

namespace H3VR.Converter;

/// <summary>JSON is intentionally field-shaped so Unity JsonUtility can read it directly.</summary>
public sealed class NativeBundlePlan
{
    public int schemaVersion = 1;
    public string project = "";
    public string sourceStreamingAssets = "";
    public NativeBundle[] bundles = [];
    public string[] issues = [];
    public NativeBundleSource[] sourceFiles = [];
}
public sealed class NativeBundle
{
    public string name = "";
    public string[] dependencies = [];
    public NativeBundleAsset[] assets = [];
}
public sealed class NativeBundleAsset
{
    public string address = "";
    public string asset = "";
}
public sealed class NativeBundleSource
{
    public string name = "";
    public string sha256 = "";
    public long bytes;
}
public sealed record BundlePlanReport(string OutputPath, string Sha256, int Bundles, int Addresses, int Issues);
public sealed record BundleAddressResolution(string? Asset, string Resolution, string[] Candidates)
{
    public bool Success => Asset is not null;
}

/// <summary>Reads original UnityFS bundles without mutating them and resolves their serialized address tables.</summary>
public static class BundlePlan
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };

    public static BundlePlanReport Create(string streamingAssets, string project, string outputPath)
    {
        streamingAssets = Path.GetFullPath(streamingAssets); project = Path.GetFullPath(project); outputPath = Path.GetFullPath(outputPath);
        if (!Directory.Exists(streamingAssets)) throw new DirectoryNotFoundException("Original game StreamingAssets is missing: " + streamingAssets);
        if (!Directory.Exists(Path.Combine(project, "Assets"))) throw new InvalidDataException("Bundle plan project must contain Assets.");
        SafeFiles.RequireSeparate(streamingAssets, project); SafeFiles.RequireSeparate(streamingAssets, outputPath); RejectLinks(streamingAssets); RejectLinks(Path.Combine(project, "Assets"));
        var issues = new List<string>();
        var assetPaths = IndexAssets(project, issues);
        var groupedAssets = IndexBundleMembers(project);
        var bundleFiles = SafeFiles.Files(streamingAssets).Where(IsUnityFs).Order(StringComparer.Ordinal).ToArray();
        var plan = new NativeBundlePlan { project = project, sourceStreamingAssets = streamingAssets };
        foreach (var file in bundleFiles)
        {
            try
            {
                var sha = SafeFiles.Hash(file); var info = ReadBundle(file);
                if (info.Addresses.Count == 1 && info.Addresses[0].Equals("AssetBundleManifest", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(info.Name)) { issues.Add(Path.GetFileName(file) + ": missing serialized m_AssetBundleName"); continue; }
                var assets = new List<NativeBundleAsset>();
                foreach (var address in info.Addresses)
                {
                    var resolved = Resolve(address, info.Name, assetPaths, groupedAssets);
                    if (!resolved.Success)
                    {
                        issues.Add($"{info.Name}: address '{address}' did not resolve by exact path or unique bundle basename" + (resolved.Candidates.Length == 0 ? "" : "; candidates=" + string.Join(", ", resolved.Candidates)));
                        continue;
                    }
                    assets.Add(new NativeBundleAsset { address = address, asset = resolved.Asset! });
                }
                var duplicateAddress = assets.GroupBy(a => a.address, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (duplicateAddress is not null) issues.Add($"{info.Name}: duplicate case-insensitive asset address '{duplicateAddress.Key}'");
                plan.bundles = plan.bundles.Append(new NativeBundle { name = info.Name, dependencies = info.Dependencies, assets = assets.ToArray() }).ToArray();
                plan.sourceFiles = plan.sourceFiles.Append(new NativeBundleSource { name = Path.GetRelativePath(streamingAssets, file).Replace(Path.DirectorySeparatorChar, '/'), sha256 = sha, bytes = new FileInfo(file).Length }).ToArray();
                if (!SafeFiles.Hash(file).Equals(sha, StringComparison.Ordinal)) throw new IOException("Original bundle changed while being inspected: " + file);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                issues.Add(Path.GetRelativePath(streamingAssets, file).Replace(Path.DirectorySeparatorChar, '/') + ": parser failed: " + e.Message);
            }
        }
        var duplicateNames = plan.bundles.GroupBy(b => b.name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        foreach (var name in duplicateNames) issues.Add("duplicate original bundle name: " + name);
        var known = plan.bundles.Select(b => b.name).ToHashSet(StringComparer.Ordinal);
        foreach (var bundle in plan.bundles)
        {
            foreach (var dep in bundle.dependencies)
                if (!known.Contains(dep)) issues.Add($"{bundle.name}: dependency is not present in original bundle set: {dep}");
            if (bundle.dependencies.Distinct(StringComparer.Ordinal).Count() != bundle.dependencies.Length) issues.Add(bundle.name + ": duplicate dependency names");
        }
        plan.issues = issues.Order(StringComparer.Ordinal).ToArray();
        if (plan.bundles.Length == 0) plan.issues = plan.issues.Append("no original asset bundles were found").ToArray();
        if (File.Exists(outputPath)) throw new IOException("Refusing to overwrite an existing bundle plan: " + outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(plan, Json) + "\n");
        return new BundlePlanReport(outputPath, SafeFiles.Hash(outputPath), plan.bundles.Length, plan.bundles.Sum(b => b.assets.Length), plan.issues.Length);
    }

    public static BundleAddressResolution Resolve(string address, string bundleName,
        IReadOnlyDictionary<string, string> exactPaths, IReadOnlyDictionary<string, string[]> bundleMembers)
    {
        var normalized = address.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        if (exactPaths.TryGetValue(normalized, out var exact)) return new(exact, "original-path", [exact]);
        if (bundleMembers.TryGetValue(bundleName, out var members))
        {
            var basename = Path.GetFileName(normalized);
            var candidates = members.Where(p => Path.GetFileName(p).Equals(basename, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (candidates.Length == 1) return new(candidates[0], "unique-bundle-basename", candidates);
            return new(null, candidates.Length == 0 ? "unresolved" : "ambiguous", candidates);
        }
        return new(null, "unresolved", []);
    }

    private sealed record BundleContents(string Name, string[] Dependencies, List<string> Addresses);
    private static BundleContents ReadBundle(string file)
    {
        var manager = new AssetsManager();
        try
        {
            var bundle = manager.LoadBundleFile(file);
            if (bundle.file.Header.Signature != "UnityFS") throw new InvalidDataException("unsupported bundle signature " + bundle.file.Header.Signature);
            if (bundle.file.BlockAndDirInfo.DirectoryInfos.Count == 0) throw new InvalidDataException("UnityFS bundle has no serialized file entries");
            var assets = manager.LoadAssetsFileFromBundle(bundle, 0, true);
            var entries = assets.file.GetAssetsOfType(AssetClassID.AssetBundle);
            if (entries.Count == 0) throw new InvalidDataException("bundle does not contain an AssetBundle object");
            var root = manager.GetBaseField(assets, entries[0]);
            var name = root["m_AssetBundleName"].AsString;
            var dependencies = root["m_Dependencies.Array"].Children.Select(x => x.AsString).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var addresses = root["m_Container.Array"].Children.Select(x => x[0].AsString).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return new BundleContents(name, dependencies, addresses);
        }
        finally { manager.UnloadAllAssetsFiles(true); manager.UnloadAllBundleFiles(); }
    }

    private static Dictionary<string, string> IndexAssets(string project, List<string> issues)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in SafeFiles.Files(Path.Combine(project, "Assets")).Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = Path.GetRelativePath(project, path).Replace(Path.DirectorySeparatorChar, '/');
            if (result.TryGetValue(relative, out var old)) issues.Add("case-insensitive asset path collision: " + old + " and " + relative);
            else result[relative] = relative;
        }
        return result;
    }
    private static Dictionary<string, string[]> IndexBundleMembers(string project)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var meta in SafeFiles.Files(Path.Combine(project, "Assets")).Where(p => p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
        {
            var asset = meta[..^5]; if (!File.Exists(asset)) continue;
            var value = FindBundleName(File.ReadAllText(meta)); if (value is null) continue;
            var relative = Path.GetRelativePath(project, asset).Replace(Path.DirectorySeparatorChar, '/');
            if (!grouped.TryGetValue(value, out var files)) grouped[value] = files = [];
            files.Add(relative);
        }
        return grouped.ToDictionary(p => p.Key, p => p.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }
    private static string? FindBundleName(string meta)
    {
        foreach (var line in meta.Split('\n'))
        {
            const string prefix = "  assetBundleName:";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var value = line[prefix.Length..].Trim(); return value.Length == 0 ? null : value;
        }
        return null;
    }
    private static bool IsUnityFs(string path)
    {
        if (Path.HasExtension(path)) return false;
        using var stream = File.OpenRead(path); Span<byte> bytes = stackalloc byte[7];
        return stream.Read(bytes) == bytes.Length && bytes.SequenceEqual("UnityFS"u8);
    }
    private static void RejectLinks(string root) { foreach (var _ in SafeFiles.Files(root)) { } }
}
