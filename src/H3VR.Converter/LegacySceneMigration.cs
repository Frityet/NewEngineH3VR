using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

/// <summary>Repairs only uniquely linked recovered text scenes and preserves their baked lighting payload.</summary>
public static class LegacySceneMigration
{
    private static readonly Regex Document = new(@"(?ms)^--- !u!(?<class>\d+) &(?<id>-?\d+)[^\r\n]*\r?\n.*?(?=^--- !u!|\z)", RegexOptions.Compiled);
    private static readonly Regex Guid = new(@"(?m)^guid: ([0-9a-f]{32})\r?$", RegexOptions.Compiled);
    private static readonly Regex Lighting = new(@"(?m)^  m_LightingDataAsset: \{fileID: 112000000, guid: ([0-9a-f]{32}), type: 2\}\r?$", RegexOptions.Compiled);
    private sealed record Edit(string Path, byte[] Before, byte[] After);

    public static void Apply(string project)
    {
        string assets = Path.Combine(Path.GetFullPath(project), "Assets");
        string scenes = Path.Combine(assets, "Scenes");
        if (!Directory.Exists(scenes)) return;
        var metas = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string meta in Directory.EnumerateFiles(assets, "*.asset.meta", SearchOption.AllDirectories))
        {
            var match = Guid.Match(File.ReadAllText(meta));
            if (!match.Success) throw new InvalidDataException("Missing asset GUID: " + meta);
            if (!metas.TryAdd(match.Groups[1].Value, meta[..^5])) throw new InvalidDataException("Duplicate asset GUID: " + match.Groups[1].Value);
        }
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var edits = new List<Edit>();
        var scenePaths = Directory.EnumerateFiles(scenes, "*.unity", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var identities = PlanSceneGuids(Path.GetFullPath(project), assets, scenePaths, metas, edits);
        foreach (string path in scenePaths)
        {
            byte[] before = File.ReadAllBytes(path); string text = Decode(before, path);
            if (!text.StartsWith("%YAML", StringComparison.Ordinal)) throw new InvalidDataException("Unsupported non-text scene: " + path);
            var matches = Lighting.Matches(text);
            if (matches.Count > 1) throw new InvalidDataException("Ambiguous lighting references: " + path);
            if (matches.Count == 0 && Regex.IsMatch(text, @"(?m)^--- !u!157 &"))
                throw new InvalidDataException("Unsupported lighting reference: " + path);
            if (matches.Count == 1)
            {
                string lightingGuid = matches[0].Groups[1].Value;
                if (!metas.TryGetValue(lightingGuid, out string? lightingPath) || !File.Exists(lightingPath)) throw new InvalidDataException("Unresolved lighting asset: " + lightingGuid);
                if (!owners.Add(lightingPath)) throw new InvalidDataException("Lighting asset has multiple scene owners: " + lightingPath);
                string sceneMeta = path + ".meta";
                var sceneGuid = File.Exists(sceneMeta) ? Guid.Match(File.ReadAllText(sceneMeta)) : Match.Empty;
                if (!sceneGuid.Success) throw new InvalidDataException("Missing scene GUID: " + path);
                byte[] original = File.ReadAllBytes(lightingPath); string payload = Decode(original, lightingPath);
                if (!payload.StartsWith("%YAML", StringComparison.Ordinal) || !Regex.IsMatch(payload, @"(?m)^--- !u!1120 &112000000\r?$")) throw new InvalidDataException("Unsupported lighting asset: " + lightingPath);
                var existing = Regex.Matches(payload, @"(?m)^  m_Scene: [^\r\n]*\r?\n");
                if (existing.Count > 1) throw new InvalidDataException("Ambiguous lighting scene reference: " + lightingPath);
                string newline = payload.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                string previousGuid = sceneGuid.Groups[1].Value;
                string selectedGuid = identities.TryGetValue(path, out var replacement) ? replacement : previousGuid;
                string field = "  m_Scene: {fileID: 102900000, guid: " + selectedGuid + ", type: 3}" + newline;
                string updated;
                if (existing.Count == 1)
                {
                    string previousField = "  m_Scene: {fileID: 102900000, guid: " + previousGuid + ", type: 3}" + newline;
                    if (existing[0].Value != field && !(selectedGuid != previousGuid && existing[0].Value == previousField))
                        throw new InvalidDataException("Lighting scene reference has a different owner: " + lightingPath);
                    updated = payload.Remove(existing[0].Index, existing[0].Length).Insert(existing[0].Index, field);
                }
                else
                {
                    var marker = Regex.Matches(payload, @"(?m)^  m_Lightmaps:");
                    if (marker.Count != 1) throw new InvalidDataException("Missing or ambiguous lightmaps field: " + lightingPath);
                    updated = payload.Insert(marker[0].Index, field);
                    if (updated.Remove(marker[0].Index, field.Length) != payload) throw new InvalidDataException("Lighting payload changed: " + lightingPath);
                }
                Add(edits, lightingPath, original, updated);
            }
            string updatedScene = RemoveGuiLayers(text, path);
            Add(edits, path, before, updatedScene);
        }
        foreach (var e in edits) if (!File.ReadAllBytes(e.Path).AsSpan().SequenceEqual(e.Before)) throw new IOException("Migration input changed during preflight: " + e.Path);
        var committed = new List<Edit>();
        try
        {
            foreach (var e in edits)
            {
                string temp = e.Path + ".nativeh3vr-" + System.Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temp, e.After); File.Move(temp, e.Path, true); committed.Add(e); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        catch
        {
            foreach (var e in committed.AsEnumerable().Reverse()) File.WriteAllBytes(e.Path, e.Before);
            throw;
        }
    }

    private static Dictionary<string, string> PlanSceneGuids(string project, string assets, string[] scenes,
        Dictionary<string, string> assetMetas, List<Edit> edits)
    {
        var sceneSet = scenes.ToHashSet(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var allGuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string meta in Directory.EnumerateFiles(assets, "*.meta", SearchOption.AllDirectories))
        {
            var match = Guid.Match(File.ReadAllText(meta));
            if (!match.Success) throw new InvalidDataException("Missing asset GUID: " + meta);
            string guid = match.Groups[1].Value; allGuids.Add(guid);
            if (!groups.TryGetValue(guid, out var paths)) groups.Add(guid, paths = new());
            paths.Add(meta);
        }
        var duplicated = groups.Where(p => p.Value.Count > 1 && p.Value.Any(m => m.EndsWith(".unity.meta", StringComparison.Ordinal))).ToArray();
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        if (duplicated.Length == 0) return replacements;
        string settingsPath = Path.Combine(project, "ProjectSettings", "EditorBuildSettings.asset");
        if (!File.Exists(settingsPath)) throw new InvalidDataException("Duplicate scene GUIDs require EditorBuildSettings.asset.");
        byte[] settingsBytes = File.ReadAllBytes(settingsPath); string settings = Decode(settingsBytes, settingsPath);
        var duplicates = duplicated.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var allowedLighting = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in duplicated)
        {
            if (group.Value.Any(m => !m.EndsWith(".unity.meta", StringComparison.Ordinal) || !sceneSet.Contains(m[..^5])))
                throw new InvalidDataException("Duplicate scene GUID collides with a non-scene asset: " + group.Key);
            foreach (string meta in group.Value.Order(StringComparer.Ordinal))
            {
                string path = meta[..^5]; string relative = Path.GetRelativePath(project, path).Replace('\\', '/');
                string replacement = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("NativeH3VR.SceneAsset:" + relative)))[..32];
                if (!allGuids.Add(replacement)) throw new InvalidDataException("Generated scene GUID collision: " + relative);
                replacements.Add(path, replacement);
                byte[] original = File.ReadAllBytes(meta); string content = Decode(original, meta);
                var guid = Guid.Match(content);
                if (!guid.Success || Guid.Matches(content).Count != 1 || guid.Groups[1].Value != group.Key) throw new InvalidDataException("Ambiguous scene metadata GUID: " + meta);
                string revised = content.Remove(guid.Groups[1].Index, 32).Insert(guid.Groups[1].Index, replacement);
                Add(edits, meta, original, revised);
                string pattern = @"(?m)^(?<prefix>    path: " + Regex.Escape(relative) + @"\r?\n    guid: )(?<guid>[0-9a-f]{32})(?<end>\r?$)";
                var entry = Regex.Matches(settings, pattern);
                if (entry.Count != 1 || entry[0].Groups["guid"].Value != group.Key) throw new InvalidDataException("Missing or ambiguous build scene identity: " + relative);
                var g = entry[0].Groups["guid"]; settings = settings.Remove(g.Index, 32).Insert(g.Index, replacement);
                string sceneText = Decode(File.ReadAllBytes(path), path); var light = Lighting.Matches(sceneText);
                if (light.Count > 1) throw new InvalidDataException("Ambiguous scene lighting: " + path);
                if (light.Count == 1)
                {
                    if (!assetMetas.TryGetValue(light[0].Groups[1].Value, out var lightingPath) || !allowedLighting.TryAdd(lightingPath, group.Key))
                        throw new InvalidDataException("Ambiguous scene lighting owner: " + path);
                }
            }
        }
        // Only original scene metadata and the owning lighting m_Scene pointers
        // may contain these duplicated asset references. Internal scene GUIDs
        // use a different field and remain untouched.
        foreach (string file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(project, "ProjectSettings"), "*", SearchOption.AllDirectories)))
        {
            if (file == settingsPath) continue;
            using var stream = File.OpenRead(file);
            if (!file.EndsWith(".meta", StringComparison.Ordinal))
            {
                Span<byte> prefix = new byte[5];
                if (stream.Read(prefix) != 5 || !prefix.SequenceEqual("%YAML"u8)) continue;
                stream.Position = 0;
            }
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.Contains("guid:", StringComparison.Ordinal)) continue;
                foreach (string newGuid in replacements.Values)
                    if (line.Contains("guid: " + newGuid, StringComparison.Ordinal)) throw new InvalidDataException("Generated scene GUID already referenced in " + file);
                foreach (var group in duplicates)
                {
                    if (!line.Contains("guid: " + group.Key, StringComparison.Ordinal)) continue;
                    if (file.EndsWith(".unity.meta", StringComparison.Ordinal) && group.Value.Contains(file) && line == "guid: " + group.Key) continue;
                    string permitted = "  m_Scene: {fileID: 102900000, guid: " + group.Key + ", type: 3}";
                    if (!allowedLighting.TryGetValue(file, out string? owner) || owner != group.Key || line != permitted)
                        throw new InvalidDataException("Unknown reference to duplicated scene GUID in " + file);
                }
            }
        }
        foreach (var group in duplicates)
            if (Regex.IsMatch(settings, @"(?m)^\s*guid: " + Regex.Escape(group.Key) + @"\r?$"))
                throw new InvalidDataException("Unknown build settings reference to duplicated scene GUID: " + group.Key);
        Add(edits, settingsPath, settingsBytes, settings);
        return replacements;
    }

    private static string RemoveGuiLayers(string source, string path)
    {
        var docs = Document.Matches(source).Cast<Match>().ToArray();
        var gui = docs.Where(x => x.Groups["class"].Value == "92").ToArray();
        if (gui.Length == 0) return source;
        if (gui.Select(x => x.Groups["id"].Value).Distinct(StringComparer.Ordinal).Count() != gui.Length)
            throw new InvalidDataException("Duplicate GUI Layer file ID: " + path);
        var removals = new List<(int Start, int Length)>();
        foreach (var layer in gui)
        {
            string id = layer.Groups["id"].Value;
            var owner = Regex.Matches(layer.Value, @"(?m)^  m_GameObject: \{fileID: (-?\d+)\}\r?$");
            if (owner.Count != 1) throw new InvalidDataException("Invalid GUI Layer owner: " + path);
            string ownerId = owner[0].Groups[1].Value;
            var objects = docs.Where(d => d.Groups["class"].Value == "1" && d.Groups["id"].Value == ownerId).ToArray();
            if (objects.Length != 1) throw new InvalidDataException("Ambiguous GUI Layer owner: " + path);
            var refs = Regex.Matches(source, @"\{fileID: " + Regex.Escape(id) + @"\}");
            var componentLists = Regex.Matches(objects[0].Value, @"(?m)^  m_Component:\r?\n(?<entries>(?:  - component: \{fileID: -?\d+\}\r?\n)+)");
            if (componentLists.Count != 1) throw new InvalidDataException("Invalid GameObject component list: " + path);
            var entries = componentLists[0].Groups["entries"];
            var entry = Regex.Matches(entries.Value, @"(?m)^  - component: \{fileID: " + Regex.Escape(id) + @"\}\r?\n");
            if (refs.Count != 1 || entry.Count != 1) throw new InvalidDataException("GUI Layer has ambiguous component references: " + path);
            removals.Add((objects[0].Index + entries.Index + entry[0].Index, entry[0].Length));
            removals.Add((layer.Index, layer.Length));
        }
        foreach (var item in removals.OrderByDescending(x => x.Start)) source = source.Remove(item.Start, item.Length);
        return source;
    }

    private static string Decode(byte[] bytes, string path)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Unsupported non-UTF8 asset: " + path, e); }
    }
    private static void Add(List<Edit> edits, string path, byte[] original, string result)
    {
        byte[] updated = new UTF8Encoding(false).GetBytes(result);
        if (!original.AsSpan().SequenceEqual(updated)) edits.Add(new(path, original, updated));
    }
}
