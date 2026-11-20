using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

/// <summary>Bakes legacy convex margins on a private candidate before project receipts are recorded.</summary>
internal static partial class LegacyConvexAssetMigration
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record Shape(string Guid, long SourceFileId, float SkinWidth);
    private sealed record Reference(string Path, long ColliderFileId, Shape Shape);
    private sealed record Request(int Index, string Path, string Guid, long SourceFileId, float SkinWidth);
    private sealed record BakeReport(int SchemaVersion, Replacement[] Replacements);
    private sealed record Replacement(int Index, long BakedFileId, bool UsedVertexLimit, string? Issue);
    private sealed record SourceHashes(string Asset, string Meta);
    private sealed record ChangedFile(string Path, string BeforeSha256, string AfterSha256);
    private sealed record Issue(string Path, long FileId, string Reason);
    private sealed record Journal(int SchemaVersion, string UnityVersion, int Candidates, int BakedShapes, int VertexLimitedShapes,
        int MigratedColliders, string? EditorLog, Issue[] Issues, ChangedFile[] Files);

    internal static async Task<string?> StageAsync(string repository, ConversionConfig config, string candidate,
        CancellationToken cancellation)
    {
        var references = new List<Reference>();
        var issues = new List<Issue>();
        var sourceFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var assets = Path.Combine(candidate, "Assets");
        foreach (var file in SafeFiles.Files(assets).Where(path => Path.GetExtension(path) is ".unity" or ".prefab"))
        {
            cancellation.ThrowIfCancellationRequested();
            string? text = ReadUnityYaml(file);
            if (text is null || !text.Contains("m_InflateMesh", StringComparison.Ordinal)) continue;
            string path = Relative(candidate, file);
            foreach (Match document in ColliderDocument().Matches(text))
            {
                string block = document.Value;
                if (!ConvexEnabled().IsMatch(block) || !InflationEnabled().IsMatch(block)) continue;
                long id = ReadId(document.Groups["id"].Value);
                var pointer = MeshPointer().Matches(block);
                var width = SkinWidth().Matches(block);
                if (pointer.Count != 1 || width.Count != 1 ||
                    !float.TryParse(width[0].Groups["width"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float skinWidth) ||
                    !float.IsFinite(skinWidth) || skinWidth <= 0 || ReadId(pointer[0].Groups["id"].Value) == 0)
                {
                    issues.Add(new(path, id, "Unsupported mesh pointer or skin width."));
                    continue;
                }
                references.Add(new(path, id, new Shape(pointer[0].Groups["guid"].Value, ReadId(pointer[0].Groups["id"].Value), skinWidth)));
                sourceFiles.TryAdd(path, SafeFiles.HashText(text));
            }
        }
        if (references.Count == 0 && issues.Count == 0)
            return null;

        int candidates = references.Count + issues.Count;
        var projectAssets = IndexMeshAssets(candidate, references.Select(row => row.Shape.Guid).ToHashSet(StringComparer.Ordinal));
        var requests = ResolveRequests(candidate, references.Select(row => row.Shape).Distinct(), projectAssets, issues, cancellation);
        string receipts = Path.Combine(config.Workspace, ".conversion");
        Directory.CreateDirectory(receipts);
        string journalPath = Path.Combine(receipts, "legacy-convex-assets.json");
        string? editorLog = null;
        var changes = new List<ChangedFile>();
        var bakedFileIds = new Dictionary<Shape, long>();
        int vertexLimitedShapes = 0;

        if (requests.Count != 0)
        {
            string run = Guid.NewGuid().ToString("N");
            string scratch = Path.Combine(config.Workspace, "legacy-convex-bake-" + run);
            string scratchProject = Path.Combine(scratch, "project");
            string plan = Path.Combine(scratch, "plan.json");
            string report = Path.Combine(scratch, "report.json");
            editorLog = Path.Combine(receipts, "legacy-convex-" + run + ".log");
            try
            {
                var expectedMeshes = StageBakeProject(repository, candidate, scratchProject, config.UnityVersion, requests);
                SafeFiles.AtomicJson(plan, new
                {
                    schemaVersion = 1,
                    unityVersion = config.UnityVersion,
                    requests = requests.Select(row => new
                    {
                        index = row.Index,
                        path = row.Path,
                        guid = row.Guid,
                        sourceFileId = row.SourceFileId,
                        skinWidth = row.SkinWidth
                    })
                });
                await Processes.Run(config.UnityEditor,
                    ["-batchmode", "-nographics", "-quit", "-projectPath", scratchProject,
                     "-executeMethod", "H3VRConverter.LegacyConvexBake.Run", "-logFile", editorLog,
                     "-nativeH3vrConvexPlan", plan, "-nativeH3vrConvexReport", report],
                    repository, editorLog + ".process", cancellation);
                var result = JsonSerializer.Deserialize<BakeReport>(File.ReadAllText(report), Json);
                if (result is null || result.SchemaVersion != 1 || result.Replacements is null ||
                    !result.Replacements.Select(row => row.Index).Order().SequenceEqual(requests.Select(row => row.Index)))
                    throw new InvalidDataException("Unity returned an incomplete convex bake report.");
                vertexLimitedShapes = result.Replacements.Count(row => row.UsedVertexLimit && row.BakedFileId != 0);
                AppendMeshes(candidate, scratchProject, requests, result.Replacements, expectedMeshes, bakedFileIds, changes, issues, cancellation);
            }
            finally
            {
                if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            }
        }

        int migrated = UpdateColliderPointers(candidate, references, bakedFileIds, sourceFiles, changes);
        SafeFiles.AtomicJson(journalPath, new Journal(1, config.UnityVersion, candidates, bakedFileIds.Count, vertexLimitedShapes, migrated,
            editorLog, issues.ToArray(), changes.ToArray()));
        Console.WriteLine($"Legacy convex assets: migrated {migrated}/{candidates} colliders using {bakedFileIds.Count} expanded meshes. See {journalPath}.");
        return journalPath;
    }

    private static Dictionary<string, string> IndexMeshAssets(string candidate, HashSet<string> guids)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var meta in SafeFiles.Files(Path.Combine(candidate, "Assets")).Where(path => path.EndsWith(".asset.meta", StringComparison.Ordinal)))
        {
            var match = AssetGuid().Match(ReadText(meta));
            if (!match.Success || !guids.Contains(match.Groups["guid"].Value)) continue;
            if (!paths.TryAdd(match.Groups["guid"].Value, Relative(candidate, meta[..^5])))
                throw new InvalidDataException("Ambiguous source Mesh GUID: " + match.Groups["guid"].Value);
        }
        return paths;
    }

    private static List<Request> ResolveRequests(string candidate, IEnumerable<Shape> shapes, Dictionary<string, string> assets,
        List<Issue> issues, CancellationToken cancellation)
    {
        var requests = new List<Request>();
        foreach (var group in shapes.OrderBy(shape => shape.Guid, StringComparer.Ordinal).GroupBy(shape => shape.Guid))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!assets.TryGetValue(group.Key, out var path) || !File.Exists(SafeFiles.Inside(candidate, path)))
            {
                foreach (var shape in group)
                    issues.Add(new(group.Key, shape.SourceFileId, "Source Mesh has no standalone native asset."));
                continue;
            }
            string? text = ReadUnityYaml(SafeFiles.Inside(candidate, path));
            var meshIds = text is null ? new HashSet<long>() :
                MeshDocument().Matches(text).Select(document => ReadId(document.Groups["id"].Value)).ToHashSet();
            foreach (var shape in group.OrderBy(shape => shape.SourceFileId).ThenBy(shape => shape.SkinWidth))
            {
                if (!meshIds.Contains(shape.SourceFileId))
                    issues.Add(new(path, shape.SourceFileId, "Source Mesh has no matching text document."));
                else
                    requests.Add(new(requests.Count, path, shape.Guid, shape.SourceFileId, shape.SkinWidth));
            }
        }
        return requests;
    }

    private static Dictionary<string, SourceHashes> StageBakeProject(string repository, string candidate, string scratch, string version,
        List<Request> requests)
    {
        var hashes = new Dictionary<string, SourceHashes>(StringComparer.Ordinal);
        foreach (string path in requests.Select(request => request.Path).Distinct(StringComparer.Ordinal))
        {
            string source = SafeFiles.Inside(candidate, path);
            string destination = SafeFiles.Inside(scratch, path);
            SafeFiles.Copy(source, destination);
            SafeFiles.Copy(source + ".meta", destination + ".meta");
            hashes.Add(path, new(SafeFiles.Hash(source), SafeFiles.Hash(source + ".meta")));
        }
        string editor = Path.Combine(scratch, "Assets", "Editor");
        SafeFiles.Copy(Path.Combine(repository, "editor", "LegacyConvexBake", "LegacyConvexBake.cs"), Path.Combine(editor, "LegacyConvexBake.cs"));
        SafeFiles.Copy(Path.Combine(repository, "runtime", "Compatibility", "LegacyConvexHull.cs"), Path.Combine(editor, "LegacyConvexHull.cs"));
        string settings = Directory.CreateDirectory(Path.Combine(scratch, "ProjectSettings")).FullName;
        File.WriteAllText(Path.Combine(settings, "ProjectVersion.txt"), $"m_EditorVersion: {version}\n");
        string packages = Directory.CreateDirectory(Path.Combine(scratch, "Packages")).FullName;
        File.WriteAllText(Path.Combine(packages, "manifest.json"), "{\"dependencies\":{\"com.unity.modules.physics\":\"1.0.0\",\"com.unity.modules.jsonserialize\":\"1.0.0\"}}\n");
        return hashes;
    }

    private static void AppendMeshes(string candidate, string scratch, List<Request> requests, Replacement[] replacements,
        Dictionary<string, SourceHashes> expected, Dictionary<Shape, long> fileIds, List<ChangedFile> changes, List<Issue> issues,
        CancellationToken cancellation)
    {
        var result = replacements.ToDictionary(row => row.Index);
        foreach (var group in requests.GroupBy(request => request.Path))
        {
            cancellation.ThrowIfCancellationRequested();
            string source = SafeFiles.Inside(candidate, group.Key);
            string original = ReadText(source);
            string before = SafeFiles.HashText(original);
            if (before != expected[group.Key].Asset || SafeFiles.Hash(source + ".meta") != expected[group.Key].Meta)
                throw new InvalidDataException("Source Mesh changed during the bake: " + group.Key);
            var knownIds = MeshDocument().Matches(original).Select(row => ReadId(row.Groups["id"].Value)).ToHashSet();
            var baked = MeshDocument().Matches(ReadText(SafeFiles.Inside(scratch, group.Key)))
                .ToDictionary(row => ReadId(row.Groups["id"].Value));
            var addition = new StringBuilder();
            foreach (var request in group)
            {
                var row = result[request.Index];
                if (!string.IsNullOrEmpty(row.Issue))
                {
                    if (row.BakedFileId != 0) throw new InvalidDataException("Conflicting convex bake result.");
                    issues.Add(new(group.Key, request.SourceFileId, row.Issue));
                    continue;
                }
                if (row.BakedFileId == 0 || knownIds.Contains(row.BakedFileId) ||
                    !baked.TryGetValue(row.BakedFileId, out var block) ||
                    MeshName().Match(block.Value).Groups["name"].Value != "NativeH3VR_LegacyConvex_" +
                        request.Index.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidDataException("Unity did not add the expected convex mesh: " + group.Key);
                var shape = new Shape(request.Guid, request.SourceFileId, request.SkinWidth);
                string idHash = SafeFiles.HashText($"{shape.Guid}:{shape.SourceFileId}:{shape.SkinWidth.ToString("R", CultureInfo.InvariantCulture)}");
                long id = Convert.ToInt64(idHash[..15], 16) | (1L << 60);
                if (!knownIds.Add(id)) throw new InvalidDataException("Generated convex Mesh local ID collision: " + group.Key);
                string newBlock = ReplaceGroup(block, "id", id);
                addition.Append(newBlock.TrimEnd('\r', '\n')).Append('\n');
                fileIds.Add(shape, id);
            }
            if (addition.Length == 0) continue;
            // Only new Mesh documents are appended. The original main object bytes
            // remain unchanged and its asset owns all the new subobjects.
            using (var output = new StreamWriter(source, append: true, Utf8))
            {
                if (!original.EndsWith('\n')) output.Write('\n');
                output.Write(addition.ToString());
            }
            changes.Add(new(group.Key, before, SafeFiles.Hash(source)));
        }
    }

    private static int UpdateColliderPointers(string candidate, List<Reference> references, Dictionary<Shape, long> meshes,
        Dictionary<string, string> beforeFiles, List<ChangedFile> changes)
    {
        int count = 0;
        foreach (var group in references.Where(row => meshes.ContainsKey(row.Shape)).GroupBy(row => row.Path))
        {
            string file = SafeFiles.Inside(candidate, group.Key);
            string original = ReadText(file);
            string before = SafeFiles.HashText(original);
            if (before != beforeFiles[group.Key]) throw new InvalidDataException("Collider source changed during the bake: " + group.Key);
            var planned = group.ToDictionary(row => row.ColliderFileId);
            string updated = ColliderDocument().Replace(original, document =>
            {
                if (!planned.Remove(ReadId(document.Groups["id"].Value), out var slot)) return document.Value;
                return MeshPointer().Replace(document.Value, pointer =>
                {
                    if (pointer.Groups["guid"].Value != slot.Shape.Guid || ReadId(pointer.Groups["id"].Value) != slot.Shape.SourceFileId)
                        throw new InvalidDataException("Collider mesh pointer changed during the bake.");
                    count++;
                    return ReplaceGroup(pointer, "id", meshes[slot.Shape]);
                }, 1);
            });
            if (planned.Count != 0 || updated == original) throw new InvalidDataException("Planned collider was not updated: " + group.Key);
            File.WriteAllText(file, updated, Utf8);
            changes.Add(new(group.Key, before, SafeFiles.Hash(file)));
        }
        return count;
    }

    private static string ReplaceGroup(Match match, string group, long replacement)
    {
        var field = match.Groups[group];
        int index = field.Index - match.Index;
        return match.Value.Remove(index, field.Length).Insert(index, replacement.ToString(CultureInfo.InvariantCulture));
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
    private static string? ReadUnityYaml(string path)
    {
        using var input = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[8];
        var header = prefix[..input.ReadAtLeast(prefix, prefix.Length, false)];
        if (!header.StartsWith("%YAML"u8) && !header.StartsWith("\uFEFF%YAML"u8))
            return null;
        return ReadText(path);
    }

    private static string ReadText(string path) => Utf8.GetString(File.ReadAllBytes(path));
    private static long ReadId(string id) => long.Parse(id, NumberStyles.Integer, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?m)^guid: (?<guid>[a-f0-9]{32})\r?$")]
    private static partial Regex AssetGuid();
    [GeneratedRegex(@"(?ms)^--- !u!64 &(?<id>-?\d+)\r?\n.*?(?=^--- |\z)")]
    private static partial Regex ColliderDocument();
    [GeneratedRegex(@"(?ms)^--- !u!43 &(?<id>-?\d+)\r?\n.*?(?=^--- |\z)")]
    private static partial Regex MeshDocument();
    [GeneratedRegex(@"(?m)^  m_Name: (?<name>[^\r\n]+)\r?$")]
    private static partial Regex MeshName();
    [GeneratedRegex(@"(?m)^  m_Convex: 1\r?$")]
    private static partial Regex ConvexEnabled();
    [GeneratedRegex(@"(?m)^  m_InflateMesh: 1\r?$")]
    private static partial Regex InflationEnabled();
    [GeneratedRegex(@"(?m)^  m_SkinWidth: (?<width>[^\r\n]+)\r?$")]
    private static partial Regex SkinWidth();
    [GeneratedRegex(@"(?m)^  m_Mesh: \{fileID: (?<id>-?\d+), guid: (?<guid>[a-f0-9]{32}), type: 2\}\r?$")]
    private static partial Regex MeshPointer();
}
