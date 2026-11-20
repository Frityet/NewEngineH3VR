using H3VR.Converter;
using System.Text.Json;

namespace H3VR.Converter.Tests;

public static class PipelineTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("external extraction receipt resumes only when source inputs remain identical", ExtractResume);
        yield return ("extract receipt resumes after Unity, shader, and target settings are added", ExtractResumeAfterOptionsAdded);
        yield return ("cold extraction fingerprints its worker and can resume after shader tooling changes", ExtractionUsesScopedTooling);
        yield return ("external extraction fingerprint covers StreamingAssets inputs", () => MutationIsRejected("streaming"));
        yield return ("external extraction fingerprint covers native plugin inputs", () => MutationIsRejected("plugin"));
        yield return ("project fingerprint covers shader overlay inputs", ShaderMutationChangesProjectFingerprint);
        yield return ("managed fingerprint covers Unity and target but ignores shaders", ManagedFingerprintSelection);
        yield return ("graphics upgrade scopes package and shader fingerprints to project and player steps", GraphicsFingerprintSelection);
        yield return ("workspace lock excludes concurrent conversion and can be reacquired", WorkspaceLock);
        yield return ("resuming a workspace refuses a lock redirected outside the checkout", WorkspaceLockBoundary);
        yield return ("baseline records only null MonoScript slots by source object and asset", BaselineNullScripts);
        yield return ("build receipt tracks generated StreamingAssets bundle bytes", BuildReceiptTracksBundles);
        yield return ("staging removes only verified recovered Unity bundles and their manifests", StageStreamingAssets);
        yield return ("failed bundle plan remains inspectable after candidate cleanup", FailedBundlePlanSurvivesCleanup);
        yield return ("cold extraction receipt tracks provenance maps", ColdReceiptTracksMaps);
        yield return ("shared extraction reuses verified bytes and rejects source mutations", SharedExtractionRejectsChanges);
        yield return ("URP receipt refresh tracks generated defaults while preserving input files", GeneratedUrpDefaults);
        yield return ("successful editor receipt records named shader upgrades and keeps other hashes", ShaderUpgradeReceipt);
    }

    private static void WorkspaceLockBoundary()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var lockPath = Path.Combine(fixture.Workspace, ".conversion", "lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        Write(Path.Combine(fixture.Workspace, ".h3vr-converter-workspace"), "fixture");
        var external = Path.Combine(fixture.rootPath, "external-lock");
        File.CreateSymbolicLink(lockPath, external);
        var error = Capture(() => fixture.Pipeline().Run("extract", CancellationToken.None).GetAwaiter().GetResult());
        Check(error is InvalidDataException && !File.Exists(external), "resuming a workspace followed a symlink to an external lock");
    }

    private static void StageStreamingAssets()
    {
        using var fixture = new Fixture();
        var config = fixture.Config() with { BuildTarget = "StandaloneOSX" };
        var streaming = Path.Combine(config.GameData, "StreamingAssets");
        var project = Path.Combine(fixture.Workspace, "project");
        var staged = Path.Combine(project, "Assets", "StreamingAssets");
        Directory.CreateDirectory(staged);
        foreach (var bundle in new[] { "fixture_bundle", "StreamingAssets" })
        {
            File.WriteAllText(Path.Combine(streaming, bundle), "UnityFS\0synthetic");
            File.WriteAllText(Path.Combine(streaming, bundle + ".manifest"), "synthetic manifest");
        }
        File.WriteAllText(Path.Combine(streaming, "music.bank"), "synthetic audio");
        File.WriteAllText(Path.Combine(streaming, "video.manifest"), "preserve legacy media sidecar");
        foreach (var file in Directory.GetFiles(streaming))
        {
            var copy = Path.Combine(staged, Path.GetFileName(file));
            File.Copy(file, copy);
            File.WriteAllText(copy + ".meta", "synthetic meta");
        }
        var original = fixture.InputHashes();
        LocalAssets.Stage(config, project);
        LocalAssets.Stage(config, project);
        var expected = new[] { "content.bundle", "music.bank", "video.manifest" }
            .SelectMany(name => new[] { name, name + ".meta" }).Order();
        Check(Directory.GetFiles(staged).Select(Path.GetFileName).Order().SequenceEqual(expected),
            "staging changed unrelated files or retained an unconverted bundle");
        Check(original.OrderBy(pair => pair.Key).SequenceEqual(fixture.InputHashes().OrderBy(pair => pair.Key)),
            "staging changed an input");
        var foreign = Path.Combine(staged, "fixture_bundle");
        File.WriteAllText(foreign, "unrelated bytes");
        Check(Capture(() => LocalAssets.Stage(config, project)) is IOException, "staging accepted a conflicting bundle");
        Check(File.ReadAllText(foreign) == "unrelated bytes", "staging removed a conflicting file");
    }

    private static void GeneratedUrpDefaults()
    {
        using var fixture = new Fixture();
        var project = Path.Combine(fixture.Workspace, "project");
        const string globalRelative = "Assets/UniversalRenderPipelineGlobalSettings.asset";
        const string volumeRelative = "Assets/DefaultVolumeProfile.asset";
        var generated = Path.Combine(project, globalRelative);
        var supplied = Path.Combine(project, volumeRelative);
        var material = Path.Combine(project, "Assets/Material/Stock.mat");
        Write(Path.Combine(fixture.Recovered, volumeRelative), string.Empty);
        foreach (string file in new[] { generated, supplied, material }) Write(file, "prepared");
        var old = SafeFiles.Hash(generated);
        var files = new[] { generated, supplied, material }.ToDictionary(file => file, SafeFiles.Hash);
        foreach (var file in files.Keys) Write(file, "unity run");

        GraphicsUpgradeProject.RefreshGeneratedDefaults(project, fixture.Recovered, files);
        Check(files[generated] == SafeFiles.Hash(generated), "URP-generated global settings were not refreshed");
        Check(files[supplied] == old && files[material] == old, "refresh accepted edits to supplied settings or a stock material");

        File.Delete(Path.Combine(fixture.Recovered, volumeRelative));
        Write(Path.Combine(fixture.Recovered, volumeRelative) + ".meta", "original importer metadata");
        GraphicsUpgradeProject.RefreshGeneratedDefaults(project, fixture.Recovered, files);
        Check(files[supplied] == old, "refresh adopted a volume created over recovery-supplied importer metadata");

        File.Delete(Path.Combine(fixture.Recovered, volumeRelative) + ".meta");
        GraphicsUpgradeProject.RefreshGeneratedDefaults(project, fixture.Recovered, files);
        Check(files[supplied] == SafeFiles.Hash(supplied) && files[material] == old,
            "refresh did not distinguish the Unity-generated default volume from stock material");
    }

    private static void ShaderUpgradeReceipt()
    {
        using var fixture = new Fixture();
        var project = Path.Combine(fixture.Workspace, "project");
        var assets = Path.Combine(project, "Assets");
        Directory.CreateDirectory(assets);
        var tracked = Path.Combine(assets, "Fixture Shader.shader");
        var include = Path.Combine(assets, "Fixture Include.cginc");
        var unlogged = Path.Combine(assets, "Unlogged.shader");
        var unrelated = Path.Combine(assets, "Unrelated.txt");
        foreach (var file in new[] { tracked, include, unlogged, unrelated }) File.WriteAllText(file, "old");
        var files = new[] { tracked, include, unlogged, unrelated }.ToDictionary(file => file, SafeFiles.Hash);
        var previous = files[tracked];
        foreach (var file in files.Keys) File.WriteAllText(file, "new");

        var rows = Pipeline.ImportShaderUpgrades(project, files,
        [
            "Upgraded shader: Assets/Fixture Shader.shader",
            "Upgraded shader include: Assets/Fixture Include.cginc",
            "Upgraded shader: Assets/Fixture Shader.shader",
            "Upgraded shader: Assets/Unrelated.txt",
            "Upgraded shader: Packages/Anything.shader",
        ]);
        Check(rows.Count == 2, "expected each upgraded shader and include once");
        var row = rows.Single(row => row.Path == "Assets/Fixture Shader.shader");
        Check(row.Before == previous && row.After == SafeFiles.Hash(tracked),
            "the editor receipt omitted the actual shader upgrade");
        var includeRow = rows.Single(row => row.Path == "Assets/Fixture Include.cginc");
        Check(includeRow.Before == previous && includeRow.After == files[include] && files[include] == SafeFiles.Hash(include),
            "the editor receipt omitted the actual shader include upgrade");
        Check(files[tracked] == row.After && files[unlogged] == previous && files[unrelated] == previous,
            "an unlogged shader or unrelated file was treated as an editor upgrade");
    }

    private static void ColdReceiptTracksMaps()
    {
        using var fixture = new Fixture();
        var recovery = Path.Combine(fixture.Workspace, "synthetic-recovery");
        Directory.CreateDirectory(Path.Combine(recovery, "ExportedProject"));
        Directory.CreateDirectory(Path.Combine(recovery, "AuxiliaryFiles"));
        var shader = Path.Combine(recovery, "AuxiliaryFiles", "shader-identity-v2.json");
        var paths = Path.Combine(recovery, "AuxiliaryFiles", "path_id_map.json");
        File.WriteAllText(shader, "synthetic identity"); File.WriteAllText(paths, "synthetic paths");
        var files = Pipeline.ColdRecoveryOutputFiles(recovery).ToDictionary(p => p, SafeFiles.Hash);
        var receipt = new StepReceipt("extract", "synthetic", DateTimeOffset.UtcNow, files);
        Check(Pipeline.ReceiptOutputsMatch(receipt), "unchanged map not accepted");
        File.WriteAllText(shader, "altered identity");
        Check(!Pipeline.ReceiptOutputsMatch(receipt), "altered identity map was accepted");
    }

    private static void SharedExtractionRejectsChanges()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.rootPath, "shared");
        string recovery = Path.Combine(source, "recovery");
        string assets = Path.Combine(recovery, "ExportedProject", "Assets");
        string receiptPath = Path.Combine(source, ".conversion", "extract.json");
        string file = Path.Combine(assets, "fixture.txt");
        Write(file, "original");
        Write(Path.Combine(recovery, "ExportedProject", "ProjectSettings", "ProjectVersion.txt"), "synthetic Unity project");
        Write(Path.Combine(recovery, "AuxiliaryFiles", "shader-identity-v2.json"), "{}");
        Write(Path.Combine(recovery, "AuxiliaryFiles", "path_id_map.json"), "{}");
        Write(Path.Combine(source, ".h3vr-converter-workspace"), "fixture");
        Write(Path.Combine(source, ".conversion", "lock"), "");
        var configuration = fixture.Config() with { RecoveredProject = null, ExtractionWorkspace = source };
        var pipeline = new Pipeline(fixture.Repository, configuration);
        var files = Pipeline.ColdRecoveryOutputFiles(recovery).ToDictionary(path => path, SafeFiles.Hash);
        var receipt = new StepReceipt("extract", pipeline.FingerprintForTests("extract"), DateTimeOffset.UtcNow, files);
        SafeFiles.AtomicJson(receiptPath, receipt);
        void Run() => pipeline.Run("extract", CancellationToken.None).GetAwaiter().GetResult();

        Run();
        Check(!Directory.Exists(Path.Combine(fixture.Workspace, "recovery")) && configuration.Export.StartsWith(recovery),
            "a shared build copied or ignored its recovery inputs");
        Check(SafeFiles.Hash(receiptPath) == SafeFiles.Hash(Path.Combine(fixture.Workspace, ".conversion", "extract.json")),
            "the destination receipt changed its source binding");
        Run();
        Write(file, "tampered");
        Check(Capture(Run) is InvalidDataException, "a shared workspace accepted altered asset bytes");
        Write(file, "original");
        Write(Path.Combine(assets, "extra.txt"), "unrecorded");
        Check(Capture(Run) is InvalidDataException, "a shared workspace accepted an unrecorded asset");
    }

    private static void ExtractResume()
    {
        using var fixture = new Fixture();
        var before = fixture.InputHashes();
        var pipeline = fixture.Pipeline();
        pipeline.Run("extract", CancellationToken.None).GetAwaiter().GetResult();
        var receiptPath = Path.Combine(fixture.Workspace, ".conversion/extract.json");
        var first = JsonDocument.Parse(File.ReadAllText(receiptPath)).RootElement.GetProperty("Fingerprint").GetString();
        pipeline.Run("extract", CancellationToken.None).GetAwaiter().GetResult();
        var second = JsonDocument.Parse(File.ReadAllText(receiptPath)).RootElement.GetProperty("Fingerprint").GetString();
        Check(first is not null && first == second, "identical extraction inputs did not verify the same receipt");
        Check(before.OrderBy(p => p.Key).SequenceEqual(fixture.InputHashes().OrderBy(p => p.Key)), "extract mutated game, recovery, repository, or shader inputs");
    }

    private static void MutationIsRejected(string kind)
    {
        using var fixture = new Fixture();
        var pipeline = fixture.Pipeline();
        pipeline.Run("extract", CancellationToken.None).GetAwaiter().GetResult();
        var (path, updated) = kind switch
        {
            "streaming" => (Path.Combine(fixture.GameData, "StreamingAssets/content.bundle"), "changed-streaming-bundle"),
            "plugin" => (Path.Combine(fixture.GameData, "Plugins/steam_api64.dll"), "changed-native-plugin"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        File.WriteAllText(path, updated);
        var expectedSourceHash = SafeFiles.Hash(path);
        var error = Capture(() => pipeline.Run("extract", CancellationToken.None).GetAwaiter().GetResult());
        Check(error is InvalidDataException && error.Message.Contains("inputs", StringComparison.OrdinalIgnoreCase), $"changed {kind} bytes did not invalidate the previous extraction receipt");
        Check(SafeFiles.Hash(path) == expectedSourceHash && File.ReadAllText(path) == updated, $"refused resume modified changed {kind} source bytes");
    }

    private static void ExtractResumeAfterOptionsAdded()
    {
        using var fixture = new Fixture();
        fixture.Pipeline().Run("extract", CancellationToken.None).GetAwaiter().GetResult();
        var config = fixture.Config() with
        {
            UnityEditor = Path.Combine(fixture.rootPath, "UnityEditor", "Unity"),
            ShaderSources = fixture.Shaders,
            BuildTarget = "StandaloneLinux64"
        };
        new Pipeline(fixture.Repository, config).Run("extract", CancellationToken.None).GetAwaiter().GetResult();
        Check(File.Exists(Path.Combine(fixture.Workspace, ".conversion/extract.json")),
            "adding editor/shader/target options invalidated the completed extract receipt");
    }

    private static void GraphicsFingerprintSelection()
    {
        using var fixture = new Fixture();
        fixture.CreateUnityInputs();
        var builtIn = fixture.Config();
        var upgraded = builtIn with { GraphicsUpgrade = true };
        var package = GraphicsUpgradeProject.EditorPackageManifest(upgraded);
        Write(package, $$"""{"name":"{{GraphicsUpgradeProject.PackageName}}","version":"{{GraphicsUpgradeProject.PackageVersion}}"}""");
        GraphicsUpgradeProject.VerifyEditorPackage(upgraded);
        var shader = Path.Combine(fixture.Repository, GraphicsUpgradeProject.RuntimeSource, "Shaders/GameClarity.shader");
        Write(shader, "original");
        var runtime = Path.Combine(fixture.Repository, GraphicsUpgradeProject.RuntimeSource, "Fixture.cs");
        Write(runtime, "// Original opt-in source");
        var stock = new Pipeline(fixture.Repository, builtIn);
        var urp = new Pipeline(fixture.Repository, upgraded);
        foreach (var step in new[] { "extract", "managed" })
            Check(stock.FingerprintForTests(step) == urp.FingerprintForTests(step), "Graphics upgrade changed an upstream fingerprint");
        var previous = stock.FingerprintForTests("project");
        var initialGraphics = urp.FingerprintForTests("project");
        Check(previous != initialGraphics, "project receipts did not identify the selected graphics pipeline");
        string managed = urp.FingerprintForTests("managed");
        Write(shader, "updated");
        Write(runtime, "// Updated opt-in source");
        Check(managed == urp.FingerprintForTests("managed"), "optional C# changed the managed receipt");
        Check(previous == stock.FingerprintForTests("project"), "optional shaders invalidated the built-in graphics project");
        Check(initialGraphics != urp.FingerprintForTests("project"), "optioned shader was absent from the URP fingerprint");
        Write(package, """{"name":"com.unity.render-pipelines.universal","version":"17.6.0"}""");
        Check(Capture(() => GraphicsUpgradeProject.VerifyEditorPackage(upgraded)) is InvalidDataException, "accepted a different URP package");
    }

    private static void ShaderMutationChangesProjectFingerprint()
    {
        using var fixture = new Fixture();
        fixture.CreateUnityInputs();
        var pipeline = fixture.Pipeline();
        var before = pipeline.FingerprintForTests("project");
        File.WriteAllText(Path.Combine(fixture.Shaders, "Assets/Includes/Lighting.cginc"), "changed-shader-include");
        var after = pipeline.FingerprintForTests("project");
        Check(before != after, "shader overlay changes were omitted from the project-stage fingerprint");
        File.WriteAllText(Path.Combine(fixture.Shaders, SourceStaging.ShaderVariantCatalogName), "{\"schemaVersion\":1}");
        Check(after != pipeline.FingerprintForTests("project"), "variant catalog was omitted from the project-stage fingerprint");
    }

    private static void ExtractionUsesScopedTooling()
    {
        using var fixture = new Fixture();
        fixture.CreateUnityInputs();
        var pipeline = new Pipeline(fixture.Repository, fixture.Config() with { RecoveredProject = null });
        var previousExtract = pipeline.FingerprintForTests("extract");
        var previousProject = pipeline.FingerprintForTests("project");
        var mirror = fixture.Repository + "-mirror";
        SafeFiles.CopyTree(fixture.Repository, mirror);
        var mirrored = new Pipeline(mirror, fixture.Config() with { RecoveredProject = null });
        Check(mirrored.FingerprintForTests("extract") == previousExtract,
            "identical extraction tooling in a second checkout must verify the same local inputs");

        Write(Path.Combine(fixture.Repository, "tools/ShaderOverlay/Link.cs"), "internal class Link {}");
        Check(pipeline.FingerprintForTests("extract") == previousExtract,
            "cold extraction included post-extraction shader tooling");
        Check(pipeline.FingerprintForTests("project") != previousProject,
            "project preparation ignored a change in shader tooling");

        Write(Path.Combine(fixture.Repository, "tools/AssetRipper.Worker/ShaderIdentityRecorder.cs"),
            "internal class ShaderIdentityRecorder {}");
        Check(pipeline.FingerprintForTests("extract") != previousExtract,
            "cold extraction ignored its shader identity worker");
    }

    private static void ManagedFingerprintSelection()
    {
        using var fixture = new Fixture();
        fixture.CreateUnityInputs();
        var pipeline = fixture.Pipeline();
        var before = pipeline.FingerprintForTests("managed");
        File.WriteAllText(Path.Combine(fixture.Shaders, "Assets/Includes/Lighting.cginc"), "later-project-shader");
        var config = fixture.Config();
        var afterProjectOptions = new Pipeline(fixture.Repository, config).FingerprintForTests("managed");
        Check(before == afterProjectOptions, "managed-stage fingerprint included shader settings that begin at project preparation");
        var targetChange = new Pipeline(fixture.Repository, config with { BuildTarget = "StandaloneOSX" }).FingerprintForTests("managed");
        Check(before != targetChange, "managed-stage fingerprint ignored the target-specific adapter");
        var versionChange = new Pipeline(fixture.Repository, config with { UnityVersion = "6000.7.0b9" }).FingerprintForTests("managed");
        Check(before != versionChange, "managed-stage fingerprint ignored the selected Unity version");
        File.WriteAllText(Path.Combine(fixture.EngineAssemblies, "UnityEngine.CoreModule.dll"), "updated-engine-assembly");
        var afterUnityChange = new Pipeline(fixture.Repository, config).FingerprintForTests("managed");
        Check(before != afterUnityChange, "managed-stage fingerprint ignored Unity engine inputs");
    }

    private static void WorkspaceLock()
    {
        using var fixture = new Fixture();
        var pipeline = fixture.Pipeline();
        pipeline.Run("extract", CancellationToken.None).GetAwaiter().GetResult();
        var lockPath = Path.Combine(fixture.Workspace, ".conversion/lock");
        Exception? error;
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            error = Capture(() => pipeline.Run("extract", CancellationToken.None).GetAwaiter().GetResult());
        Check(error is IOException, "a second converter acquired the workspace while the exclusive lock was held");
        pipeline.Run("extract", CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void BaselineNullScripts()
    {
        using var fixture = new Fixture();
        var asset = "Assets/Scenes/NullScript.unity";
        Write(Path.Combine(fixture.Recovered, asset), "%YAML 1.1\n--- !u!114 &1001\nMonoBehaviour:\n  m_GameObject: {fileID: 77}\n  m_Script: {fileID: 0}\n--- !u!114 &1002\nMonoBehaviour:\n  m_GameObject: {fileID: 88}\n  m_Script: {fileID: 11500000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}\n");
        AssetBaseline.Capture(fixture.Recovered);
        var rows = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Recovered, "ProjectSettings/H3VRBaseline.json")))
            .RootElement.GetProperty("missingScripts").EnumerateArray().ToArray();
        Check(rows.Length == 1, "nonzero unresolved MonoScript reference was incorrectly included in the source waiver");
        Check(rows[0].GetProperty("asset").GetString() == asset && rows[0].GetProperty("gameObject").GetInt64() == 77 && rows[0].GetProperty("count").GetInt32() == 1,
            "null source script slot was not attributed to the exact asset, game object fileID, and occurrence count");
    }

    private static void BuildReceiptTracksBundles()
    {
        using var fixture = new Fixture();
        var player = Path.Combine(fixture.rootPath, "player-output");
        var streamingAssets = Path.Combine(player, "H3VR.app/Contents/Resources/Data/StreamingAssets");
        Write(Path.Combine(player, "H3VR.app/Contents/MacOS/H3VR"), "player-binary");
        Write(Path.Combine(streamingAssets, "manifest.bundle"), "rebuilt-bundle");
        Write(Path.Combine(streamingAssets, "manifest.bundle.manifest"), "bundle-dependencies");
        var outputFiles = SafeFiles.Files(player);
        var receipt = new StepReceipt("build", "test-fingerprint", DateTimeOffset.UtcNow,
            outputFiles.ToDictionary(Path.GetFullPath, SafeFiles.Hash));
        Check(Pipeline.ReceiptOutputsMatch(receipt), "fresh build outputs did not match their receipt");
        File.WriteAllText(Path.Combine(streamingAssets, "manifest.bundle"), "changed-bundle");
        Check(!Pipeline.ReceiptOutputsMatch(receipt), "changing a generated StreamingAssets bundle was not detected by the build receipt");
    }

    private static void FailedBundlePlanSurvivesCleanup()
    {
        using var fixture = new Fixture();
        var candidate = Path.Combine(fixture.rootPath, "project-candidate");
        var receipts = Path.Combine(fixture.Workspace, ".conversion");
        var sourcePlan = Path.Combine(candidate, "bundle-plan.json");
        Write(sourcePlan, "{\"schemaVersion\":1,\"issues\":[\"unresolved address\"]}\n");
        var diagnostic = Pipeline.PreserveFailedBundlePlan(sourcePlan, receipts);
        Directory.Delete(candidate, true);
        using var saved = JsonDocument.Parse(File.ReadAllText(diagnostic));
        Check(saved.RootElement.GetProperty("issues")[0].GetString() == "unresolved address",
            "bundle-plan issue details were lost when the failed candidate was removed");
    }

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception e) { return e; }
    }
    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "h3vr-pipeline-tests-" + Guid.NewGuid().ToString("N"));
        public string rootPath => root;
        public string Repository => Path.Combine(root, "repository");
        public string GameDirectory => Path.Combine(root, "game");
        public string GameData => Path.Combine(GameDirectory, "h3vr_Data");
        public string Recovered => Path.Combine(root, "recovered");
        public string Shaders => Path.Combine(root, "shaders");
        public string Workspace => Path.Combine(Repository, ".local", "workspace");
        public string UnityData => Path.Combine(root, "UnityEditor", "Data");
        public string EngineAssemblies => Path.Combine(UnityData, "Resources", "Scripting", "Managed", "UnityEngine");

        public Fixture()
        {
            foreach (var name in new[] { "src", "tools", "editor", "runtime" })
            {
                var directory = Path.Combine(Repository, name); Directory.CreateDirectory(directory);
                Write(Path.Combine(directory, "Fixture.cs"), "// fingerprint fixture\n");
            }
            Write(Path.Combine(Repository, "Directory.Build.props"), "<Project />\n");
            Write(Path.Combine(Repository, "global.json"), "{}\n");
            Write(Path.Combine(GameData, "Managed/Assembly-CSharp.dll"), "managed-game-input");
            Write(Path.Combine(GameData, "StreamingAssets/content.bundle"), "bundle-bytes");
            Write(Path.Combine(GameData, "Plugins/steam_api64.dll"), "native-plugin-bytes");
            Write(Path.Combine(GameDirectory, "actions.json"), "{}\n");
            Write(Path.Combine(Recovered, "ProjectSettings/ProjectVersion.txt"), "m_EditorVersion: 2020.3.0f1\n");
            Directory.CreateDirectory(Path.Combine(Recovered, "Assets"));
            Write(Path.Combine(Recovered, "Assets/source.txt"), "recovered-source");
            Write(Path.Combine(Shaders, "Assets/Includes/Lighting.cginc"), "shader-include");
            Directory.CreateDirectory(Workspace);
        }

        public Pipeline Pipeline()
        {
            return new Pipeline(Repository, Config());
        }

        public ConversionConfig Config() => new()
        {
            GameDirectory = GameDirectory,
            UnityEditor = Path.Combine(root, "UnityEditor", "Unity"),
            Workspace = Workspace,
            RecoveredProject = Recovered,
            ShaderSources = Shaders,
            BuildTarget = "StandaloneWindows64"
        };

        public void CreateUnityInputs()
        {
            Write(Path.Combine(root, "UnityEditor/Unity"), "synthetic editor executable");
            Write(Path.Combine(EngineAssemblies, "UnityEngine.CoreModule.dll"), "synthetic engine assembly");
            Write(Path.Combine(UnityData, "Resources/Scripting/MonoBleedingEdge/lib/mono/4.7.1-api/mscorlib.dll"), "synthetic framework assembly");
            Write(Path.Combine(root, "PlaybackEngines/MacStandaloneSupport/Variations/mono/Managed/UnityEngine.VideoModule.dll"),
                "synthetic macOS Mono player assembly");
        }

        public Dictionary<string, string> InputHashes()
        {
            var files = new List<string>();
            foreach (var directory in new[] { GameData, Recovered, Shaders }) files.AddRange(SafeFiles.Files(directory));
            foreach (var directory in new[] { "src", "tools", "editor", "runtime" }) files.AddRange(SafeFiles.Files(Path.Combine(Repository, directory)));
            files.AddRange(new[] { Path.Combine(Repository, "Directory.Build.props"), Path.Combine(Repository, "global.json"), Path.Combine(GameDirectory, "actions.json") });
            return files.Distinct().ToDictionary(path => Path.GetFullPath(path), path => SafeFiles.Hash(path), StringComparer.Ordinal);
        }

        public void Dispose() { try { Directory.Delete(root, true); } catch { } }
    }
}
