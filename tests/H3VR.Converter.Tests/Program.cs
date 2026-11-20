using H3VR.Converter;
using H3VR.Converter.Tests;

var cases = new List<(string Name, Action Run)>
{
    ("shader exporter pins the reviewed fork and upstream", () => {
        Check(ShaderExporter.ForkCommit.Length == 40 && ShaderExporter.UpstreamCommit.Length == 40 && ShaderExporter.ForkCommit != ShaderExporter.UpstreamCommit, "source revisions are not pinned");
    }),
    ("stock particle fallbacks point at the renamed Unity built-in", () => {
        const string legacy = "Fallback \"Particles/Alpha Blended\"";
        const string current = "Fallback \"Legacy Shaders/Particles/Alpha Blended\"";
        foreach (var name in new[] { "WFX/Transparent Diffuse", "WFX/Transparent Specular" })
        {
            var source = $"Shader \"{name}\" {{\r\n// {legacy}\r\n\t{legacy} // stock fallback\r\n}}";
            var updated = ProjectMigration.UpdateLegacyParticleFallback(source);
            Check(updated == source.Replace("\t" + legacy, "\t" + current), "changed more than the fallback directive");
            Check(ProjectMigration.UpdateLegacyParticleFallback(updated) == updated, "fallback migration is not idempotent");
        }
        var other = $"Shader \"Custom/Particles\" {{\n{legacy}\n}}";
        Check(ProjectMigration.UpdateLegacyParticleFallback(other) == other, "custom fallback changed");
    }),
    ("publication rejects recovered assets and old probe trees", () => {
        foreach (var name in new[] { "recovered/Game.cs", "src/Game.dll", "editor/Game.shader", "src/bin/Generated.cs", "probe/Assets/Editor/Test.cs" })
            Check(!Publication.Allowed(name), name);
        Check(Publication.Allowed("src/H3VR.Converter/Program.cs"), "authored source rejected");
        Check(Publication.Allowed("tests/StockMenuRendering/measure.py") && Publication.Allowed("tests/StockMenuRendering/run.py")
            && !Publication.Allowed("tests/StockMenuRendering/imported.py"), "stock rendering test source allowlist");
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
    ("converter defaults to the pinned Unity 6.7 beta", () => Check(new ConversionConfig().UnityVersion == "6000.7.0b10", "wrong target"))
};
cases.Add(("Unity 6.7 archives empty unreferenced legacy shader resources", () =>
{
    var root = Directory.CreateTempSubdirectory("h3vr-cg-program-");
    try
    {
        var resources = Directory.CreateDirectory(Path.Combine(root.FullName, "Assets", "Resources")).FullName;
        var source = Path.Combine(resources, "SyntheticInclude.asset");
        var archive = Path.Combine(root.FullName, "RecoveredSources");
        const string guid = "4ab7834af39047519fd553402a592318";
        const string emptyProgram = """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!109 &10900000
            CGProgram:
              m_ObjectHideFlags: 0
              m_PrefabParentObject: {fileID: 0}
              m_PrefabInternal: {fileID: 0}
              m_Name: SyntheticInclude
              m_Script:
              m_PathName:
            """;
        File.WriteAllText(source + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n  assetBundleName:\n");
        File.WriteAllText(source, emptyProgram.Replace("m_Script:", "m_Script: payload", StringComparison.Ordinal));
        Throws(() => LegacyCgProgramResources.Archive(root.FullName, archive));
        File.WriteAllText(source, emptyProgram);
        var referringAsset = Path.Combine(resources, "UsesInclude.mat");
        File.WriteAllText(referringAsset, "m_Include: {fileID: 10900000, guid: " + guid + ", type: 2}\n");
        Throws(() => LegacyCgProgramResources.Archive(root.FullName, archive));
        Check(File.Exists(source) && File.Exists(source + ".meta"), "rejected resource was moved");
        File.Delete(referringAsset);
        Check(LegacyCgProgramResources.Archive(root.FullName, archive) == 1, "empty include was not archived");
        var moved = Path.Combine(archive, "Resources", Path.GetFileName(source));
        Check(File.ReadAllText(moved) == emptyProgram && File.Exists(moved + ".meta"), "archive lost original input");
    }
    finally { root.Delete(recursive: true); }
}
));
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
        const string unityVersion = "6000.7.0b9";
        var receipt = File.ReadAllText(PlayerReceipt.Write(Path.GetDirectoryName(app)!, game, extract, unityVersion));
        Check(receipt.Contains("25002541") && receipt.Contains(SafeFiles.Hash(dll)) && receipt.Contains(new string('a', 64)) && !receipt.Contains("private"), "receipt lacks identity/hash or leaks account data");
        using var document = System.Text.Json.JsonDocument.Parse(receipt);
        Check(document.RootElement.GetProperty("unityVersion").GetString() == unityVersion,
            "the stock build receipt must identify the selected Unity version");
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
        using var assembly = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, SourceStaging.EditorAssets, "H3VRConverter.Editor.asmdef")));
        Check(assembly.RootElement.GetProperty("references").GetArrayLength() == 0 &&
            assembly.RootElement.GetProperty("versionDefines").GetArrayLength() == 0, "stock XR editor assemblies staged on macOS");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
));
cases.Add(("optional graphics source staging and workspace mode leave stock input isolated", () =>
{
    using var fixture = new GraphicsStagingFixture();
    var builtIn = ConverterApplication.WithDefaults(new ConversionConfig(), [], [], fixture.Root);
    var upgraded = ConverterApplication.WithDefaults(new ConversionConfig { GraphicsUpgrade = true }, [], [], fixture.Root);
    Check(builtIn.Workspace != upgraded.Workspace, "the two graphics modes shared a default workspace");
    Directory.CreateDirectory(builtIn.Project);
    SourceStaging.Install(fixture.Repository, builtIn.Project, "StandaloneOSX");
    Check(!Directory.Exists(Path.Combine(builtIn.Project, GraphicsUpgradeProject.RuntimeAssets)), "URP sources installed in built-in project");
    Check(!File.Exists(Path.Combine(builtIn.Project, SourceStaging.EditorAssets, "GraphicsUpgradeConfiguration.cs")),
        "URP editor tooling installed in built-in project");
    Throws(() => GraphicsUpgradeProject.RequireWorkspaceMode(upgraded with { Workspace = builtIn.Workspace }));

    Directory.CreateDirectory(upgraded.Project);
    SourceStaging.Install(fixture.Repository, upgraded.Project, "StandaloneOSX", graphicsUpgrade: true);
    var staged = Path.Combine(upgraded.Project, GraphicsUpgradeProject.RuntimeAssets);
    Check(File.Exists(Path.Combine(staged, "Shaders/GameClarity.shader")), "URP shader was not staged");
    Check(File.Exists(Path.Combine(staged, "NativeH3VR.GraphicsUpgrade.asmdef")), "URP assembly was not staged");
    var marker = Path.Combine(upgraded.Project, GraphicsUpgradeProject.Marker);
    Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
    File.WriteAllText(marker, "{}");
    GraphicsUpgradeProject.RequireWorkspaceMode(upgraded);
    Throws(() => GraphicsUpgradeProject.RequireWorkspaceMode(upgraded with { GraphicsUpgrade = false }));
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
cases.Add(GraphicsOptionsHookTests.Case());

cases.AddRange(QuickbeltColorTests.Cases());
cases.AddRange(TnhColorTests.Cases());
cases.AddRange(StockMaterialColorTests.Cases());
cases.AddRange(PlayerEnlightenTests.Cases());
cases.AddRange(PlayerReflectionProbesTests.Cases());
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


internal sealed class GraphicsStagingFixture : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("h3vr-graphics-staging-").FullName;
    public string Repository => Path.Combine(Root, "source");

    public GraphicsStagingFixture()
    {
        foreach (var file in new[] { "editor/GraphicsUpgradeConfiguration.cs", "runtime/GraphicsUpgrade/Shaders/GameClarity.shader",
                     "runtime/ModHost/NativeModHost.cs" })
        {
            var path = Path.Combine(Repository, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "synthetic");
        }
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
