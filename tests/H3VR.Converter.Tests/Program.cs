using H3VR.Converter;
using H3VR.Converter.Tests;

var cases = new List<(string Name, Action Run)>
{
    ("embedded upstream patch retains its reviewed bytes", () => {
        var patch = ShaderExporter.ReadSourcePatch();
        Check(patch.StartsWith("diff --git ", StringComparison.Ordinal), "source patch was not decoded");
    }),
    ("publication rejects recovered assets and old probe trees", () => {
        foreach (var name in new[] { "recovered/Game.cs", "src/Game.dll", "editor/Game.shader", "src/bin/Generated.cs", "probe/Assets/Editor/Test.cs" })
            Check(!Publication.Allowed(name), name);
        Check(Publication.Allowed("src/H3VR.Converter/Program.cs"), "authored source rejected");
    }),
    ("path containment rejects nested output", () => {
        var root = Path.Combine(Path.GetTempPath(), "h3vr-boundary");
        Throws(() => SafeFiles.RequireSeparate(root, Path.Combine(root, "project")));
        Throws(() => SafeFiles.RequireSeparate(Path.Combine(root, "project"), root));
        Throws(() => SafeFiles.RequireSeparate(Path.GetPathRoot(root)!, root));
        SafeFiles.RequireSeparate(root, root + "-separate");
    }),
    ("integration path rejects traversal", () => {
        Throws(() => SafeFiles.Inside(Path.GetTempPath(), "../bad"));
        Throws(() => SafeFiles.Inside(Path.GetTempPath(), Path.GetFullPath("/absolute")));
    }),
    ("symlink output aliases are detected", () => {
        var root = Path.Combine(Path.GetTempPath(), "h3vr-symlinks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var original = Directory.CreateDirectory(Path.Combine(root, "original"));
            Directory.CreateSymbolicLink(Path.Combine(root, "alias"), original.FullName);
            Throws(() => SafeFiles.RequireSeparate(original.FullName, Path.Combine(root, "alias", "output")));
        } finally { Directory.Delete(root, true); }
    }),
    ("copy preserves the original on later edits", () => {
        var root = Path.Combine(Path.GetTempPath(), "h3vr-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var source = Path.Combine(root, "source"); var dest = Path.Combine(root, "destination");
            File.WriteAllText(source, "original"); SafeFiles.Copy(source, dest); File.WriteAllText(dest, "modified");
            Check(File.ReadAllText(source) == "original", "copy modified original");
            Throws(() => SafeFiles.Copy(source, dest));
        } finally { Directory.Delete(root, true); }
    }),
    ("target Unity version is the experiment version", () => Check(ConversionConfig.TargetVersion == "2021.3.45f2", "wrong target"))
};
cases.AddRange(ProjectMigrationTests.Cases());
cases.AddRange(BundlePlanTests.Cases());
cases.AddRange(PipelineTests.Cases());
cases.AddRange(InstallationDiscoveryTests.Cases());
cases.AddRange(AssetRipperIntegrationTests.Cases());
cases.AddRange(ShaderToolCacheTests.Cases());
cases.AddRange(ShaderRecoveryTests.Cases());
cases.AddRange(CliTests.Cases());
cases.AddRange(ApplicationTests.Cases());
cases.AddRange(PublicationTests.Cases());
var failures = 0;
foreach (var (name, run) in cases)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
Console.WriteLine($"{cases.Count} checks, {failures} failures");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("Expected rejection"); }
