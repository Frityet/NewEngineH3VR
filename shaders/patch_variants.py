from pathlib import Path

p = (
    Path(__file__).resolve().parents[1]
    / ".tools/assetripper-source-1.0.15/Source/AssetRipper.Export.UnityProjects/Shaders/USCShaderExporter.cs"
)
s = p.read_text()
pos = s.index("\n\t\tprivate static void ExportPassDecomp")
s = (
    s[:pos]
    + """
        private static bool PreserveVariantsFor(IShader shader) {
            if (Environment.GetEnvironmentVariable("H3VR_VARIANT_MODE") != "optics") return false;
            string n = shader.ParsedForm.Name.String;
            return new[] {"Scope", "Reticle", "Optic", "Reflex", "Holograp"}.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
        private static HashSet<string> Keys(ShaderSubProgram p) => p.LocalKeywords.Concat(p.GlobalKeywords).ToHashSet();
        private sealed record ProgramPair(ShaderSubProgram? Vertex, ShaderSubProgram? Fragment, string[] Keywords);
        private static List<ProgramPair> PairPrograms(List<ShaderSubProgram>? vertices, List<ShaderSubProgram>? fragments, bool preserve) {
            if (!preserve) return new(){new(vertices?.First(), fragments?.First(), Array.Empty<string>())};
            var vs=(vertices ?? new()).GroupBy(p=>string.Join(" ",Keys(p).OrderBy(k=>k))).Select(g=>g.First()).ToList();
            var fs=(fragments ?? new()).GroupBy(p=>string.Join(" ",Keys(p).OrderBy(k=>k))).Select(g=>g.First()).ToList();
            var vk=vs.SelectMany(Keys).ToHashSet();var fk=fs.SelectMany(Keys).ToHashSet();
            var shared=vk.Intersect(fk).ToHashSet();
            var result=new List<ProgramPair>();
            foreach(var v in vs) foreach(var f in fs) {
                var a=Keys(v);var b=Keys(f);
                if(!a.Intersect(shared).ToHashSet().SetEquals(b.Intersect(shared))) continue;
                result.Add(new(v,f,a.Union(b).OrderBy(k=>k).ToArray()));
            }
            if(result.Count==0) throw new InvalidOperationException("No compatible original vertex/fragment variants");
            return result.GroupBy(p=>string.Join(" ",p.Keywords)).Select(g=>g.First()).OrderByDescending(p=>p.Keywords.Length).ToList();
        }
"""
    + s[pos:]
)
s = s.replace(
    "ShaderSubProgram? vertexSubProgram = null;",
    """bool preserveVariants = PreserveVariantsFor(writer.Shader);
                    var pairs = PairPrograms(vertexSubPrograms, fragmentSubPrograms, preserveVariants);
                    var allKeywords = pairs.SelectMany(p => p.Keywords).Distinct().OrderBy(k=>k).ToArray();
                    writer.WriteLine("CGPROGRAM");
                    writer.WriteLine("#pragma target 4.5");
                    if (preserveVariants) foreach(var keyword in allKeywords) writer.WriteLine($"#pragma shader_feature __ {keyword}");
                    int pairIndex = 0;
                    foreach (var pair in pairs) {
                    if(preserveVariants) {
                        string condition=allKeywords.Length==0 ? "1" : string.Join(" && ",allKeywords.Select(k=>pair.Keywords.Contains(k) ? $"defined({k})" : $"!defined({k})"));
                        writer.WriteLine($"{(pairIndex++==0 ? "#if" : "#elif")} {condition}");
                    }
                    ShaderSubProgram? vertexSubProgram = null;""",
)
s = s.replace(
    "vertexSubProgram = vertexSubPrograms![0];", "vertexSubProgram = pair.Vertex!;"
).replace("fragmentSubProgram = fragmentSubPrograms![0];", "fragmentSubProgram = pair.Fragment!;")
# Remove original inner CGPROGRAM line; retain new outer one.
s = s.replace(
    'writer.WriteIndent(3);\n\t\t\t\t\twriter.WriteLine("CGPROGRAM");', "writer.WriteIndent(3);"
)
s = s.replace(
    'writer.WriteLine("ENDCG");',
    """}
                    if(preserveVariants) writer.WriteLine("#else\\n#error Original shader has no recovered variant for this keyword combination\\n#endif");
                    writer.WriteLine("ENDCG");""",
)
# Bypass filtering only for explicitly selected variants.
a = s.index("\t\t\t\t// skip instanced shaders")
b = s.index("\n\t\t\t\tif (matchedProgram != null)", a)
s = (
    s[:a]
    + "\t\t\t\tif (!PreserveVariantsFor(shader)) {\n"
    + s[a:b]
    + "\n                }\n"
    + s[b:]
)
p.write_text(s)
