using H3VR.Converter;
using System.Text.Json;

namespace H3VR.Converter.Tests;

public static class PipelineTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("external extraction receipt resumes only when source inputs remain identical", ExtractResume);
        yield return ("extract receipt resumes after Unity, shader, and target settings are added", ExtractResumeAfterOptionsAdded);
        yield return ("external extraction fingerprint covers StreamingAssets inputs", () => MutationIsRejected("streaming"));
        yield return ("external extraction fingerprint covers native plugin inputs", () => MutationIsRejected("plugin"));
        yield return ("project fingerprint covers shader overlay inputs", ShaderMutationChangesProjectFingerprint);
        yield return ("managed fingerprint covers Unity inputs but ignores later project settings", ManagedFingerprintSelection);
        yield return ("workspace lock excludes concurrent conversion and can be reacquired", WorkspaceLock);
        yield return ("baseline records only null MonoScript slots by source object and asset", BaselineNullScripts);
        yield return ("build receipt tracks generated StreamingAssets bundle bytes", BuildReceiptTracksBundles);
        yield return ("failed bundle plan remains inspectable after candidate cleanup", FailedBundlePlanSurvivesCleanup);
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

    private static void ShaderMutationChangesProjectFingerprint()
    {
        using var fixture = new Fixture();
        fixture.CreateUnityInputs();
        var pipeline = fixture.Pipeline();
        var before = pipeline.FingerprintForTests("project");
        File.WriteAllText(Path.Combine(fixture.Shaders, "Assets/Includes/Lighting.cginc"), "changed-shader-include");
        var after = pipeline.FingerprintForTests("project");
        Check(before != after, "shader overlay changes were omitted from the project-stage fingerprint");
    }

    private static void ManagedFingerprintSelection()
    {
        using var fixture = new Fixture();
        fixture.CreateUnityInputs();
        var pipeline = fixture.Pipeline();
        var before = pipeline.FingerprintForTests("managed");
        File.WriteAllText(Path.Combine(fixture.Shaders, "Assets/Includes/Lighting.cginc"), "later-project-shader");
        var config = fixture.Config() with { BuildTarget = "StandaloneLinux64" };
        var afterProjectOptions = new Pipeline(fixture.Repository, config).FingerprintForTests("managed");
        Check(before == afterProjectOptions, "managed-stage fingerprint included shader or target settings that begin at project preparation");
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
        var streamingAssets = Path.Combine(fixture.rootPath, "candidate/Assets/StreamingAssets");
        Write(Path.Combine(player, "H3VR.app/Contents/MacOS/H3VR"), "player-binary");
        Write(Path.Combine(streamingAssets, "manifest.bundle"), "rebuilt-bundle");
        Write(Path.Combine(streamingAssets, "manifest.bundle.manifest"), "bundle-dependencies");
        var outputFiles = Pipeline.BuildOutputFiles(player, streamingAssets);
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
        public string Workspace => Path.Combine(root, "workspace");
        public string UnityData => Path.Combine(root, "UnityEditor", "Data");
        public string EngineAssemblies => Path.Combine(UnityData, "Managed", "UnityEngine");

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
            Write(Path.Combine(UnityData, "MonoBleedingEdge/lib/mono/4.7.1-api/mscorlib.dll"), "synthetic framework assembly");
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
