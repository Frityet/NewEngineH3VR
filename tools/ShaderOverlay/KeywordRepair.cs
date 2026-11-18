using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using K4os.Compression.LZ4;

namespace NativeH3VR.ShaderOverlay;

/// <summary>Recover keyword declarations from the paired original Direct3D programs.</summary>
internal sealed partial class KeywordRepair : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly AssetsManager manager = new();
    private readonly AssetsFileInstance original;

    internal sealed record PassRepair(string Name, int PassIndex, int VariantCount, ShaderKeywordPlan Keywords);
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

    [GeneratedRegex(@"(?m)^[ \t]*Pass[ \t]*\{[ \t]*\r?$")]
    private static partial Regex PassStarts();

    [GeneratedRegex(@"(?m)^[ \t]*CGPROGRAM[ \t]*\r?\n[ \t]*#pragma target [^\r\n]+\r?\n(?<declarations>(?:[ \t]*#pragma shader_feature __ [A-Za-z_]\w*[ \t]*\r?\n)+)(?=[ \t]*#if )")]
    private static partial Regex ProgramHeaders();

    [GeneratedRegex(@"(?m)^[ \t]*#(if|ifdef|ifndef|elif|else|endif)\b([^\r\n]*)")]
    private static partial Regex ConditionalDirectives();

    public PassRepair[] Repair(string path, long pathId)
    {
        var source = File.ReadAllText(path, Utf8);
        if (!source.Contains("#pragma shader_feature __ ", StringComparison.Ordinal)) return [];
        var locations = PassStarts().Matches(source);
        var info = original.file.GetAssetInfo(pathId) ?? throw new InvalidDataException($"Missing original shader object {pathId}.");
        var asset = manager.GetBaseField(original, info);
        var parsed = asset["m_ParsedForm"];
        var platform = List(asset["platforms"]).FindIndex(platform => platform.AsUInt == 4);
        if (parsed.IsDummy || platform < 0) return [];

        var originalPasses = List(parsed["m_SubShaders"]).SelectMany(subShader => List(subShader["m_Passes"]))
            .Where(pass => pass["m_Type"].AsInt == 0).ToArray();
        if (originalPasses.Length != locations.Count) return [];
        var programs = DecodePrograms(asset, platform);
        var edits = new List<Edit>();
        for (var index = 0; index < locations.Count; index++)
        {
            var match = locations[index];
            var limit = index + 1 < locations.Count ? locations[index + 1].Index : source.Length;
            var edit = Plan(source, index, match.Index + match.Length, limit, originalPasses[index], programs);
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

    private static Edit? Plan(string source, int index, int start, int end, AssetTypeValueField pass, ProgramKeywords[] programs)
    {
        var header = ProgramHeaders().Match(source, start, end - start);
        if (!header.Success) return null;
        var name = pass["m_State"]["m_Name"].AsString;
        if (PassNames().Match(source, start, header.Index - start).Groups[1].Value != name) return null;
        var declarations = header.Groups["declarations"];
        var names = declarations.Value.TrimEnd().Split('\n').Select(line => line.Trim().Split(' ')[^1]).ToArray();
        if (names.Length > 64 || names.Distinct(StringComparer.Ordinal).Count() != names.Length) return null;
        var positions = names.Select((keyword, bit) => (keyword, mask: 1UL << bit))
            .ToDictionary(row => row.keyword, row => row.mask, StringComparer.Ordinal);

        var rows = ReadGuards(source, header.Index + header.Length, end, positions);
        if (rows is null) return null;
        var vertex = Stage(pass, "progVertex", programs, 15, 16);
        var fragment = Stage(pass, "progFragment", programs, 17, 18);
        var paired = Pair(vertex, fragment, positions);
        if (paired is null || !rows.SetEquals(paired)) return null;

        var plan = ShaderKeywordPlan.Create(names, paired);
        return plan.ChangesDeclarations
            ? new(declarations.Index, declarations.Index + declarations.Length,
                string.Join("\n", plan.Directives()) + "\n", new(name, index, paired.Count, plan))
            : null;
    }

    private static HashSet<ulong>? ReadGuards(string source, int start, int end, Dictionary<string, ulong> positions)
    {
        var rows = new HashSet<ulong>();
        var depth = 0;
        var fallback = false;
        for (var match = ConditionalDirectives().Match(source, start, end - start); match.Success; match = match.NextMatch())
        {
            var directive = match.Groups[1].Value;
            if (depth == 0 || depth == 1 && directive == "elif")
            {
                if (fallback || directive is not ("if" or "elif") ||
                    !TryReadGuard(match.Groups[2].Value.Trim(), positions, out var mask) || !rows.Add(mask)) return null;
            }
            if (directive is "if" or "ifdef" or "ifndef") depth++;
            if (directive == "else" && depth == 1)
            {
                var tail = source.AsSpan(match.Index + match.Length).TrimStart();
                if (!tail.StartsWith("#error Original shader has no recovered variant", StringComparison.Ordinal)) return null;
                fallback = true;
            }
            if (directive == "endif" && --depth == 0) return fallback ? rows : null;
        }
        return null;
    }

    internal static bool TryReadGuard(string expression, Dictionary<string, ulong> positions, out ulong enabled)
    {
        var found = 0UL;
        enabled = 0UL;
        foreach (var term in expression.Split(" && ", StringSplitOptions.None))
        {
            var negative = term.StartsWith('!');
            var condition = negative ? term[1..] : term;
            if (!condition.StartsWith("defined(", StringComparison.Ordinal) || !condition.EndsWith(')') ||
                !positions.TryGetValue(condition[8..^1], out var bit) || (found & bit) != 0)
                return false;
            found |= bit;
            if (!negative) enabled |= bit;
        }
        return found == positions.Values.Aggregate(0UL, (mask, bit) => mask | bit);
    }

    private static List<ulong>? Pair(ProgramKeywords[] vertex, ProgramKeywords[] fragment, Dictionary<string, ulong> positions)
    {
        if (vertex.Length == 0 || fragment.Length == 0) return null;
        if (vertex.Concat(fragment).SelectMany(row => row.Keywords).Any(keyword => !positions.ContainsKey(keyword)))
            return null;
        ulong Mask(ProgramKeywords row) => row.Keywords.Aggregate(0UL, (mask, key) => mask | positions[key]);
        var first = vertex.Select(Mask).Distinct().ToArray();
        var second = fragment.Select(Mask).Distinct().ToArray();
        var shared = first.Aggregate(0UL, (mask, row) => mask | row) & second.Aggregate(0UL, (mask, row) => mask | row);
        var fragments = second.ToLookup(mask => mask & shared);
        var compatible = first.SelectMany(left => fragments[left & shared].Select(right => left | right));
        return compatible.Distinct().ToList();
    }

    private static List<AssetTypeValueField> List(AssetTypeValueField field) =>
        field.IsDummy || field["Array"].IsDummy ? [] : field["Array"].Children;

    private static ProgramKeywords[] Stage(AssetTypeValueField pass, string name, ProgramKeywords[] programs, int sm40, int sm50)
    {
        var result = new List<ProgramKeywords>();
        foreach (var row in List(pass[name]["m_SubPrograms"]))
        {
            var type = row["m_GpuProgramType"];
            if (!type.IsDummy && type.AsInt != sm40 && type.AsInt != sm50) continue;
            var index = row["m_BlobIndex"].AsUInt;
            if (index >= programs.Length) throw new InvalidDataException("Shader program index is outside the original platform table.");
            var program = programs[index];
            if (program.Kind == sm40 || program.Kind == sm50) result.Add(program);
        }
        return result.ToArray();
    }

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
