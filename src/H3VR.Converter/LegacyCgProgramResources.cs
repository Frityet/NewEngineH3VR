using System.Text.RegularExpressions;

namespace H3VR.Converter;

internal static class LegacyCgProgramResources
{
    private static readonly Regex EmptyProgram = new(
        """
        \A%YAML 1\.1\r?\n%TAG !u! tag:unity3d\.com,2011:\r?\n--- !u!109 &\d+\r?\nCGProgram:\r?\n  m_ObjectHideFlags: 0\r?\n  m_PrefabParentObject: \{fileID: 0\}\r?\n  m_PrefabInternal: \{fileID: 0\}\r?\n  m_Name: [^\r\n]*\r?\n  m_Script:[ \t]*\r?\n  m_PathName:[ \t]*(?:\r?\n)?\z
        """, RegexOptions.CultureInvariant);
    private static readonly Regex GuidLine = new(@"(?m)^guid: (?<guid>[a-f0-9]{32})\r?$", RegexOptions.CultureInvariant);
    private static readonly Regex AssignedAssetBundle = new(@"(?m)^  assetBundleName:[ \t]*\S", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".asset", ".meta", ".unity", ".prefab", ".mat", ".anim", ".controller", ".overrideController",
        ".playable", ".spriteatlas", ".renderTexture", ".lighting", ".shader", ".compute", ".cginc", ".glslinc", ".hlsl"
    };

    internal static int Archive(string project, string archive)
    {
        string assets = Path.Combine(project, "Assets");
        var files = SafeFiles.Files(assets).ToArray();
        var programs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in files.Where(path => Path.GetExtension(path).Equals(".asset", StringComparison.OrdinalIgnoreCase) &&
                     Path.GetRelativePath(assets, path).Split(Path.DirectorySeparatorChar).Contains("Resources", StringComparer.OrdinalIgnoreCase)))
        {
            var header = File.ReadLines(asset).Take(4).ToArray();
            if (header.Length != 4 || !header[2].StartsWith("--- !u!109 ", StringComparison.Ordinal) || header[3] != "CGProgram:") continue;
            if (!EmptyProgram.IsMatch(File.ReadAllText(asset)))
                throw new InvalidDataException("Legacy CGProgram resource contains unsupported content: " + asset);
            string metadata = File.ReadAllText(asset + ".meta");
            var guid = GuidLine.Match(metadata);
            if (!guid.Success || AssignedAssetBundle.IsMatch(metadata) || !programs.TryAdd(guid.Groups["guid"].Value, asset))
                throw new InvalidDataException("A legacy CGProgram resource needs unique metadata and no bundle assignment: " + asset);
        }
        if (programs.Count == 0) return 0;

        var originals = programs.Values.SelectMany(asset => new[] { asset, asset + ".meta" }).ToHashSet(StringComparer.Ordinal);
        var pointers = new Regex(@"\bguid: (?:" + string.Join('|', programs.Keys) + @")\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        string settings = Path.Combine(project, "ProjectSettings");
        var candidates = Directory.Exists(settings) ? files.Concat(SafeFiles.Files(settings)) : files;
        foreach (var path in candidates.Where(path => TextExtensions.Contains(Path.GetExtension(path)) && !originals.Contains(path)))
        {
            if (File.ReadLines(path).Any(line => line.Contains("guid: ", StringComparison.Ordinal) && pointers.IsMatch(line)))
                throw new InvalidDataException("An obsolete CGProgram is still referenced by " + Path.GetRelativePath(project, path));
        }

        foreach (var asset in programs.Values)
        {
            var destination = SafeFiles.Inside(archive, Path.GetRelativePath(assets, asset));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(asset, destination);
            File.Move(asset + ".meta", destination + ".meta");
        }
        return programs.Count;
    }
}
