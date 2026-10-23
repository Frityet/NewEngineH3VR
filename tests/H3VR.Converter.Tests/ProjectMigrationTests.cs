using H3VR.Converter;
using Mono.Cecil;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace H3VR.Converter.Tests;

public static class ProjectMigrationTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("MD4 vectors and Unity file ID", Md4Vectors);
        yield return ("project migration rebinds scripts and keeps recovery immutable", MigrateFixture);
        yield return ("OpenXR input staging copies the manifest and every referenced binding", SteamVrActions);
        yield return ("missing referenced controller bindings fail closed", MissingSteamVrBinding);
        yield return ("shader overlay rejects dummy exports", RejectDummyShader);
        yield return ("integration applies a hash-pinned OpenXR provider", ApplyIntegration);
        yield return ("integration rejects traversal and hash mismatch", RejectUnsafeIntegration);
    }

    private static void Md4Vectors()
    {
        Check(Convert.ToHexStringLower(Md4.Hash([])) == "31d6cfe0d16ae931b73c59d7e0c089c0", "empty MD4 vector");
        Check(Convert.ToHexStringLower(Md4.Hash("abc"u8)) == "a448017aaf21d8525fc10ae87aa6729d", "abc MD4 vector");
        Check(Md4.UnityFileId("H3VR", "Fixture") == BitConverter.ToInt32(Md4.Hash(Encoding.UTF8.GetBytes("s\0\0\0H3VRFixture")), 0), "Unity fileID is signed little-endian MD4 prefix");
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
        fixture.WriteCandidate("Packages/manifest.json", "{\"dependencies\":{\"com.unity.ugui\":\"1.0.0\",\"com.unity.modules.ai\":\"1.0.0\"}}\n");
        fixture.WriteSteamActions("{\"default_bindings\":[{\"binding_url\":\"bindings_knuckles.json\"}]}", ("bindings_knuckles.json", "{\"controller_type\":\"knuckles\"}"));
        var sourceManifest = Path.Combine(fixture.Recovered, relative + ".meta"); var sourceHash = SafeFiles.Hash(sourceManifest);
        var report = new ProjectMigration(fixture.Config(), fixture.Candidate).Run();
        var rewritten = File.ReadAllText(Path.Combine(fixture.Candidate, "Assets/Scenes/Test.prefab"));
        var managedMeta = File.ReadAllText(Path.Combine(fixture.Candidate, "Assets/Plugins/NativeH3VR.Game.dll.meta"));
        Check(rewritten.Contains("guid: " + GuidFrom(managedMeta), StringComparison.Ordinal), "serialized MonoScript points at migrated managed assembly GUID");
        Check(rewritten.Contains("fileID: " + Md4.UnityFileId("H3VR", "Fixture"), StringComparison.Ordinal), "serialized MonoScript uses Unity MD4 type file ID");
        Check(report.RewrittenReferences == 1 && report.ScriptBindings == 1, "report counts actual rebinding");
        Check(managedMeta.Contains("H3VR.Fixture: 17", StringComparison.Ordinal), "original Unity execution order is copied to managed plugin metadata");
        Check(File.Exists(Path.Combine(fixture.Candidate, "NativeH3VR-RecoveredSources/Assembly-CSharp/H3VR/Fixture.cs")), "recovered source retained in candidate archive");
        Check(SafeFiles.Hash(sourceManifest) == sourceHash, "original recovered project metadata unchanged");
        Check(!JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Candidate, "Packages/manifest.json"))).RootElement.GetProperty("dependencies").TryGetProperty("com.unity.ugui", out _), "duplicate UGUI package removed");
        Check(File.ReadAllText(Path.Combine(fixture.Candidate, "Packages/manifest.json")).Contains("com.unity.xr.openxr", StringComparison.Ordinal), "pinned OpenXR package configured");
        var packages = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Candidate, "Packages/manifest.json"))).RootElement.GetProperty("dependencies");
        Check(packages.GetProperty("com.unity.xr.management").GetString() == "4.4.0" && packages.GetProperty("com.unity.inputsystem").GetString() == "1.6.3" && packages.GetProperty("com.unity.xr.core-utils").GetString() == "2.1.1" && packages.GetProperty("com.unity.xr.legacyinputhelpers").GetString() == "2.1.7", "OpenXR dependency graph is explicitly pinned for Unity 2021");
        Check(File.ReadAllText(Path.Combine(fixture.Candidate, "ProjectSettings/ProjectSettings.asset")).Contains("activeInputHandler: 2", StringComparison.Ordinal), "both legacy and new input backends are enabled before first import");
        Check(File.ReadAllText(Path.Combine(fixture.Candidate, "ProjectSettings/ProjectVersion.txt")).Contains("2021.3.45f2", StringComparison.Ordinal), "Unity target pinned");
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
        var overlayAssets = Path.Combine(fixture.Shaders, "Assets"); Directory.CreateDirectory(overlayAssets);
        File.WriteAllText(Path.Combine(overlayAssets, "Bad.shader"), "Shader \"Fixture\" { //DummyShaderTextExporter\n }");
        File.WriteAllText(Path.Combine(overlayAssets, "Bad.shader.meta"), "fileFormatVersion: 2\nguid: 22222222222222222222222222222222\n");
        var error = Capture(() => new ProjectMigration(fixture.Config(fixture.Shaders), fixture.Candidate).Run());
        Check(error is InvalidDataException && error.Message.Contains("Dummy shader", StringComparison.Ordinal), "rejects known placeholder shader content");
        Check(!File.Exists(Path.Combine(fixture.Candidate, "Assets/Bad.shader")), "does not copy the rejected shader");
    }

    private static void ApplyIntegration()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Candidate);
        var bytes = Encoding.UTF8.GetBytes("provider"); File.WriteAllBytes(Path.Combine(fixture.Root, "provider.dll"), bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var manifest = new { schemaVersion = 1, unityVersion = ConversionConfig.TargetVersion, buildTarget = "StandaloneOSX", xrBackend = "OpenXR", provider = "H3VR.TestProvider", files = new[] { new { source = "provider.dll", destination = "Assets/Plugins/provider.dll", sha256 = hash } } };
        var manifestPath = Path.Combine(fixture.Root, "integration.json"); File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        Integration.Apply(manifestPath, fixture.Candidate, "StandaloneOSX");
        Check(SafeFiles.Hash(Path.Combine(fixture.Candidate, "Assets/Plugins/provider.dll")) == hash, "copies the verified provider bytes");
        Check(File.ReadAllText(Path.Combine(fixture.Candidate, "ProjectSettings/H3VRProvider.json")).Contains("H3VR.TestProvider", StringComparison.Ordinal), "persists the managed XR loader type for editor configuration");
        Integration.Apply(manifestPath, fixture.Candidate, "StandaloneOSX");
        var missingProvider = new { schemaVersion = 1, unityVersion = ConversionConfig.TargetVersion, buildTarget = "StandaloneLinux64", xrBackend = "OpenXR", files = new[] { new { source = "provider.dll", destination = "Assets/OpenXRProvider/linux.dll", sha256 = hash } } };
        var missingPath = Path.Combine(fixture.Root, "missing-provider.json"); File.WriteAllText(missingPath, JsonSerializer.Serialize(missingProvider));
        Check(Capture(() => Integration.Apply(missingPath, fixture.Candidate, "StandaloneLinux64")) is InvalidDataException, "non-Windows integration requires a managed OpenXR loader type");
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
        private static string Meta(string guid, int order) => $"fileFormatVersion: 2\nguid: {guid}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: {order}\n";
        private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
