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
            var updated = ProjectMigration.UpdateLegacyShaderFallback(source);
            Check(updated == source.Replace("\t" + legacy, "\t" + current), "changed more than the fallback directive");
            Check(ProjectMigration.UpdateLegacyShaderFallback(updated) == updated, "fallback migration is not idempotent");
        }
        var other = $"Shader \"Custom/Particles\" {{\n{legacy}\n}}";
        Check(ProjectMigration.UpdateLegacyShaderFallback(other) == other, "custom fallback changed");
    }),
    ("the additive pass has no unresolved fallback", () => {
        const string directive = "Fallback \"Transparent\"";
        var source = $"Shader \"Hidden/SimpleAdd\" {{\r\n// {directive}\r\n\t{directive} // stock\r\n}}";
        var repaired = ProjectMigration.UpdateLegacyShaderFallback(source);
        Check(repaired == source.Replace("\t" + directive, "\tFallback Off"), "only the active fallback directive should change");
        Check(ProjectMigration.UpdateLegacyShaderFallback(repaired) == repaired, "additive fallback repair must be idempotent");
        var custom = source.Replace("Hidden/SimpleAdd", "Custom/SimpleAdd");
        Check(ProjectMigration.UpdateLegacyShaderFallback(custom) == custom, "custom fallback changed");
    }),
    ("runtime optical effects keep named blit passes and runtime keyword variants", () => {
        const string source = """
            Shader "Hidden/PIPScope_PostProcess" {
                Tags { "LIGHTMODE" = "META" "RenderType" = "Opaque" }
                Name "THERMALPOSTPROCESS"
                #pragma shader_feature __ SCOPE_PP_USE_OVERLAY
                #pragma shader_feature __ UNITY_SINGLE_PASS_STEREO
                #pragma shader_feature __ CUSTOM_OPTION
                Name "IRFILTERBLUR"
                tmp2 = tmp1;
                tmp0.z = 0.0;
                while (true) {}
                Name "EXTERNALNVGCROP"
                tmp2 = tmp1;
                tmp0.z = 0.0;
                while (true) {}
            }
            """;
        var expected = source
            .Replace("\"LIGHTMODE\" = \"META\" ", "", StringComparison.Ordinal)
            .Replace("shader_feature __ SCOPE_PP_USE_OVERLAY", "multi_compile __ SCOPE_PP_USE_OVERLAY", StringComparison.Ordinal)
            .Replace("shader_feature __ UNITY_SINGLE_PASS_STEREO", "multi_compile __ UNITY_SINGLE_PASS_STEREO", StringComparison.Ordinal)
            .Replace("Name \"IRFILTERBLUR\"\n    tmp2 = tmp1;\n    tmp0.z = 0.0;",
                "Name \"IRFILTERBLUR\"\n    tmp2 = tmp1;\n    tmp0.z = 1.0;", StringComparison.Ordinal);
        var updated = ProjectMigration.KeepOpticPostProcessPasses(source);
        Check(updated == expected, "changed original blit identity or unrelated source");
        Check(ProjectMigration.KeepOpticPostProcessPasses(updated) == updated, "optic pass migration is not idempotent");
        var other = source.Replace("Hidden/PIPScope_PostProcess", "Custom/Baked", StringComparison.Ordinal);
        Check(ProjectMigration.KeepOpticPostProcessPasses(other) == other, "changed unrelated Meta logic");
    }),
    ("staged overlays keep engine-selected baked lightmap keyword groups", () => {
        const string source = "#pragma shader_feature __ _DECAL_ON\r\n" +
            "  #pragma shader_feature __ LIGHTMAP_ON // renderer selects the bake mode\r\n" +
            "#pragma shader_feature __ DIRLIGHTMAP_COMBINED\n" +
            "#pragma shader_feature __ DYNAMICLIGHTMAP_ON\n" +
            "#pragma shader_feature __ LIGHTMAP_SHADOW_MIXING SHADOWS_SHADOWMASK\n" +
            "#pragma shader_feature __ CUSTOM_LIGHTMAP_ON\n" +
            "// #pragma shader_feature __ LIGHTMAP_ON\n";
        const string expected = "#pragma shader_feature __ _DECAL_ON\r\n" +
            "  #pragma multi_compile __ LIGHTMAP_ON // renderer selects the bake mode\r\n" +
            "#pragma multi_compile __ DIRLIGHTMAP_COMBINED\n" +
            "#pragma multi_compile __ DYNAMICLIGHTMAP_ON\n" +
            "#pragma multi_compile __ LIGHTMAP_SHADOW_MIXING SHADOWS_SHADOWMASK\n" +
            "#pragma shader_feature __ CUSTOM_LIGHTMAP_ON\n" +
            "// #pragma shader_feature __ LIGHTMAP_ON\n";
        var adapted = ProjectMigration.KeepEngineLightmapVariants(source);
        Check(adapted == expected, "engine bake lanes were stripped or unrelated declarations changed");
        Check(ProjectMigration.KeepEngineLightmapVariants(adapted) == adapted, "lightmap variant repair is not idempotent");
        const string settings = "GraphicsSettings:\r\n  m_LightmapStripping: 0\r\n  m_LightmapKeepPlain: 0\r\n" +
            "  m_LightmapKeepDirCombined: 1\r\n  m_LightmapKeepShadowMask: 0\r\n  m_FogStripping: 0\r\n";
        const string retained = "GraphicsSettings:\r\n  m_LightmapStripping: 1\r\n  m_LightmapKeepPlain: 1\r\n" +
            "  m_LightmapKeepDirCombined: 1\r\n  m_LightmapKeepShadowMask: 1\r\n  m_FogStripping: 0\r\n";
        Check(ProjectMigration.KeepBakedLightmapSettings(settings) == retained, "Unity Auto policy would remove rebuilt lighting");
        Check(ProjectMigration.KeepBakedLightmapSettings(retained) == retained, "lightmap settings repair is not idempotent");
    }),
    ("recovered night vision passes preserve Direct3D comparison and swizzle semantics", () => {
        const string recoveredGrain = "tmp2.x = floor(-tmp2.x);\r\n\ttmp2.x = 1.0 - tmp2.x;\r\n";
        const string originalGrain = "tmp2.x = tmp2.x ? 0.0 : 1.0;\r\n";
        const string recoveredStreak = "tmp0.x = tmp0.y - tmp0.x;\nfloat3(_NightVisionHalo.xx, _NightVisionEdgeDarken.x)\n";
        const string originalStreak = "tmp0.x = tmp0.x - tmp0.y;\nfloat3(_NightVisionHalo, _NightVisionEdgeDarken, _NightVisionHalo)\n";
        const string grain = "Name \"EXTERNALNVG\"\n";
        const string streak = "Name \"NVGSTREAKING\"\n";
        const string unrelated = "Name \"EXTERNALNVGCROP\"\n" + recoveredGrain + recoveredStreak;
        const string header = "Shader \"Hidden/PIPScope_PostProcess\" {\n";
        var source = header + grain + string.Concat(Enumerable.Repeat(recoveredGrain, 4))
            + streak + recoveredStreak + recoveredStreak + unrelated + "}";
        var expected = header + grain + string.Concat(Enumerable.Repeat(originalGrain, 4))
            + streak + originalStreak + originalStreak + unrelated + "}";
        var updated = ProjectMigration.KeepOpticPostProcessPasses(source);
        Check(updated == expected, "night vision variants or unrelated pass changed incorrectly");
        Check(ProjectMigration.KeepOpticPostProcessPasses(updated) == updated, "night vision migration is not idempotent");
        var custom = source.Replace("Hidden/PIPScope_PostProcess", "Custom/PIPScope_PostProcess", StringComparison.Ordinal);
        Check(ProjectMigration.KeepOpticPostProcessPasses(custom) == custom, "changed an unrelated shader");
    }),
    ("recovered two-sided fragments use the original boolean face input", () => {
        const string signature = "fout frag(v2f inp, float facing: VFACE)";
        const string corrected = "fout frag(v2f inp, bool facing: SV_IsFrontFace)";
        foreach (string use in new[] { "tmp0.x = facing.x ? 1.0 : -1.0;",
                                        "tmp2.w = uint1(tmp2.w) | uint1(facing.x);" })
        {
            string expected = corrected + " { " + use.Replace("uint1(facing.x)", "(facing.x ? 0xffffffffu : 0u)") + " }";
            Check(ProjectMigration.RestoreDxbcFrontFace(signature + " { " + use + " }") == expected, use);
            Check(ProjectMigration.RestoreDxbcFrontFace(expected) == expected, "face migration is not idempotent");
        }
        string signed = signature + " { float side = sign(facing); }";
        Check(ProjectMigration.RestoreDxbcFrontFace(signed) == signed, "changed a signed face calculation");
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
cases.AddRange(StockShaderWarmupTests.Cases());
cases.AddRange(LegacyReverbTests.Cases());
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
cases.AddRange(LegacyNormalDecodingTests.Cases());
cases.AddRange(LegacyPackedTextureDataTests.Cases());
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
