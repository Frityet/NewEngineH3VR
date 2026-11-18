using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using K4os.Compression.LZ4;

namespace NativeH3VR.ShaderOverlay;

/// <summary>Replace independent booleans only where the installed paired programs prove a one-of group.</summary>
internal sealed partial class KeywordRepair : IDisposable
{
    private static readonly Dictionary<string, string[]> Domains = new(StringComparer.Ordinal)
    {
        ["FORWARD"] = ["DIRECTIONAL"],
        ["FORWARD_DELTA"] = ["DIRECTIONAL", "DIRECTIONAL_COOKIE", "POINT", "POINT_COOKIE", "SPOT"],
        ["SHADOWCASTER"] = ["SHADOWS_CUBE", "SHADOWS_DEPTH"]
    };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly AssetsManager manager = new();
    private readonly AssetsFileInstance original;

    internal sealed record PassRepair(string Name, string[] Required);
    private sealed record Edit(int Start, int End, string Header, PassRepair Repair);
    private sealed record ProgramKeywords(int Kind, HashSet<string> Keywords);

    public KeywordRepair(string path, string classDatabase)
    {
        original = manager.LoadAssetsFile(path, false);
        if (!original.file.Metadata.TypeTreeEnabled)
        {
            manager.LoadClassPackage(classDatabase);
            manager.LoadClassDatabaseFromPackage(original.file.Metadata.UnityVersion);
        }
    }

    [GeneratedRegex("(?m)^[\\t ]*Name[\\t ]+\"([^\"]+)\"[\\t ]*$")]
    private static partial Regex PassNames();

