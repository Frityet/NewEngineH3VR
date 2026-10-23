using System.Text.RegularExpressions;

namespace H3VR.Converter;

public static class AssetBaseline
{
    public sealed record MissingScript(string asset, long gameObject, int count);

    // Record only explicitly null source script slots. A nonzero unresolved script
    // reference is always an error, even when another object was already missing a script.
    public static void Capture(string project)
    {
        var rows = new List<MissingScript>();
        foreach (var file in SafeFiles.Files(Path.Combine(project, "Assets")).Where(p => p.EndsWith(".unity") || p.EndsWith(".prefab")))
        {
            var text = File.ReadAllText(file);
            if (!text.StartsWith("%YAML", StringComparison.Ordinal)) continue;
            var counts = new Dictionary<long, int>();
            foreach (Match block in Regex.Matches(text, @"(?ms)^--- !u!114 &[^\r\n]+\r?\n(?:(?!^--- ).)*"))
            {
                if (!Regex.IsMatch(block.Value, @"(?m)^  m_Script: \{fileID: 0\}\r?$")) continue;
                var owner = Regex.Match(block.Value, @"(?m)^  m_GameObject: \{fileID: (-?\d+)\}");
                if (!owner.Success || !long.TryParse(owner.Groups[1].Value, out var id) || id == 0) continue;
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
            rows.AddRange(counts.Select(c => new MissingScript(Path.GetRelativePath(project, file).Replace('\\', '/'), c.Key, c.Value)));
        }
        SafeFiles.AtomicJson(Path.Combine(project, "ProjectSettings/H3VRBaseline.json"), new { missingScripts = rows });
    }
}
