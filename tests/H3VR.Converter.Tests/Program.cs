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
cases.Add(("player receipt records manifest build and managed hashes without account data", () => {
    var root = Path.Combine(Path.GetTempPath(), "h3vr-receipt-" + Guid.NewGuid().ToString("N"));
    try {
        var apps = Path.Combine(root, "steamapps"); var game = Path.Combine(apps, "common", "H3VR");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(apps, "appmanifest_450540.acf"), "\"AppState\"\n{\n\"appid\" \"450540\"\n\"installdir\" \"H3VR\"\n\"buildid\" \"25002541\"\n\"LastOwner\" \"private\"\n}");
        var app = Path.Combine(root, "Player.app", "Contents");
        Directory.CreateDirectory(Path.Combine(app, "MacOS"));
        Directory.CreateDirectory(Path.Combine(app, "Resources", "Data", "Managed"));
        File.WriteAllText(Path.Combine(app, "Info.plist"), "<plist><dict><key>CFBundleExecutable</key><string>Player</string></dict></plist>");
        File.WriteAllText(Path.Combine(app, "MacOS", "Player"), "executable");
        var dll = Path.Combine(app, "Resources", "Data", "Managed", "Assembly-CSharp.dll"); File.WriteAllText(dll, "synthetic");
        var receipt = File.ReadAllText(PlayerReceipt.Write(Path.GetDirectoryName(app)!, game));
        Check(receipt.Contains("25002541") && receipt.Contains(SafeFiles.Hash(dll)) && !receipt.Contains("private"), "receipt lacks identity/hash or leaks account data");
        File.WriteAllText(Path.Combine(apps, "appmanifest_450540.acf"), "\"appid\" \"999\"\n\"installdir\" \"H3VR\"\n\"buildid\" \"1\"");
        Throws(() => PlayerReceipt.SteamBuildId(game));
    } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}));
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