    public PassRepair[] Repair(string path, long pathId)
    {
        var source = File.ReadAllText(path, Utf8);
        var locations = PassNames().Matches(source);
        var counts = locations.GroupBy(match => match.Groups[1].Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (!counts.Keys.Any(Domains.ContainsKey)) return [];
        var info = original.file.GetAssetInfo(pathId) ?? throw new InvalidDataException($"Missing original shader object {pathId}.");
        var asset = manager.GetBaseField(original, info);
        var parsed = asset["m_ParsedForm"];
        var platform = List(asset["platforms"]).FindIndex(platform => platform.AsUInt == 4);
        if (parsed.IsDummy || platform < 0) return [];

        var originalPasses = List(parsed["m_SubShaders"]).SelectMany(subShader => List(subShader["m_Passes"]))
            .ToLookup(pass => pass["m_State"]["m_Name"].AsString, StringComparer.Ordinal);
        var programs = DecodePrograms(asset, platform);
        var edits = new List<Edit>();
        for (var index = 0; index < locations.Count; index++)
        {
            var match = locations[index];
            var name = match.Groups[1].Value;
            if (!Domains.TryGetValue(name, out var group) || counts[name] != 1 || originalPasses[name].Count() != 1) continue;
            var pass = originalPasses[name].Single();
            var vertex = Stage(pass, "progVertex", programs, 15, 16);
            var fragment = Stage(pass, "progFragment", programs, 17, 18);
            if (vertex.Length == 0 || fragment.Length == 0 || vertex.Concat(fragment).Any(row => group.Count(row.Keywords.Contains) != 1))
                continue;
            var limit = index + 1 < locations.Count ? locations[index + 1].Index : source.Length;
            var edit = Plan(source, name, group, match.Index + match.Length, limit);
            if (edit is not null) edits.Add(edit);
        }

        if (edits.Count == 0) return [];
        using var stream = File.CreateText(path);
        var head = 0;
        foreach (var edit in edits)
        {
            stream.Write(source.AsSpan(head, edit.Start - head));
            stream.Write(edit.Header);
            head = edit.End;
        }
        stream.Write(source.AsSpan(head));
        return edits.Select(edit => edit.Repair).ToArray();
    }

    private static Edit? Plan(string source, string name, string[] group, int start, int end)
    {
        var program = source.IndexOf("CGPROGRAM", start, end - start, StringComparison.Ordinal);
        if (program == -1) return null;
        var target = source.IndexOf("#pragma target", program, end - program, StringComparison.Ordinal);
        if (target == -1) return null;
        var newline = source.IndexOf('\n', target, end - target);
        if (newline == -1) return null;
        var first = newline + 1;
        var last = source.IndexOf("#if", first, end - first, StringComparison.Ordinal);
        if (last == -1) return null;

        var declarations = source[first..last];
        foreach (var keyword in group)
        {
            var pragma = $"#pragma shader_feature __ {keyword}\n";
            var occurrence = declarations.IndexOf(pragma, StringComparison.Ordinal);
            if (occurrence == -1 || declarations.IndexOf(pragma, occurrence + pragma.Length, StringComparison.Ordinal) >= 0)
                return null;
            declarations = declarations.Remove(occurrence, pragma.Length);
        }
        var mode = group.Length == 1 ? $"#define {group[0]} 1\n" : $"#pragma multi_compile {string.Join(" ", group)}\n";
        return new(first, last, mode + declarations, new(name, group));
    }

    private static List<AssetTypeValueField> List(AssetTypeValueField field) =>
        field.IsDummy || field["Array"].IsDummy ? [] : field["Array"].Children;

    private static ProgramKeywords[] Stage(AssetTypeValueField pass, string name, ProgramKeywords[] programs, int sm40, int sm50) =>
        List(pass[name]["m_SubPrograms"]).Select(row => programs[checked((int)row["m_BlobIndex"].AsUInt)])
            .Where(program => program.Kind == sm40 || program.Kind == sm50).ToArray();

    private static ProgramKeywords[] DecodePrograms(AssetTypeValueField shader, int platform)
    {
        var blob = shader["compressedBlob"];
        if (blob.IsDummy) throw new InvalidDataException("Original Shader has no compressed variant table.");
        var packed = blob["Array"];
        var compressed = blob.Value?.ValueType == AssetValueType.ByteArray ? blob.AsByteArray
            : packed.Value?.ValueType == AssetValueType.ByteArray ? packed.AsByteArray
            : List(blob).Select(value => value.AsByte).ToArray();
        var offset = checked((int)List(shader["offsets"])[platform].AsUInt);
        var length = checked((int)List(shader["compressedLengths"])[platform].AsUInt);
        var expected = checked((int)List(shader["decompressedLengths"])[platform].AsUInt);
        var bytes = new byte[expected];
        if (LZ4Codec.Decode(compressed, offset, length, bytes, 0, expected) != expected)
            throw new InvalidDataException("Incomplete decompression of the original Direct3D variant table.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Utf8);
        var count = reader.ReadInt32();
        const int entrySize = 8;
        if (count < 0 || count > (stream.Length - 4) / entrySize) throw new InvalidDataException("Invalid Unity 5 shader variant count.");
        var programs = new ProgramKeywords[count];
        for (var index = 0; index < count; index++)
        {
            stream.Position = 4 + index * entrySize;
            stream.Position = reader.ReadInt32();
            var version = reader.ReadInt32();
            var kind = reader.ReadInt32();
            stream.Position += version >= 201608170 ? 16 : 12;
            var words = reader.ReadInt32();
            if (words < 0 || words > 512) throw new InvalidDataException("Invalid Unity 5 shader keyword count.");
            var keywords = new HashSet<string>(StringComparer.Ordinal);
            for (var word = 0; word < words; word++)
            {
                var size = reader.ReadInt32();
                if (size < 0 || size > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid Unity 5 shader keyword length.");
                keywords.Add(Utf8.GetString(reader.ReadBytes(size)));
                stream.Position = (stream.Position + 3) & ~3L;
            }
            programs[index] = new(kind, keywords);
        }
        return programs;
    }

    public void Dispose() => manager.UnloadAllAssetsFiles(true);
}
