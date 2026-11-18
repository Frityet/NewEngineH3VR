using System.Text;
using System.Text.RegularExpressions;
using H3VR.Converter;

namespace NativeH3VR.ShaderOverlay;

internal static partial class ShaderProgramSplitter
{
    private const int SplitThreshold = 300;
    private const int EntriesPerInclude = 96;

    internal sealed record Branch(ulong Mask, string Condition, int Start, int End);
    internal sealed record Program(List<Branch> Branches, int FallbackStart, int FallbackEnd, int End);
    internal sealed record Include(string Path, string Sha256);
    internal sealed record Result(string Source, Include[] Includes);

    [GeneratedRegex(@"\G\s*#pragma vertex vert[ \t]*\r?\n[ \t]*#pragma fragment frag[ \t]*\r?\n")]
    private static partial Regex EntryPoints();

    [GeneratedRegex(@"(?m)^[ \t]*#(pragma|include\w*)\b([^\r\n]*)")]
    private static partial Regex LocalDirectives();

    internal static Result? Split(string source, Program program, string[] names, string overlay, string guid, int pass)
    {
        if (program.Branches.Count < SplitThreshold) return null;
        var variants = new List<Branch>(program.Branches.Count);
        foreach (var branch in program.Branches)
        {
            // ShaderLab entry point pragmas must stay in the CGPROGRAM.
            var entry = EntryPoints().Match(source, branch.Start, branch.End - branch.Start);
            if (!entry.Success) return null;
            var body = entry.Index + entry.Length;
            for (var match = LocalDirectives().Match(source, body, branch.End - body); match.Success; match = match.NextMatch())
                if (match.Groups[1].Value != "include" || match.Groups[2].Value.Trim() != "\"UnityCG.cginc\"")
                    return null;
            variants.Add(branch with { Start = body });
        }

        var includes = new List<Include>();
        var shader = new StringBuilder("#pragma vertex vert\n#pragma fragment frag\n");
        // Compile each guard as originally emitted. The outer tree only routes
        // a keyword combination to the include that could contain its row.
        WriteTree(variants);
        return new(shader.ToString(), includes.ToArray());

        void WriteTree(List<Branch> rows)
        {
            if (rows.Count <= EntriesPerInclude)
            {
                WriteInclude(rows);
                return;
            }
            var bestIndex = -1;
            var imbalance = rows.Count;
            for (var index = 0; index < names.Length; index++)
            {
                var bit = 1UL << index;
                var matches = rows.Count(row => (row.Mask & bit) != 0);
                var difference = Math.Abs(rows.Count - 2 * matches);
                if (difference >= imbalance) continue;
                bestIndex = index;
                imbalance = difference;
            }
            if (bestIndex < 0)
                throw new InvalidDataException("The shader program contains duplicate variant guards.");

            var chosen = 1UL << bestIndex;
            var enabled = new List<Branch>();
            var disabled = new List<Branch>();
            foreach (var row in rows)
                ((row.Mask & chosen) != 0 ? enabled : disabled).Add(row);

            shader.Append("#if defined(").Append(names[bestIndex]).AppendLine(")");
            WriteTree(enabled);
            shader.AppendLine("#else");
            WriteTree(disabled);
            shader.AppendLine("#endif");
        }

        void WriteInclude(List<Branch> rows)
        {
            var relative = $"Assets/NativeH3VRShaderPrograms/{guid}/pass-{pass}-{includes.Count}.cginc";
            var path = SafeFiles.Inside(overlay, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    var row = rows[index];
                    writer.Write(index == 0 ? "#if " : "#elif ");
                    writer.WriteLine(row.Condition);
                    writer.Write(source.AsSpan(row.Start, row.End - row.Start));
                }
                writer.Write("#else");
                writer.Write(source.AsSpan(program.FallbackStart, program.FallbackEnd - program.FallbackStart));
                writer.WriteLine("#endif");
            }
            includes.Add(new(relative, SafeFiles.Hash(path)));
            shader.Append("#include \"").Append(relative).AppendLine("\"");
        }
    }
}
