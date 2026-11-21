using H3VR.Converter;
using Mono.Cecil;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace H3VR.Converter.Tests;

public static class ProjectMigrationTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("an opt-in project pins the installed Unity 6.7 URP release", PinGraphicsPackage);
        yield return ("duplicate scene GUID repair preserves baked occlusion and is deterministic", DuplicateSceneGuids);
        yield return ("legacy scene migration preserves baked data and validates ownership", LegacyScenes);
        yield return ("MD4 vectors and Unity file ID", Md4Vectors);
        yield return ("project migration rebinds scripts and keeps recovery immutable", MigrateFixture);
        yield return ("macOS project uses the native bridge without Unity XR packages", MetalProject);
        yield return ("legacy UI assembly references bind EventSystems components", RebindEventSystem);
        yield return ("OpenXR input staging copies the manifest and every referenced binding", SteamVrActions);
        yield return ("missing referenced controller bindings fail closed", MissingSteamVrBinding);
        yield return ("shader overlay rejects dummy exports", RejectDummyShader);
        yield return ("graphics overlay verifies each recovered shader and its generated includes", VerifyGraphicsShaders);
        yield return ("stock RGB is preserved for exclusive opaque data-alpha uses", PreserveDataAlphaRgb);
        foreach (var target in new[] { "StandaloneOSX", "StandaloneWindows64", "StandaloneLinux64" })
            yield return ($"{target} converts stock HDR colors and preserves authored materials", () => MigrateHdrMaterials(target));
        foreach (var stockHdr in LegacyDynamicHdrMaterialsTests.Cases()) yield return stockHdr;
        yield return ("compute overlays preserve local asset identity and rebind Unity importer references", ReplaceComputeShader);
        yield return ("integration applies a hash-pinned OpenXR provider", ApplyIntegration);
        yield return ("integration rejects traversal and hash mismatch", RejectUnsafeIntegration);
        yield return ("macOS player puts Steam and FMOD libraries directly in PlugIns", PlaceMacPlayerLibraries);
    }

    private static void PreserveDataAlphaRgb()
    {
        using var f = new Fixture();
        const string alloy = "76d7d27bf46448744b5deb4484aaa8ec";
        const string pine = "85543bfddcf8c824c915d07a065342d1";
        const string prototyping = "1e94baccfd73a4142bb746e61dcf1c7a";
        const string ao = "66666666666666666666666666666666";
        const string detail = "77777777777777777777777777777777";
        const string packed = "11111111111111111111111111111111";
        const string shared = "22222222222222222222222222222222";
        const string direct = "33333333333333333333333333333333";
        const string cutout = "44444444444444444444444444444444";
        const string unknown = "55555555555555555555555555555555";

        string Importer(string guid) => $"""
            fileFormatVersion: 2
            guid: {guid}
            TextureImporter:
              alphaUsage: 1
              alphaIsTransparency: 1
              textureType: 0
            """.ReplaceLineEndings("\r\n");
        string Material(string shader, string keywords, int mode, string slot, string texture) => $$"""
            Material:
              m_Shader: {fileID: 4800000, guid: {{shader}}, type: 3}
              m_ShaderKeywords: {{keywords}}
              m_CustomRenderQueue: -1
              m_SavedProperties:
                m_TexEnvs:
                  {{slot}}:
                    m_Texture: {fileID: 2800000, guid: {{texture}}, type: 3}
                m_Floats:
                  _Mode: {{mode}}
                  _SrcBlend: 1
                  _DstBlend: 0
                  _Glossiness: 0.5
            """.ReplaceLineEndings("\r\n");
        const string roughness = "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A";
        f.WriteCandidate("Assets/Material/Pine.mat", Material(alloy, roughness, 0, "_MainTex", pine));
        f.WriteCandidate("Assets/Material/Packed.mat", Material(alloy, "", 0, "_SpecTex", packed));
        f.WriteCandidate("Assets/Material/Ambient.mat", Material(prototyping, "", 0, "_AoMap", ao));
        f.WriteCandidate("Assets/Material/Detail.mat", Material(alloy, "_DETAIL_MULX2", 0, "_DetailAlbedoMap", detail));
        f.WriteCandidate("Assets/Material/Shared.mat", Material(alloy, roughness, 0, "_MainTex", shared));
        f.WriteCandidate("Assets/Material/OtherUse.mat", Material(alloy, "_ALPHATEST_ON", 1, "_MainTex", shared));
        f.WriteCandidate("Assets/Material/Cutout.mat", Material(alloy, "_ALPHATEST_ON " + roughness, 0, "_MainTex", cutout));
        f.WriteCandidate("Assets/Material/Unknown.mat", Material(unknown, roughness, 0, "_MainTex", unknown));
        f.WriteCandidate("Assets/Material/Direct.mat", Material(alloy, "", 0, "_SpecTex", direct));
        f.WriteCandidate("Assets/UI.prefab", $"  m_Sprite: {{fileID: 21300000, guid: {direct}, type: 3}}");
        string[] textures = [pine, packed, ao, detail, shared, direct, cutout, unknown];
        foreach (string guid in textures)
            f.WriteCandidate($"Assets/Texture2D/{guid}.png.meta", Importer(guid));

        string assets = Path.Combine(f.Candidate, "Assets");
        var shaders = new Dictionary<string, string> { [alloy] = "Alloy/Core", [prototyping] = "Alloy/Prototyping" };
        Check(LegacyTextureAlpha.Apply(assets, shaders) == 4, "expected the exclusive opaque albedo, packed, detail, and occlusion textures");
        foreach (string guid in textures)
        {
            string expected = Importer(guid);
            if (guid is pine or packed or ao or detail) expected = expected.Replace("alphaIsTransparency: 1", "alphaIsTransparency: 0");
            Check(File.ReadAllText(Path.Combine(assets, "Texture2D", guid + ".png.meta")) == expected,
                "unexpected importer changes for " + guid);
        }
        Check(LegacyTextureAlpha.Apply(assets, shaders) == 0, "data-alpha migration must be idempotent");
    }

    private static void DuplicateSceneGuids()
    {
        using var f = new Fixture();
        const string original = "11111111111111111111111111111111";
        const string aLight = "22222222222222222222222222222222";
        const string bLight = "33333333333333333333333333333333";
        const string a = "Assets/Scenes/A.unity", b = "Assets/Scenes/B.unity";
        static string Scene(string light) => "%YAML 1.1\n--- !u!29 &1\nOcclusionCullingSettings:\n  m_SceneGUID: " + original + "\n" +
            "--- !u!157 &2\nLightmapSettings:\n  m_LightingDataAsset: {fileID: 112000000, guid: " + light + ", type: 2}\n";
        const string light = "%YAML 1.1\n--- !u!1120 &112000000\nLightingDataAsset:\n  m_Name: LightingData\n  m_Lightmaps: []\n  m_EnlightenData: aabb\n";
        const string build = "%YAML 1.1\n--- !u!1045 &1\nEditorBuildSettings:\n  m_Scenes:\n  - enabled: 1\n    path: " + a + "\n    guid: " + original + "\n  - enabled: 1\n    path: " + b + "\n    guid: " + original + "\n";
        f.WriteCandidate(a, Scene(aLight), original); f.WriteCandidate(b, Scene(bLight), original);
        f.WriteCandidate("Assets/Scenes/A/LightingData.asset", light, aLight); f.WriteCandidate("Assets/Scenes/B/LightingData.asset", light, bLight);
        f.WriteCandidate("ProjectSettings/EditorBuildSettings.asset", build);
        static string NewGuid(string path) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("NativeH3VR.SceneAsset:" + path)))[..32];
        string first = NewGuid(a), second = NewGuid(b);
        f.WriteCandidate("Assets/Unknown.asset", "%YAML 1.1\n--- !u!1 &1\nUnknown:\n  target: {fileID: 102900000, guid: " + original + ", type: 3}\n");
        try { LegacySceneMigration.Apply(f.Candidate); throw new Exception("Unknown duplicate reference accepted"); } catch (InvalidDataException) { }
        Check(File.ReadAllText(Path.Combine(f.Candidate, a)) == Scene(aLight) && File.ReadAllText(Path.Combine(f.Candidate, "ProjectSettings/EditorBuildSettings.asset")) == build, "failed preflight must not write files");
        File.Delete(Path.Combine(f.Candidate, "Assets/Unknown.asset"));
        f.WriteCandidate("Assets/Collision.asset", "%YAML 1.1\n", first);
        try { LegacySceneMigration.Apply(f.Candidate); throw new Exception("Generated GUID collision accepted"); } catch (InvalidDataException) { }
        File.Delete(Path.Combine(f.Candidate, "Assets/Collision.asset")); File.Delete(Path.Combine(f.Candidate, "Assets/Collision.asset.meta"));
        f.WriteCandidate("ProjectSettings/EditorBuildSettings.asset", build.Replace("    path: " + b, "    path: Assets/Scenes/Missing.unity"));
        try { LegacySceneMigration.Apply(f.Candidate); throw new Exception("Missing build path accepted"); } catch (InvalidDataException) { }
        Check(File.ReadAllText(Path.Combine(f.Candidate, a + ".meta")).Contains("guid: " + original), "missing build path leaves original metadata");
        f.WriteCandidate("ProjectSettings/EditorBuildSettings.asset", build);
        LegacySceneMigration.Apply(f.Candidate);
        Check(File.ReadAllText(Path.Combine(f.Candidate, a + ".meta")).Contains("guid: " + first) && File.ReadAllText(Path.Combine(f.Candidate, b + ".meta")).Contains("guid: " + second), "both scene identities repaired deterministically");
        Check(File.ReadAllText(Path.Combine(f.Candidate, a)) == Scene(aLight) && File.ReadAllText(Path.Combine(f.Candidate, b)) == Scene(bLight), "internal occlusion GUIDs remain unchanged");
        string settings = File.ReadAllText(Path.Combine(f.Candidate, "ProjectSettings/EditorBuildSettings.asset"));
        Check(settings.Contains("path: " + a + "\n    guid: " + first) && settings.Contains("path: " + b + "\n    guid: " + second), "build scene path identities repaired");
        Check(File.ReadAllText(Path.Combine(f.Candidate, "Assets/Scenes/A/LightingData.asset")).Contains("guid: " + first) && File.ReadAllText(Path.Combine(f.Candidate, "Assets/Scenes/B/LightingData.asset")).Contains("guid: " + second), "lighting pointers use corrected identities");
        LegacySceneMigration.Apply(f.Candidate);
        Check(File.ReadAllText(Path.Combine(f.Candidate, "ProjectSettings/EditorBuildSettings.asset")) == settings, "GUID repair is idempotent");
    }

    private static void LegacyScenes()
    {
        using var f = new Fixture();
        const string sceneGuid = "11111111111111111111111111111111";
        const string lightingGuid = "22222222222222222222222222222222";
        const string scenePath = "Assets/Scenes/Test.unity";
        const string lightPath = "Assets/Scenes/Test/LightingData.asset";
        const string scene = "%YAML 1.1\n--- !u!157 &1\nLightmapSettings:\n  m_LightingDataAsset: {fileID: 112000000, guid: " + lightingGuid + ", type: 2}\n" +
            "--- !u!1 &7\nGameObject:\n  m_Component:\n  - component: {fileID: 8}\n  - component: {fileID: 9}\n  m_Name: camera\n" +
            "--- !u!92 &8\nGUILayer:\n  m_GameObject: {fileID: 7}\n  m_Enabled: 1\n--- !u!20 &9\nCamera:\n  m_GameObject: {fileID: 7}\n";
        const string light = "%YAML 1.1\n--- !u!1120 &112000000\nLightingDataAsset:\n  m_Name: LightingData\n  m_Lightmaps:\n  - m_Lightmap: {fileID: 28, guid: abcdef, type: 3}\n  m_BakedAmbientProbeInLinear: 0.1234\n  m_EnlightenData: aabbccdd\n";
        f.WriteCandidate(scenePath, scene, sceneGuid); f.WriteCandidate(lightPath, light, lightingGuid);
        LegacySceneMigration.Apply(f.Candidate);
        string actualScene = File.ReadAllText(Path.Combine(f.Candidate, scenePath));
        string actualLight = File.ReadAllText(Path.Combine(f.Candidate, lightPath));
        Check(actualScene == scene.Replace("  - component: {fileID: 8}\n", "").Replace("--- !u!92 &8\nGUILayer:\n  m_GameObject: {fileID: 7}\n  m_Enabled: 1\n", ""), "only GUI layer and owner entry removed");
        string field = "  m_Scene: {fileID: 102900000, guid: " + sceneGuid + ", type: 3}\n";
        Check(actualLight.Replace(field, "") == light, "baked payload must remain byte-exact");
        LegacySceneMigration.Apply(f.Candidate);
        Check(File.ReadAllText(Path.Combine(f.Candidate, lightPath)) == actualLight, "migration is idempotent");

        // A malformed second owner is discovered before *any* planned changes.
        f.WriteCandidate(scenePath, scene, sceneGuid); f.WriteCandidate(lightPath, light, lightingGuid);
        f.WriteCandidate("Assets/Scenes/Other.unity", scene, "33333333333333333333333333333333");
        try { LegacySceneMigration.Apply(f.Candidate); throw new Exception("Shared lighting was accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(Path.Combine(f.Candidate, scenePath)) == scene && File.ReadAllText(Path.Combine(f.Candidate, lightPath)) == light, "failed preflight changes no source files");
        File.Delete(Path.Combine(f.Candidate, "Assets/Scenes/Other.unity"));
        f.WriteCandidate(scenePath, scene.Replace("m_GameObject: {fileID: 7}", "m_GameObject: {fileID: 66}"), sceneGuid);
        try { LegacySceneMigration.Apply(f.Candidate); throw new Exception("Missing component owner was accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(Path.Combine(f.Candidate, lightPath)) == light, "invalid owner leaves lighting untouched");
        f.WriteCandidate(scenePath, scene.Replace("  - component: {fileID: 8}\n", ""), sceneGuid);
        try { LegacySceneMigration.Apply(f.Candidate); throw new Exception("Missing component entry was accepted"); }
        catch (InvalidDataException) { }
        f.WriteCandidate(scenePath, scene, sceneGuid);
        f.WriteCandidate(lightPath, light.Replace("  m_Lightmaps:", "  m_Scene: {fileID: 102900000, guid: 44444444444444444444444444444444, type: 3}\n  m_Lightmaps:"), lightingGuid);
        try { LegacySceneMigration.Apply(f.Candidate); throw new Exception("Wrong scene owner was accepted"); }
        catch (InvalidDataException) { }
    }

    private static void MetalProject()
    {
        using var fixture = new Fixture();
        fixture.CreateManaged("NativeH3VR.Game", "Assembly-CSharp", "H3VR", "Fixture");
        fixture.WriteSteamActions("{\"default_bindings\":[{\"binding_url\":\"fixture.json\"}]}", ("fixture.json", "{}"));
        const string manifest = "Packages/manifest.json";
        const string xrSettings = "ProjectSettings/XRPackageSettings.json";
        fixture.WriteCandidate(manifest, """
            {"dependencies":{"com.unity.xr.openxr":"1.13.0","com.unity.xr.management":"4.4.0",
              "com.unity.xr.core-utils":"2.1.1","com.unity.xr.legacyinputhelpers":"2.1.7",
              "com.unity.modules.xr":"1.0.0","com.unity.modules.vr":"1.0.0"}}
            """);
        fixture.WriteCandidate(xrSettings, "{}");
        var config = fixture.Config() with { BuildTarget = "StandaloneOSX" };
        var report = new ProjectMigration(config, fixture.Candidate).Run();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Candidate, manifest)));
        var packages = document.RootElement.GetProperty("dependencies");
        Check(packages.EnumerateObject().All(item => !item.Name.StartsWith("com.unity.xr.", StringComparison.Ordinal)),
            "macOS retained Unity XR provider packages");
        Check(packages.GetProperty("com.unity.inputsystem").GetString() == "6.7.0" &&
            packages.GetProperty("com.unity.modules.xr").GetString() == "1.0.0", "macOS lost a required engine dependency");
        Check(!packages.TryGetProperty("com.unity.modules.vr", out _), "macOS retained the removed standalone VR module");
        Check(report.OpenXrPackage is null && !File.Exists(Path.Combine(fixture.Candidate, xrSettings)),
            "macOS retained Unity XR provider settings");
    }

    private static void Md4Vectors()
    {
        Check(Convert.ToHexStringLower(Md4.Hash([])) == "31d6cfe0d16ae931b73c59d7e0c089c0", "empty MD4 vector");
        Check(Convert.ToHexStringLower(Md4.Hash("abc"u8)) == "a448017aaf21d8525fc10ae87aa6729d", "abc MD4 vector");
        Check(Md4.UnityFileId("H3VR", "Fixture") == BitConverter.ToInt32(Md4.Hash(Encoding.UTF8.GetBytes("s\0\0\0H3VRFixture")), 0), "Unity fileID is signed little-endian MD4 prefix");
    }

    private static void PinGraphicsPackage()
    {
        using var fixture = new Fixture();
        fixture.CreateManaged("NativeH3VR.Game", "Assembly-CSharp", "H3VR", "Fixture");
        fixture.CreateManaged("NativeH3VR.UnityScript", "Assembly-UnityScript", "", "Rotate");
        fixture.WriteSteamActions("{\"default_bindings\":[{\"binding_url\":\"bindings_knuckles.json\"}]}",
            ("bindings_knuckles.json", "{}"));
        var config = fixture.Config() with { GraphicsUpgrade = true };
        fixture.WriteCandidate("Packages/manifest.json", """{"dependencies":{"com.unity.render-pipelines.universal":"17.6.0"}}""");
        var report = new ProjectMigration(config, fixture.Candidate).Run();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Candidate, "Packages/manifest.json")));
        var dependencies = manifest.RootElement.GetProperty("dependencies");
        Check(dependencies.GetProperty(GraphicsUpgradeProject.PackageName).GetString()
            == GraphicsUpgradeProject.PackageVersion, "the opt-in upgrade failed to pin URP");
        Check(dependencies.GetProperty("com.unity.modules.ui").GetString() == "1.0.0" &&
            dependencies.GetProperty("com.unity.modules.uielements").GetString() == "1.0.0",
            "the graphics menu and ShaderGraph editor UI modules were not pinned");
        Check(report.GraphicsPipelinePackage == GraphicsUpgradeProject.PackageName + "@" + GraphicsUpgradeProject.PackageVersion,
            "migration report did not declare the selected URP package");
        var legacy = File.ReadAllText(Path.Combine(fixture.Candidate, "Assets/Plugins/NativeH3VR.UnityScript.dll.meta"));
        Check(legacy.Contains("isExplicitlyReferenced: 1", StringComparison.Ordinal),
            "legacy UnityScript Rotate can shadow ShaderGraph's UIElements.Rotate");
    }

    private static void MigrateFixture()
    {
        using var fixture = new Fixture();
        fixture.CreateManaged("NativeH3VR.Game", "Assembly-CSharp", "H3VR", "Fixture");
        var relative = "Assets/Scripts/Assembly-CSharp/H3VR/Fixture.cs";
        fixture.WriteRecovered(relative, "namespace H3VR { public class Fixture {} }", "11111111111111111111111111111111", 17);
        fixture.WriteCandidate(relative, "namespace H3VR { public class Fixture {} }", "11111111111111111111111111111111", 17);
        fixture.WriteCandidate("Assets/Scenes/Test.prefab", "m_Script: {fileID: 11500000, guid: 11111111111111111111111111111111, type: 3}\n");
        fixture.WriteCandidate("Assets/Scenes/Test.prefab.meta", "fileFormatVersion: 2\nguid: 44444444444444444444444444444444\nNativeFormatImporter:\n  assetBundleName: test_bundle\n  assetBundleVariant:\n");
        fixture.WriteCandidate("Packages/manifest.json", """
            {"dependencies":{"com.unity.ugui":"1.0.0","com.unity.modules.ai":"1.0.0",
              "com.unity.modules.vr":"1.0.0","com.unity.xr.openxr":"1.13.0"}}
            """);
        fixture.WriteSteamActions("{\"default_bindings\":[{\"binding_url\":\"bindings_knuckles.json\"}]}", ("bindings_knuckles.json", "{\"controller_type\":\"knuckles\"}"));
        var sourceManifest = Path.Combine(fixture.Recovered, relative + ".meta"); var sourceHash = SafeFiles.Hash(sourceManifest);
        const string selectedVersion = "6000.7.0b9";
        var config = fixture.Config() with { BuildTarget = "StandaloneWindows64", UnityVersion = selectedVersion };
        var report = new ProjectMigration(config, fixture.Candidate).Run();
        var rewritten = File.ReadAllText(Path.Combine(fixture.Candidate, "Assets/Scenes/Test.prefab"));
        var managedMeta = File.ReadAllText(Path.Combine(fixture.Candidate, "Assets/Plugins/NativeH3VR.Game.dll.meta"));
        Check(rewritten.Contains("guid: " + GuidFrom(managedMeta), StringComparison.Ordinal), "serialized MonoScript points at migrated managed assembly GUID");
        Check(rewritten.Contains("fileID: " + Md4.UnityFileId("H3VR", "Fixture"), StringComparison.Ordinal), "serialized MonoScript uses Unity MD4 type file ID");
        Check(report.RewrittenReferences == 1 && report.ScriptBindings == 1, "report counts actual rebinding");
        Check(managedMeta.Contains("H3VR.Fixture: 17", StringComparison.Ordinal), "original Unity execution order is copied to managed plugin metadata");
        Check(File.Exists(Path.Combine(fixture.Candidate, "NativeH3VR-RecoveredSources/Assembly-CSharp/H3VR/Fixture.cs")), "recovered source retained in candidate archive");
        Check(SafeFiles.Hash(sourceManifest) == sourceHash, "original recovered project metadata unchanged");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Candidate, "Packages/manifest.json")));
        var packages = manifest.RootElement.GetProperty("dependencies");
        Check(!packages.TryGetProperty(GraphicsUpgradeProject.PackageName, out _) && report.GraphicsPipelinePackage == null,
            "the default migration added a URP dependency");
        Check(!packages.TryGetProperty("com.unity.ugui", out _), "duplicate UGUI package retained");
        Check(!packages.TryGetProperty("com.unity.modules.vr", out _), "the removed standalone VR module was retained");
        Check(packages.GetProperty("com.unity.xr.openxr").GetString() == "1.19.0" &&
            packages.GetProperty("com.unity.inputsystem").GetString() == "6.7.0" &&
            packages.GetProperty("com.unity.xr.management").GetString() == "6.0.0" &&
            packages.GetProperty("com.unity.xr.core-utils").GetString() == "6.0.0" &&
            packages.GetProperty("com.unity.xr.legacyinputhelpers").GetString() == "6.0.0" &&
            report.OpenXrPackage == "com.unity.xr.openxr@1.19.0", "OpenXR dependencies are pinned for Unity 6.7");
        Check(File.ReadAllText(Path.Combine(fixture.Candidate, "ProjectSettings/ProjectSettings.asset")).Contains("activeInputHandler: 2", StringComparison.Ordinal), "both legacy and new input backends are enabled before first import");
        Check(File.ReadAllText(Path.Combine(fixture.Candidate, "ProjectSettings/ProjectVersion.txt")) == $"m_EditorVersion: {selectedVersion}\n" &&
            report.UnityVersion == selectedVersion, "project and report do not identify the selected exact Unity release");
        var bundlePlan = File.ReadAllText(Path.Combine(fixture.Candidate, report.AssetBundleMembership));
        Check(bundlePlan.Contains("test_bundle", StringComparison.Ordinal) && bundlePlan.Contains("Assets/Scenes/Test.prefab", StringComparison.Ordinal) && bundlePlan.Contains("\"addresses\": null", StringComparison.Ordinal), "records original bundle membership without fabricating serialized addresses");
    }

    private static void SteamVrActions()
    {
        using var fixture = new Fixture();
        fixture.CreateManaged("NativeH3VR.Game", "Assembly-CSharp", "H3VR", "Fixture");
        fixture.WriteSteamActions("{\"default_bindings\":[{\"binding_url\":\"bindings_knuckles.json\"},{\"binding_url\":\"bindings_touch.json\"}]}",
            ("bindings_knuckles.json", "{\"controller_type\":\"knuckles\"}"), ("bindings_touch.json", "{\"controller_type\":\"oculus_touch\"}"));
        new ProjectMigration(fixture.Config(), fixture.Candidate).Run();
        var target = Path.Combine(fixture.Candidate, "Assets/StreamingAssets/SteamVR");
        Check(File.Exists(Path.Combine(target, "actions.json")), "root action manifest was not staged");
        Check(File.Exists(Path.Combine(target, "bindings_knuckles.json")) && File.Exists(Path.Combine(target, "bindings_touch.json")), "not every referenced controller binding was staged");
        Check(SafeFiles.Hash(Path.Combine(target, "actions.json")) == SafeFiles.Hash(Path.Combine(fixture.Game, "actions.json")), "action manifest bytes changed while staging");
    }

    private static void RebindEventSystem()
    {
        using var fixture = new Fixture();
        fixture.CreateManaged("UnityEngine.UI", "UnityEngine.UI", "UnityEngine.EventSystems", "EventSystem");
        fixture.WriteSteamActions("{\"default_bindings\":[{\"binding_url\":\"fixture.json\"}]}", ("fixture.json", "{}"));
        var config = fixture.Config();
        var package = Path.Combine(config.UnityData, "Resources/PackageManager/BuiltInPackages/com.unity.ugui");
        Directory.CreateDirectory(package);
        var source = Path.Combine(package, "EventSystem.cs");
        File.WriteAllText(source, "namespace UnityEngine.EventSystems { public class EventSystem { } }");
        File.WriteAllText(source + ".meta", "fileFormatVersion: 2\nguid: 77610ee63b9e42949f0ed72377faa0b7\n");
        const string prefab = "Assets/EventSystem.prefab";
        var fileId = Md4.UnityFileId("UnityEngine.EventSystems", "EventSystem");
        fixture.WriteCandidate(prefab, $"  m_Script: {{fileID: {fileId}, guid: f5f67c52d1564df4a8936ccd202a3bd8, type: 3}}\n");
        new ProjectMigration(config, fixture.Candidate).Run();
        var guid = GuidFrom(File.ReadAllText(Path.Combine(fixture.Candidate, "Assets/Plugins/UnityEngine.UI.dll.meta")));
        Check(File.ReadAllText(Path.Combine(fixture.Candidate, prefab)).Contains($"fileID: {fileId}, guid: {guid}, type: 3", StringComparison.Ordinal),
            "the EventSystem script did not bind to the UI assembly with its original file ID");
    }

    private static void MissingSteamVrBinding()
    {
        using var fixture = new Fixture();
        fixture.CreateManaged("NativeH3VR.Game", "Assembly-CSharp", "H3VR", "Fixture");
        fixture.WriteSteamActions("{\"default_bindings\":[{\"binding_url\":\"missing.json\"}]}");
        var error = Capture(() => new ProjectMigration(fixture.Config(), fixture.Candidate).Run());
        Check(error is FileNotFoundException && error.Message.Contains("binding", StringComparison.OrdinalIgnoreCase), "missing root-referenced binding must fail migration");
    }

    private static void RejectDummyShader()
    {
        using var fixture = new Fixture(); fixture.CreateManaged("NativeH3VR.Game", "Assembly-CSharp", "H3VR", "Fixture");
        fixture.WriteCandidate("Assets/Keep.txt", "kept");
        fixture.WriteShaderOverlay("Shader \"Fixture\" { //DummyShaderTextExporter\n }",
            new StepReceipt("extract", "synthetic-extraction", DateTimeOffset.UtcNow, new()));
        var error = Capture(() => new ProjectMigration(fixture.Config(fixture.Shaders), fixture.Candidate).Run());
        Check(error is InvalidDataException && error.Message.Contains("Dummy shader", StringComparison.Ordinal), "rejects known placeholder shader content");
        Check(!File.Exists(Path.Combine(fixture.Candidate, "Assets/Fixture.shader")), "does not copy the rejected shader");
    }

    private static void VerifyGraphicsShaders()
    {
        using var fixture = new Fixture();
        var extract = new StepReceipt("extract", "synthetic-extraction", DateTimeOffset.UtcNow, new());
        fixture.WriteShaderOverlay("Shader \"Fixture\" { }", extract, "float4 fixture;");
        var assets = Path.Combine(fixture.Shaders, "Assets");
        var shader = Path.Combine(assets, "Fixture.shader");
        var include = Path.Combine(assets, "Fixture.cginc");
        var catalog = Path.Combine(fixture.Shaders, SourceStaging.ShaderVariantCatalogName);

        (string Path, string Sha256)? Verify() => GraphicsShaderOverlay.Verify(fixture.Config(), fixture.Shaders, assets, [shader], [include]);
        Check(Verify() is null, "permits overlays written before the variant catalog");
        File.WriteAllText(catalog, "{}");
        Check(Capture(() => Verify()) is InvalidDataException, "rejects an unrecorded variant catalog");
        const string catalogText = "{\"schemaVersion\":1,\"shaders\":[]}";
        File.WriteAllText(catalog, catalogText);
        var receiptPath = Path.Combine(fixture.Shaders, GraphicsShaderOverlay.ReceiptName);
        var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
        receipt["shaderVariantCatalogSha256"] = SafeFiles.Hash(catalog);
        SafeFiles.AtomicJson(receiptPath, receipt);
        Check(Verify() is { } verified && verified.Path == catalog && verified.Sha256 == SafeFiles.HashText(catalogText),
            "verifies the recorded variant catalog");
        File.AppendAllText(catalog, "\n");
        Check(Capture(() => Verify()) is InvalidDataException, "rejects an edited variant catalog");
        File.WriteAllText(catalog, catalogText);
        Verify();
        File.AppendAllText(include, "\nfloat4 changed;");
        Check(Capture(() => Verify()) is InvalidDataException, "rejects an edited shader include");
        File.WriteAllText(include, "float4 fixture;");
        File.AppendAllText(shader, "\n// changed");
        Check(Capture(() => Verify()) is InvalidDataException, "rejects graphics shader content not recorded in the receipt");
    }

    private static void MigrateHdrMaterials(string target)
    {
        using var fixture = new Fixture();
        fixture.CreateManaged("NativeH3VR.Game", "Assembly-CSharp", "H3VR", "Fixture");
        fixture.WriteSteamActions("{\"default_bindings\":[{\"binding_url\":\"fixture.json\"}]}", ("fixture.json", "{}"));
        Directory.CreateDirectory(Path.Combine(fixture.Recovered, "ProjectSettings"));
        File.WriteAllText(Path.Combine(fixture.Recovered, "ProjectSettings/ProjectSettings.asset"), "  m_ActiveColorSpace: 1\n");

        string material = """
            Material:
              m_Shader: {fileID: 4800000, guid: 22222222222222222222222222222222, type: 3}
              m_SavedProperties:
                m_TexEnvs:
                - _RimTex: {m_Texture: {fileID: 29}}
                m_Floats: []
                m_Colors:
                  _Color: {r: 0.5, g: 0.2, b: 0.1, a: 0.9}
                  _Phase: {r: 2, g: 3, b: 4, a: 0.1}
                  _RimColor: {r: 0.5, g: 2, b: 1, a: 0.2}
              unrelated: 7
            """;
        const string source = "Assets/Imported.mat";
        const string authored = "Assets/UserAuthored.mat";
        fixture.WriteRecovered(source, material, new string('3', 32));
        fixture.WriteCandidate(source, material);
        fixture.WriteCandidate(authored, material);
        string original = Path.Combine(fixture.Recovered, source);

        var extract = new StepReceipt("extract", "synthetic-extraction", DateTimeOffset.UtcNow,
            new() { [original] = SafeFiles.Hash(original) });
        fixture.WriteShaderOverlay("""
            Shader "Fixture" {
                Properties {
                    _Color ("Color", Color) = (1,1,1,1)
                    [HDR] _Phase ("Other", Vector) = (1,1,1,1)
                    [HDR] _RimColor ("HDR color", Color) = (1,1,1,1)
                }
            }
            """, extract);
        var config = fixture.Config(fixture.Shaders) with { BuildTarget = target };
        var report = new ProjectMigration(config, fixture.Candidate).Run();
        var candidate = Path.Combine(fixture.Candidate, source);
        string converted = File.ReadAllText(candidate);
        var lines = converted.Split('\n');
        string rim = lines.Single(line => line.Contains("_RimColor: ", StringComparison.Ordinal));
        var fields = System.Text.RegularExpressions.Regex.Match(rim,
            @"_RimColor: \{r: ([^,]+), g: ([^,]+), b: 1, a: 0\.2\}");
        float Channel(int index) => float.Parse(fields.Groups[index].Value, System.Globalization.CultureInfo.InvariantCulture);
        Check(fields.Success && MathF.Abs(Channel(1) - .21404114f) < 1e-6f && MathF.Abs(Channel(2) - 4.5947933f) < 1e-6f,
            "legacy RGB is linearized with the HDR exponent and alpha stays as authored: " + rim);
        Check(converted.Replace(rim, "      _RimColor: {r: 0.5, g: 2, b: 1, a: 0.2}", StringComparison.Ordinal) == material,
            "unrelated values, vector properties, non-HDR colors and texture references are preserved");
        Check(File.ReadAllText(original) == material && File.ReadAllText(Path.Combine(fixture.Candidate, authored)) == material,
            "both the immutable recovery and the user-authored material are untouched");
        Check(report.LegacyHdrMaterials is not null && File.Exists(Path.Combine(fixture.Candidate, report.LegacyHdrMaterials)),
            "the project reports exactly which stock values were changed");
    }

    private static void ReplaceComputeShader()
    {
        using var fixture = new Fixture();
        fixture.CreateManaged("NativeH3VR.Game", "Assembly-CSharp", "H3VR", "Fixture");
        fixture.WriteSteamActions("{\"default_bindings\":[{\"binding_url\":\"fixture.json\"}]}", ("fixture.json", "{}"));
        const string guid = "34fa6b0930ae46b8966df6dac490fd24";
        const string source = "Assets/ComputeShader/Readback.asset";
        const string compute = "Assets/ComputeShader/Readback.compute";
        const string dependency = "Assets/Depends.asset";
        var original = Path.Combine(fixture.Recovered, source);
        var meta = $"fileFormatVersion: 2\nguid: {guid}\nNativeFormatImporter:\n  mainObjectFileID: 7200000\n";
        fixture.WriteRecovered(source, "--- !u!72 &7200000\nComputeShader:\n  fixture: true\n", guid);
        File.WriteAllText(original + ".meta", meta);
        SafeFiles.Copy(original, Path.Combine(fixture.Candidate, source));
        SafeFiles.Copy(original + ".meta", Path.Combine(fixture.Candidate, source) + ".meta");
        fixture.WriteCandidate(dependency, $"probe: {{fileID: 7200000, guid: {guid}, type: 2}}\n");
        var extract = new StepReceipt("extract", "synthetic-extraction", DateTimeOffset.UtcNow,
            new() { [original] = SafeFiles.Hash(original), [original + ".meta"] = SafeFiles.Hash(original + ".meta") });
        fixture.WriteShaderOverlay("Shader \"Fixture\" { }", extract);

        var assets = Path.Combine(fixture.Shaders, "Assets");
        var overlayCompute = Path.Combine(fixture.Shaders, compute);
        Directory.CreateDirectory(Path.GetDirectoryName(overlayCompute)!);
        File.WriteAllText(overlayCompute, "#pragma kernel Readback\n[numthreads(1, 1, 1)]\nvoid Readback() { }\n");
        File.WriteAllText(overlayCompute + ".meta", $"fileFormatVersion: 2\nguid: {guid}\nComputeShaderImporter:\n  externalObjects: {{}}\n");
        var receipt = Path.Combine(assets, "ComputeShader", ComputeShaderOverlay.ReceiptName);
        void WriteReceipt(string computeHash) => SafeFiles.AtomicJson(receipt, new
        {
            schemaVersion = 1,
            extractionFingerprint = extract.Fingerprint,
            files = new[] { new {
                source = "Readback.asset", sourceSha256 = extract.Files[original], sourceMetaSha256 = extract.Files[original + ".meta"],
                compute = "Readback.compute", computeSha256 = computeHash, computeMetaSha256 = SafeFiles.Hash(overlayCompute + ".meta"),
                kernels = new[] { new { name = "Readback" } }
            } }
        });
        WriteReceipt("wrong");
        var config = fixture.Config(fixture.Shaders);
        Check(Capture(() => ComputeShaderOverlay.Stage(config, assets, Path.Combine(fixture.Candidate, "Assets"))) is InvalidDataException,
            "a compute overlay with changed bytes is rejected");
        WriteReceipt(SafeFiles.Hash(overlayCompute));
        new ProjectMigration(config, fixture.Candidate).Run();
        Check(File.Exists(Path.Combine(fixture.Candidate, compute)) && !File.Exists(Path.Combine(fixture.Candidate, source)),
            "the translated source replaces the old serialized compute asset in the candidate");
        Check(GuidFrom(File.ReadAllText(Path.Combine(fixture.Candidate, compute) + ".meta")) == guid &&
              File.ReadAllText(Path.Combine(fixture.Candidate, dependency)).Contains($"guid: {guid}, type: 3", StringComparison.Ordinal),
            "the compute GUID and fileID survive the move to the source importer");
        Check(SafeFiles.Hash(original) == extract.Files[original], "the immutable extraction was preserved");
    }

    private static void ApplyIntegration()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Candidate);
        var bytes = Encoding.UTF8.GetBytes("provider"); File.WriteAllBytes(Path.Combine(fixture.Root, "provider.dll"), bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var entries = Integration.RequiredMetalFiles().Select(destination => new { source = "provider.dll", destination, sha256 = hash });
        const string selectedVersion = "6000.7.0b9";
        var manifest = new { schemaVersion = 1, unityVersion = selectedVersion, buildTarget = "StandaloneOSX", xrBackend = "OpenXR", provider = Integration.MetalBridge, files = entries };
        var manifestPath = Path.Combine(fixture.Root, "integration.json"); File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        Check(Capture(() => Integration.Apply(manifestPath, fixture.Candidate, "StandaloneOSX", ConversionConfig.TargetVersion)) is InvalidDataException,
            "integration for a different 6.7 release was accepted");
        Integration.Apply(manifestPath, fixture.Candidate, "StandaloneOSX", selectedVersion);
        Check(SafeFiles.Hash(Path.Combine(fixture.Candidate, "Assets/Plugins/macOS/libNativeH3VRMetal.dylib")) == hash, "copies the verified provider bytes");
        Check(File.ReadAllText(Path.Combine(fixture.Candidate, "ProjectSettings/H3VRProvider.json")).Contains(Integration.MetalBridge, StringComparison.Ordinal), "persists the Metal bridge selection for the editor configuration");
        Integration.Apply(manifestPath, fixture.Candidate, "StandaloneOSX", selectedVersion);
        var missingProvider = new { schemaVersion = 1, unityVersion = ConversionConfig.TargetVersion, buildTarget = "StandaloneLinux64", xrBackend = "OpenXR", files = new[] { new { source = "provider.dll", destination = "Assets/OpenXRProvider/linux.dll", sha256 = hash } } };
        var missingPath = Path.Combine(fixture.Root, "missing-provider.json"); File.WriteAllText(missingPath, JsonSerializer.Serialize(missingProvider));
        Check(Capture(() => Integration.Apply(missingPath, fixture.Candidate, "StandaloneLinux64")) is InvalidDataException, "non-Windows integration requires a managed OpenXR loader type");
    }

    private static void PlaceMacPlayerLibraries()
    {
        using var fixture = new Fixture();
        var plugins = Path.Combine(fixture.Candidate, "Contents", "PlugIns");
        var architecture = Path.Combine(plugins, "ARM64");
        Directory.CreateDirectory(architecture);
        var libraries = Integration.RequiredMetalFiles().Where(path => path.EndsWith(".dylib", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToArray();
        foreach (var library in libraries) File.WriteAllText(Path.Combine(architecture, library!), library);
        var neighboringFile = Path.Combine(architecture, "unrelated.txt");
        File.WriteAllText(neighboringFile, "preserve");
        string duplicate = Path.Combine(plugins, libraries[^1]!);
        File.WriteAllText(duplicate, "conflict");
        Check(Capture(() => Integration.PlaceMacPlayerLibraries(fixture.Candidate)) is InvalidDataException &&
              libraries.All(name => File.Exists(Path.Combine(architecture, name!))), "layout conflict changed the candidate");
        File.Delete(duplicate);
        Integration.PlaceMacPlayerLibraries(fixture.Candidate);
        Integration.PlaceMacPlayerLibraries(fixture.Candidate);
        Check(libraries.All(name => File.ReadAllText(Path.Combine(plugins, name!)) == name &&
              !File.Exists(Path.Combine(architecture, name!))) && File.ReadAllText(neighboringFile) == "preserve",
              "platform libraries did not move to the Unity resolver directory");
    }

    private static void RejectUnsafeIntegration()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Candidate);
        File.WriteAllText(Path.Combine(fixture.Root, "source"), "valid");
        string Build(string source, string destination, string hash) => JsonSerializer.Serialize(new { schemaVersion = 1, unityVersion = ConversionConfig.TargetVersion, buildTarget = "StandaloneLinux64", xrBackend = "OpenXR", files = new[] { new { source, destination, sha256 = hash } } });
        var path = Path.Combine(fixture.Root, "integration.json"); File.WriteAllText(path, Build("source", "Assets/OpenXRProvider/provider", new string('0', 64)));
        Check(Capture(() => Integration.Apply(path, fixture.Candidate, "StandaloneLinux64")) is InvalidDataException, "hash mismatch fails closed");
        File.WriteAllText(path, Build("source", "Assets/Plugins/../../outside", SafeFiles.Hash(Path.Combine(fixture.Root, "source"))));
        Check(Capture(() => Integration.Apply(path, fixture.Candidate, "StandaloneLinux64")) is InvalidDataException, "destination traversal fails closed");
    }

    private static string? GuidFrom(string meta) => System.Text.RegularExpressions.Regex.Match(meta, @"(?m)^guid: ([0-9a-f]{32})$").Groups[1].Value;
    private static Exception Capture(Action action) { try { action(); } catch (Exception e) { return e; } return new Exception("Expected operation to fail."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "h3vr-migration-test-" + Guid.NewGuid().ToString("N"));
        public string Candidate => Path.Combine(Root, "candidate"); public string Recovered => Path.Combine(Root, "recovered");
        public string Managed => Path.Combine(Root, "managed"); public string Shaders => Path.Combine(Root, "shaders"); public string Game => Path.Combine(Root, "game");
        public Fixture() { Directory.CreateDirectory(Root); Directory.CreateDirectory(Candidate); Directory.CreateDirectory(Recovered); Directory.CreateDirectory(Managed); Directory.CreateDirectory(Path.Combine(Recovered, "Assets")); Directory.CreateDirectory(Path.Combine(Candidate, "Assets")); Directory.CreateDirectory(Path.Combine(Candidate, "ProjectSettings")); File.WriteAllText(Path.Combine(Candidate, "ProjectSettings/ProjectSettings.asset"), "%YAML 1.1\n--- !u!129 &1\nPlayerSettings:\n  serializedVersion: 11\n"); }
        public ConversionConfig Config(string? shader = null) => new() { Workspace = Root, RecoveredProject = Recovered, ShaderSources = shader, GameDirectory = Path.Combine(Root, "game"), UnityEditor = Path.Combine(Root, "Unity"), BuildTarget = "StandaloneOSX" };
        public void CreateManaged(string assemblyName, string originalAssemblyName, string ns, string typeName)
        {
            static AssemblyDefinition Make(string name, string ns, string typeName) { var a = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll); a.MainModule.Types.Add(new TypeDefinition(ns, typeName, Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, a.MainModule.TypeSystem.Object)); return a; }
            Make(assemblyName, ns, typeName).Write(Path.Combine(Managed, assemblyName + ".dll"));
            var input = Path.Combine(Root, "game", "h3vr_Data", "Managed"); Directory.CreateDirectory(input);
            Make(originalAssemblyName, ns, typeName).Write(Path.Combine(input, originalAssemblyName + ".dll"));
        }
        public void WriteSteamActions(string actions, params (string Name, string Content)[] bindings)
        {
            Directory.CreateDirectory(Game); File.WriteAllText(Path.Combine(Game, "actions.json"), actions);
            foreach (var (name, content) in bindings) File.WriteAllText(Path.Combine(Game, name), content);
        }
        public void WriteRecovered(string relative, string content, string guid, int executionOrder = 0) { Write(Path.Combine(Recovered, relative), content); Write(Path.Combine(Recovered, relative + ".meta"), Meta(guid, executionOrder)); }
        public void WriteCandidate(string relative, string content, string? guid = null, int executionOrder = 0) { Write(Path.Combine(Candidate, relative), content); if (guid is not null) Write(Path.Combine(Candidate, relative + ".meta"), Meta(guid, executionOrder)); }
        public void WriteShaderOverlay(string content, StepReceipt extraction, string? include = null)
        {
            const string relative = "Assets/Fixture.shader";
            const string includeRelative = "Assets/Fixture.cginc";
            WriteRecovered(relative, "Shader \"Original fixture\" { }", new string('2', 32));
            var original = Path.Combine(Recovered, relative);
            var output = Path.Combine(Shaders, relative);
            Write(output, content);
            SafeFiles.Copy(original + ".meta", output + ".meta");
            extraction.Files[original] = SafeFiles.Hash(original);
            extraction.Files[original + ".meta"] = SafeFiles.Hash(original + ".meta");
            SafeFiles.AtomicJson(Path.Combine(Root, ".conversion/extract.json"), extraction);

            var repairs = new List<object>();
            var rawHash = SafeFiles.Hash(output);
            if (include is not null)
            {
                Write(Path.Combine(Shaders, includeRelative), include);
                rawHash = SafeFiles.HashText("Shader \"Raw fixture\" { }");
                repairs.Add(new
                {
                    path = relative,
                    before = rawHash,
                    after = SafeFiles.Hash(output),
                    passes = new[] { new { includes = new[] { new { path = includeRelative, sha256 = SafeFiles.HashText(include) } } } }
                });
            }
            SafeFiles.AtomicJson(Path.Combine(Shaders, GraphicsShaderOverlay.ReceiptName), new
            {
                schemaVersion = 1,
                validationState = "identity-only-unvalidated",
                extractionFingerprint = extraction.Fingerprint,
                unmappedProjectPaths = Array.Empty<string>(),
                keywordRepairs = repairs,
                links = new[] { new
                {
                    projectPath = relative, projectSha256 = extraction.Files[original],
                    projectMetaSha256 = extraction.Files[original + ".meta"], rawSha256 = rawHash
                } }
            });
        }
        private static string Meta(string guid, int order) => $"fileFormatVersion: 2\nguid: {guid}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: {order}\n";
        private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
