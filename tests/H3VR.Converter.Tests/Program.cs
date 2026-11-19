using H3VR.Converter;
using H3VR.Converter.Tests;

var cases = new List<(string Name, Action Run)>
{
    ("shader exporter pins the reviewed fork and upstream", () => {
        Check(ShaderExporter.ForkCommit.Length == 40 && ShaderExporter.UpstreamCommit.Length == 40 && ShaderExporter.ForkCommit != ShaderExporter.UpstreamCommit, "source revisions are not pinned");
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
cases.Add(("player receipt records manifest build and managed hashes without account data", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "h3vr-receipt-" + Guid.NewGuid().ToString("N"));
    try
    {
        var apps = Path.Combine(root, "steamapps"); var game = Path.Combine(apps, "common", "H3VR");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(apps, "appmanifest_450540.acf"), "\"AppState\"\n{\n\"appid\" \"450540\"\n\"installdir\" \"H3VR\"\n\"buildid\" \"25002541\"\n\"LastOwner\" \"private\"\n}");
        Throws(() => PlayerReceipt.ValidateSupportedInstallation(game));
        foreach (var item in new[] { "h3vr.exe", "steam_api64.dll", "h3vr_Data/globalgamemanagers", "h3vr_Data/Managed/Assembly-CSharp.dll", "h3vr_Data/Managed/Assembly-CSharp-firstpass.dll" })
        {
            var path = Path.Combine(game, item); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "synthetic");
        }
        Directory.CreateDirectory(Path.Combine(game, "h3vr_Data/StreamingAssets"));
        PlayerReceipt.ValidateSupportedInstallation(game);
        var app = Path.Combine(root, "Player.app", "Contents");
        Directory.CreateDirectory(Path.Combine(app, "MacOS"));
        Directory.CreateDirectory(Path.Combine(app, "Resources", "Data", "Managed"));
        File.WriteAllText(Path.Combine(app, "Info.plist"), "<plist><dict><key>CFBundleExecutable</key><string>Player</string></dict></plist>");
        File.WriteAllText(Path.Combine(app, "MacOS", "Player"), "executable");
        var dll = Path.Combine(app, "Resources", "Data", "Managed", "NativeH3VR.Game.dll"); File.WriteAllText(dll, "synthetic");
        var extract = Path.Combine(root, "extract.json");
        SafeFiles.AtomicJson(extract, new StepReceipt("extract", new string('a', 64), DateTimeOffset.UtcNow, new()));
        var receipt = File.ReadAllText(PlayerReceipt.Write(Path.GetDirectoryName(app)!, game, extract));
        Check(receipt.Contains("25002541") && receipt.Contains(SafeFiles.Hash(dll)) && receipt.Contains(new string('a', 64)) && !receipt.Contains("private"), "receipt lacks identity/hash or leaks account data");
        using var document = System.Text.Json.JsonDocument.Parse(receipt);
        Check(document.RootElement.GetProperty("modHostSchemaVersion").GetInt32() == 2,
            "the stock build receipt must identify the external mod manifest version");
        File.WriteAllText(Path.Combine(apps, "appmanifest_450540.acf"), "\"appid\" \"999\"\n\"installdir\" \"H3VR\"\n\"buildid\" \"1\"");
        Throws(() => PlayerReceipt.SteamBuildId(game));
        File.WriteAllText(Path.Combine(apps, "appmanifest_450540.acf"), "\"appid\" \"450540\"\n\"installdir\" \"H3VR\"\n\"buildid\" \"1\"");
        Throws(() => PlayerReceipt.ValidateSupportedInstallation(game));
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
));
cases.Add(("shader export rejects upstream exceptions even when HTTP succeeds", () =>
{
    Check(ShaderRecovery.ExportLogHasException("[stdout] System.IO.FileNotFoundException: missing\n"), "missed exporter exception");
    Check(ShaderRecovery.ExportLogHasException("[stderr] System.TypeInitializationException: failed\n"), "missed initialization exception");
    Check(!ShaderRecovery.ExportLogHasException("[stdout] Export : complete\n"), "rejected clean log");
}
));
cases.Add(("macOS source staging uses the mod host instead of stock XR feature scripts", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "h3vr-staging-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        SourceStaging.Install(Repository.Find(), root, "StandaloneOSX");
        Check(File.Exists(Path.Combine(root, "Assets/H3VRConverter/NativeModHost.cs")), "mod host missing");
        Check(!Directory.Exists(Path.Combine(root, "Assets/H3VRConverter/OpenXR")), "stock XR features staged on macOS");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
));
cases.Add(("macOS project and player stages reject external recovery", () =>
{
    var config = new ConversionConfig { BuildTarget = "StandaloneOSX", RecoveredProject = "/comparison" };
    config.RequireColdRecovery("extract"); config.RequireColdRecovery("managed");
    foreach (var step in new[] { "project", "validate", "build" }) Throws(() => config.RequireColdRecovery(step));
    (config with { RecoveredProject = null }).RequireColdRecovery("build");
}
));
cases.AddRange(ProjectMigrationTests.Cases());
cases.AddRange(BundlePlanTests.Cases());
cases.AddRange(PipelineTests.Cases());
cases.AddRange(InstallationDiscoveryTests.Cases());
cases.AddRange(AssetRipperIntegrationTests.Cases());
cases.AddRange(ShaderToolCacheTests.Cases());
cases.AddRange(ShaderRecoveryTests.Cases());
cases.AddRange(ShaderKeywordTests.Cases());
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
