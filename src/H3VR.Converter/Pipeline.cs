using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("H3VR.Converter.Tests")]

namespace H3VR.Converter;

public sealed record StepReceipt(string Step, string Fingerprint, DateTimeOffset Completed, Dictionary<string, string> Files);

public sealed partial class Pipeline(string repository, ConversionConfig config)
{
    public static readonly string[] Steps = ["extract", "managed", "project", "validate", "build"];
    private string Receipts => Path.Combine(config.Workspace, ".conversion");

    private string GameFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        void FileInput(string path) { Add(Path.GetFullPath(path)); Add(SafeFiles.Hash(path)); }
        void Tree(string path)
        {
            foreach (var file in SafeFiles.Files(path).Order(StringComparer.Ordinal)) FileInput(file);
        }
        Add("h3vr-converter-game-inputs-v4");
        Add(Path.GetFullPath(config.GameDirectory));
        Add(config.RecoveredProject is null ? "internal-recovery" : Path.GetFullPath(config.RecoveredProject));
        // Hash actual input bytes. File timestamps alone miss replacements with preserved dates.
        Tree(config.GameData);
        foreach (var file in Directory.GetFiles(config.GameDirectory).Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)) FileInput(file);
        if (config.RecoveredProject is not null) Tree(config.RecoveredProject);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private string Fingerprint(string step, string common)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        void FileInput(string path) { Add(Path.GetFullPath(path)); Add(SafeFiles.Hash(path)); }
        void Tree(string path) { foreach (var file in SafeFiles.Files(path).Order(StringComparer.Ordinal)) FileInput(file); }
        Add("h3vr-converter-step-inputs-v3");
        Add(step);
        Add(common);

        if (step == "extract")
        {
            AddExtractionSources(repository, Add);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        foreach (var directory in new[] { "src", "tools", "editor", "runtime" })
            foreach (var file in SafeFiles.Files(Path.Combine(repository, directory))
                .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")
                    && Path.GetExtension(path) is ".cs" or ".csproj").Order(StringComparer.Ordinal))
                FileInput(file);
        foreach (var file in new[] { "Directory.Build.props", "global.json" })
            FileInput(Path.Combine(repository, file));

        AddUnityInputs();
        Add(config.UnityVersion);
        Add(config.BuildTarget);
        if (step == "build" && config.BuildTarget == "StandaloneOSX")
        {
            PlayerEnlighten.RequireClassDatabase(config.CacheDirectory(repository));
            Add(PlayerEnlighten.ClassDatabaseSha256);
        }
        if (step is "project" or "validate" or "build")
        {
            Add(JsonSerializer.Serialize(config with
            {
                Offline = false,
                ToolCacheDirectory = null,
                CompactPlayerData = step == "build" && config.CompactPlayerData
            }));
            if (config.ShaderSources is not null)
                foreach (var file in SafeFiles.Files(config.ShaderSources)
                    .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".shader" or ".cginc" or ".hlsl" or ".compute" or ".glslinc" or ".meta" ||
                                Path.GetFileName(p) is ComputeShaderOverlay.ReceiptName or GraphicsShaderOverlay.ReceiptName
                                    or SourceStaging.ShaderVariantCatalogName)
                    .Order(StringComparer.Ordinal)) FileInput(file);
        }
        if (config.IntegrationManifest is not null)
        {
            if (step is not ("project" or "validate" or "build")) return Convert.ToHexStringLower(hash.GetHashAndReset());
            FileInput(config.IntegrationManifest);
            using var manifest = JsonDocument.Parse(File.ReadAllText(config.IntegrationManifest));
            var files = manifest.RootElement.EnumerateObject().Single(p => p.Name.Equals("files", StringComparison.OrdinalIgnoreCase)).Value;
            foreach (var file in files.EnumerateArray())
            {
                var source = file.EnumerateObject().Single(p => p.Name.Equals("source", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
                FileInput(SafeFiles.Inside(Path.GetDirectoryName(config.IntegrationManifest)!, source));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void AddUnityInputs()
        {
            Add(Path.GetFullPath(config.UnityEditor));
            FileInput(config.UnityEditor);
            Tree(config.EngineAssemblies);
            Tree(config.MonoReferenceAssemblies);
            if (config.BuildTarget == "StandaloneOSX") Tree(config.MacPlayerAssemblies);
        }
    }

    internal string FingerprintForTests(string step) => Fingerprint(step, GameFingerprint());

    public static void Status(ConversionConfig config)
    {
        foreach (var step in Steps)
        {
            var file = Path.Combine(config.Workspace, ".conversion", step + ".json");
            Console.WriteLine($"{step,-10} {(File.Exists(file) ? "receipt present (convert verifies before reuse)" : "not completed")}");
        }
    }

    public async Task Run(string through, CancellationToken cancellation)
    {
        if (!Steps.Contains(through)) throw new ArgumentException("Unknown step: " + through);
        config.RequireColdRecovery(through);
        var marker = Path.Combine(config.Workspace, ".h3vr-converter-workspace");
        var lockPath = Path.Combine(Receipts, "lock");
        foreach (var path in new[] { config.Workspace, marker, lockPath, config.ManagedOutput, config.Project,
            Path.Combine(config.Workspace, "recovery"), Path.Combine(config.Workspace, "player") })
            SafeFiles.RequirePrivateOutput(repository, path);
        Directory.CreateDirectory(config.Workspace);
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(config.Workspace).Any()) throw new IOException("Workspace must initially be empty. Choose a fresh workspace; existing projects are never replaced.");
            File.WriteAllText(marker, "H3VR.Converter workspace schema 1\n");
        }
        Directory.CreateDirectory(Receipts);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var sourceLease = OpenExtractionLease();
        Console.WriteLine("Fingerprinting local inputs before conversion/resume…");
        var gameFingerprint = GameFingerprint();
        var sharedExtraction = sourceLease is null ? null : VerifySharedExtraction(Fingerprint("extract", gameFingerprint), cancellation);
        foreach (var step in Steps)
        {
            cancellation.ThrowIfCancellationRequested();
            if (step == "project" && config.DxbcCompiler is not null)
            {
                var overlay = await LocalShaderPreparation.PrepareAsync(repository, config, cancellation);
                config = config with { ShaderSources = overlay };
            }
            var fingerprint = Fingerprint(step, gameFingerprint);
            var receiptFile = Path.Combine(Receipts, step + ".json");
            if (File.Exists(receiptFile))
            {
                var receipt = JsonSerializer.Deserialize<StepReceipt>(File.ReadAllText(receiptFile), ConversionConfig.Json)
                    ?? throw new InvalidDataException("Empty step receipt: " + receiptFile);
                bool outputsMatch = step == "extract" && sharedExtraction is not null
                    ? receipt.Files.Count == sharedExtraction.Files.Count &&
                      receipt.Files.All(file => sharedExtraction.Files.TryGetValue(file.Key, out var sha256) && file.Value == sha256)
                    : ReceiptOutputsMatch(receipt);
                if (receipt.Step != step || receipt.Fingerprint != fingerprint || !outputsMatch)
                    throw new InvalidDataException($"{step} inputs or recorded outputs changed. Use a fresh workspace to preserve the previous conversion.");
                Console.WriteLine("Verified completed step: " + step);
            }
            else if (step == "extract" && sharedExtraction is not null)
            {
                SafeFiles.AtomicJson(receiptFile, sharedExtraction);
                Console.WriteLine("Reused Steam extraction from " + config.ExtractionWorkspace);
            }
            else
            {
                Console.WriteLine("Starting " + step);
                var outputs = step switch
                {
                    "extract" => await Extract(cancellation),
                    "managed" => await Managed(cancellation),
                    "project" => await Prepare(cancellation),
                    "validate" => await Editor(false, cancellation),
                    "build" => await Editor(true, cancellation),
                    _ => throw new InvalidOperationException()
                };
                SafeFiles.AtomicJson(receiptFile, new StepReceipt(step, fingerprint, DateTimeOffset.UtcNow,
                    outputs.Distinct().ToDictionary(p => Path.GetFullPath(p), SafeFiles.Hash)));
                Console.WriteLine("Completed " + step);
            }
            if (step == through) break;
        }
    }

    private FileStream? OpenExtractionLease()
    {
        if (config.ExtractionWorkspace is not { } source) return null;
        SafeFiles.RequireSeparate(source, config.Workspace);
        string receipts = Path.Combine(source, ".conversion");
        string lockPath = Path.Combine(receipts, "lock");
        if (new DirectoryInfo(receipts).LinkTarget is not null || new FileInfo(lockPath).LinkTarget is not null)
            throw new InvalidDataException("The shared extraction's conversion state must be in the source workspace.");
        return File.Open(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private StepReceipt VerifySharedExtraction(string fingerprint, CancellationToken cancellation)
    {
        string source = config.ExtractionWorkspace!;
        if (!File.Exists(Path.Combine(source, ".h3vr-converter-workspace")))
            throw new InvalidDataException("The extraction source must be a converter workspace: " + source);
        string path = Path.Combine(source, ".conversion", "extract.json");
        var receipt = JsonSerializer.Deserialize<StepReceipt>(File.ReadAllText(path), ConversionConfig.Json);
        if (receipt is null || receipt.Step != "extract" || receipt.Fingerprint != fingerprint)
            throw new InvalidDataException("The shared extraction does not match the current Steam files and exporter: " + path);
        string recovery = Path.Combine(source, "recovery");
        if (new DirectoryInfo(recovery).LinkTarget is not null)
            throw new InvalidDataException("The shared extraction must be in the source workspace.");
        var files = ColdRecoveryOutputFiles(recovery);
        if (receipt.Files.Count != files.Count)
            throw new InvalidDataException("The shared extraction file inventory differs from its receipt: " + path);
        foreach (string file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!receipt.Files.TryGetValue(file, out var expected) || expected != SafeFiles.Hash(file))
                throw new InvalidDataException("The shared extraction does not match its receipt: " + file);
        }
        return receipt;
    }

    private async Task<List<string>> Managed(CancellationToken cancellation)
    {
        if (Directory.Exists(config.ManagedOutput)) throw new IOException("Unrecorded managed output exists. Inspect it before using a fresh workspace.");
        var candidate = Path.Combine(config.Workspace, "managed-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(candidate);
        try
        {
            var compat = Path.Combine(candidate, "compat");
            await Processes.Run("dotnet", ["build", Path.Combine(repository, "runtime/Compatibility/Compatibility.csproj"), "-c", "Release", "-o", compat, "-p:UnityEngineAssemblies=" + config.EngineAssemblies], repository, cancellation: cancellation);
            var retargeted = Path.Combine(candidate, "retargeted");
            var retargetArgs = new List<string> { "run", "--project", Path.Combine(repository, "tools/ManagedRetarget"),
                "-c", "Release", "--", config.ManagedInput, retargeted, config.EngineAssemblies,
                Path.Combine(compat, "NativeH3VR.Compat.dll") };
            if (config.BuildTarget == "StandaloneOSX") retargetArgs.Add("--metal-buffer-bounds");
            await Processes.Run("dotnet", retargetArgs, repository, cancellation: cancellation);
            if (config.BuildTarget == "StandaloneOSX") await ApplyMacPlatform(retargeted, candidate, cancellation);
            await VerifyManaged(retargeted, cancellation);
            var adapter = Path.Combine(candidate, "adapter");
            var adapterArgs = new List<string> { "build", Path.Combine(repository, "runtime/OpenXR/NativeH3VR.OpenXR.csproj"), "-c", "Release", "-o", adapter, "-p:UnityEngineAssemblies=" + config.EngineAssemblies, "-p:GameManaged=" + retargeted };
            if (config.BuildTarget == "StandaloneOSX") adapterArgs.Add("-p:NativeH3VRMetal=true");
            await Processes.Run("dotnet", adapterArgs, repository, cancellation: cancellation);
            var patched = Path.Combine(candidate, "patched");
            var patchArgs = new List<string> { "run", "--project", Path.Combine(repository, "tools/OpenXRPatch"), "-c", "Release", "--",
                retargeted, Path.Combine(adapter, "NativeH3VR.OpenXR.dll"), patched, "--unity-engine", config.EngineAssemblies };
            if (config.BuildTarget == "StandaloneOSX" && LegacyHdrMaterials.IsLinearProject(config.Export))
                patchArgs.Add("--legacy-stock-rim");
            await Processes.Run("dotnet", patchArgs, repository, cancellation: cancellation);
            await VerifyManaged(patched, cancellation);
            Directory.Move(patched, config.ManagedOutput);
        }
        finally { Directory.Delete(candidate, true); }
        return SafeFiles.Files(config.ManagedOutput).ToList();
    }

    private Task VerifyManaged(string output, CancellationToken cancellation) => Processes.Run("dotnet", ["run", "--project", Path.Combine(repository, "tools/ManagedVerify"), "-c", "Release", "--", output, config.EngineAssemblies, config.MonoReferenceAssemblies], repository, cancellation: cancellation);

    private async Task ApplyMacPlatform(string managed, string candidate, CancellationToken cancellation)
    {
        var scratch = Path.Combine(candidate, "platform"); Directory.CreateDirectory(scratch);
        var patches = Path.Combine(repository, "tools/PlatformPatch");
        var first = Path.Combine(managed, "NativeH3VR.FirstPass.dll");
        var steam = Path.Combine(scratch, "steam.dll"); var fmod = Path.Combine(scratch, "NativeH3VR.FirstPass.dll");
        await Processes.Run("dotnet", ["run", "--project", Path.Combine(patches, "Steam"), "-c", "Release", "--", first, steam], repository, cancellation: cancellation);
        await Processes.Run("dotnet", ["run", "--project", Path.Combine(patches, "Fmod"), "-c", "Release", "--", steam, fmod], repository, cancellation: cancellation);
        File.Move(fmod, first, true);
        var game = Path.Combine(managed, "NativeH3VR.Game.dll");
        var unity = config.MacPlayerAssemblies;
        if (!Directory.Exists(unity)) throw new InvalidDataException("Install Unity's macOS standalone Mono player module: " + unity);
        var prepared = Path.Combine(scratch, "prepared/NativeH3VR.Game.dll");
        var backendDir = Path.Combine(scratch, "backend"); var backend = Path.Combine(backendDir, "NativeH3VR.Platform.dll");
        var video = Path.Combine(patches, "Video");
        await Processes.Run("dotnet", ["run", "--project", video, "-c", "Release", "--", "prepare", game, prepared, "-", unity, managed], repository, cancellation: cancellation);
        await Processes.Run("dotnet", ["build", Path.Combine(repository, "runtime/Platform/Video/NativeH3VR.Platform.csproj"), "-c", "Release", "-o", backendDir, "-p:UnityManaged=" + unity, "-p:GameManaged=" + prepared], repository, cancellation: cancellation);
        var intermediate = Path.Combine(scratch, "video-intermediate.dll"); var final = Path.Combine(scratch, "NativeH3VR.Game.dll");
        await Processes.Run("dotnet", ["run", "--project", video, "-c", "Release", "--", "finish", prepared, intermediate, backend, unity, managed], repository, cancellation: cancellation);
        await Processes.Run("dotnet", ["run", "--project", video, "-c", "Release", "--", "normalize-self", intermediate, final, "-", unity, managed], repository, cancellation: cancellation);
        File.Move(final, game, true); File.Copy(backend, Path.Combine(managed, "NativeH3VR.Platform.dll"));
    }

    private async Task<List<string>> Prepare(CancellationToken cancellation)
    {
        if (Directory.Exists(config.Project)) throw new IOException("Unrecorded project exists. Inspect it before using a fresh workspace.");
        var candidate = Path.Combine(config.Workspace, "project-candidate-" + Guid.NewGuid().ToString("N"));
        try
        {
            SafeFiles.CopyTree(config.Export, candidate);
            var report = new ProjectMigration(config, candidate).Run();
            LocalAssets.Stage(config, candidate);
            var convexJournal = await LegacyConvexAssetMigration.StageAsync(repository, config, candidate, cancellation);
            AssetBaseline.Capture(candidate);
            SourceStaging.Install(repository, candidate, config.BuildTarget, config.BuildProbe);
            if (config.IntegrationManifest is not null) Integration.Apply(config.IntegrationManifest, candidate, config.BuildTarget, config.UnityVersion);
            var temporaryPlan = Path.Combine(candidate, "bundle-plan.json");
            var bundleReport = BundlePlan.Create(Path.Combine(config.GameData, "StreamingAssets"), candidate, temporaryPlan);
            if (bundleReport.Issues != 0)
            {
                var diagnosticPath = PreserveFailedBundlePlan(temporaryPlan, Receipts);
                throw new InvalidDataException($"Bundle planning has {bundleReport.Issues} unresolved addresses. Diagnostic: {diagnosticPath}");
            }
            var plan = JsonNode.Parse(File.ReadAllText(temporaryPlan))!;
            plan["project"] = Path.GetFullPath(config.Project);
            File.Delete(temporaryPlan);
            Directory.Move(candidate, config.Project);
            var reportPath = Path.Combine(Receipts, "project-migration.json");
            SafeFiles.AtomicJson(reportPath, report with { Project = config.Project });
            var planPath = Path.Combine(Receipts, "bundle-plan.json"); SafeFiles.AtomicJson(planPath, plan);
            // Unity creates/updates .meta files and XR settings during import. Authored
            // game assets, copied binaries and tooling must remain identical on resume.
            return SafeFiles.Files(Path.Combine(config.Project, "Assets"))
                .Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .Concat(SafeFiles.Files(Path.Combine(config.Project, "ProjectSettings")))
                .Concat(SafeFiles.Files(Path.Combine(config.Project, "Packages")))
                .Concat(report.LegacyHdrMaterials is null ? [] : [SafeFiles.Inside(config.Project, report.LegacyHdrMaterials)])
                .Concat(convexJournal is null ? [] : [convexJournal])
                .Concat([reportPath, planPath]).ToList();
        }
        finally { if (Directory.Exists(candidate)) Directory.Delete(candidate, true); }
    }

    private async Task<List<string>> Editor(bool build, CancellationToken cancellation)
    {
        var name = build ? "build" : "validation";
        var run = name + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var report = Path.Combine(Receipts, run + ".json");
        var logfile = Path.Combine(Receipts, run + ".log");
        var executableName = config.BuildTarget switch
        {
            "StandaloneOSX" => "NativeH3VR.app",
            "StandaloneWindows64" => "H3VR.exe",
            _ => config.BuildProbe ? "H3VR.BuildProbe.x86_64" : "H3VR.x86_64"
        };
        var output = Path.Combine(config.Workspace, "player", executableName);
        if (build && Directory.Exists(Path.GetDirectoryName(output)!)) throw new IOException("Player output exists. Inspect the previous build before using a fresh workspace.");
        var bundleWork = Path.Combine(config.Workspace, "bundles-" + Guid.NewGuid().ToString("N"));
        var bundleOutput = Path.Combine(bundleWork, "StreamingAssets");
        // Cecil migration and verification replace Unity's heuristic assembly updater.
        // The latter cannot safely choose among several deprecated Unity 5 overloads.
        string[] editorArguments = ["-batchmode", "-disable-assembly-updater", "-projectPath", config.Project, "-buildTarget", config.BuildTarget, "-executeMethod", build ? "H3VRConverter.EditorConversion.Build" : "H3VRConverter.EditorConversion.Validate", "-logFile", logfile, "-nativeH3vrUnityVersion", config.UnityVersion, "-h3vrReport", report, "-h3vrOutput", output, "-h3vrBundlePlan", Path.Combine(Receipts, "bundle-plan.json"), "-h3vrBundleOutput", bundleOutput];
        if (config.BuildTarget == "StandaloneOSX") editorArguments = [.. editorArguments, "-force-metal"];
        if (config.BuildProbe) editorArguments = [.. editorArguments, "-h3vrBuildProbe"];
        await Processes.Run(config.UnityEditor, editorArguments, repository, cancellation: cancellation);
        if (!File.Exists(report)) throw new InvalidDataException("Unity did not produce the expected report. Inspect " + logfile);
        using var document = JsonDocument.Parse(File.ReadAllText(report));
        if (!document.RootElement.GetProperty("passed").GetBoolean()) throw new InvalidDataException("Unity validation failed: " + report);
        if (Regex.IsMatch(File.ReadAllText(logfile), @"(?im)^.*(?:error CS\d+|Shader error|Compute shader error|Build completed with a result of 'Failed'|Unable to update following assemblies)"))
            throw new InvalidDataException("Compiler failures were found in " + logfile);
        var shaderUpgrades = Path.Combine(Receipts, run + "-shader-upgrades.json");
        RefreshEditorSettingsReceipt(logfile, shaderUpgrades);
        if (build)
        {
            var evidence = new List<string> { report, logfile, shaderUpgrades };
            if (!File.Exists(output) && !Directory.Exists(output)) throw new InvalidDataException("Unity reported success without creating the player.");
            bool mac = config.BuildTarget == "StandaloneOSX";
            if (mac)
            {
                if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The macOS player must be signed and verified on macOS.");
                Integration.PlaceMacPlayerLibraries(output);
                var playerReceipt = PlayerReceipt.Write(output, config.GameDirectory, Path.Combine(Receipts, "extract.json"), config.UnityVersion);
                using var playerMetadata = JsonDocument.Parse(File.ReadAllText(playerReceipt));
                var executable = playerMetadata.RootElement.GetProperty("playerExecutable").GetString()!;
                await Processes.Run("lipo", ["-verify_arch", "arm64", Path.Combine(output, "Contents/MacOS", executable)], repository, cancellation: cancellation);
                var architectureLog = Path.Combine(Receipts, "player-architectures.txt");
                await Processes.Run("lipo", ["-archs", Path.Combine(output, "Contents/MacOS", executable)], repository, architectureLog, cancellation);
                if (File.ReadAllText(architectureLog).Trim() != "arm64")
                    throw new InvalidDataException("Player executable is not a thin Apple Silicon build; inspect " + architectureLog);
                var libraries = Directory.GetFiles(Path.Combine(output, "Contents"), "*.dylib", SearchOption.AllDirectories);
                var libraryNames = libraries.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
                foreach (var expected in Integration.RequiredMetalFiles().Where(path => path.EndsWith(".dylib", StringComparison.Ordinal)))
                    if (!libraryNames.Contains(Path.GetFileName(expected)))
                        throw new InvalidDataException("The built player is missing its native plug-in " + Path.GetFileName(expected));
                foreach (var library in libraries)
                    await Processes.Run("lipo", ["-verify_arch", "arm64", library], repository, cancellation: cancellation);
                evidence.AddRange(RestorePlayerEnlighten(output, run, bundleWork));
                string reflectionReceipt = Path.Combine(Receipts, run + "-reflection-probes.json");
                var reflections = PlayerReflectionProbes.Restore(config.GameData, Path.Combine(output, "Contents", "Resources", "Data"),
                    PlayerEnlighten.RequireClassDatabase(config.CacheDirectory(repository)),
                    Path.Combine(bundleWork, "reflection-probes"), reflectionReceipt, config.UnityVersion);
                PlayerReceipt.BindReflectionProbes(playerReceipt, reflectionReceipt);
                Console.WriteLine($"Restored {reflections.Scenes.Sum(scene => scene.Probes.Length)} Steam baked reflection probes across {reflections.Scenes.Length} scenes.");
                evidence.Add(reflectionReceipt);
                evidence.Add(architectureLog);
            }
            if (config.CompactPlayerData)
            {
                string data = mac ? Path.Combine(output, "Contents", "Resources", "Data")
                    : Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_Data");
                string archiveReceipt = Path.Combine(Receipts, run + "-archive.json");
                await Processes.Run("dotnet", ["run", "--project", Path.Combine(repository, "tools", "PlayerDataArchive"),
                    "-c", "Release", "--", data, Path.Combine(bundleWork, "archive"), archiveReceipt],
                    repository, cancellation: cancellation);
                evidence.Add(archiveReceipt);
            }
            if (mac)
            {
                await Processes.Run("codesign", ["--force", "--deep", "--sign", "-", output], repository, cancellation: cancellation);
                await Processes.Run("codesign", ["--verify", "--deep", "--strict", output], repository, cancellation: cancellation);
            }
            if (Directory.Exists(bundleWork)) Directory.Delete(bundleWork, true);
            return SafeFiles.Files(Path.Combine(config.Workspace, "player"))
                .Concat(evidence).ToList();
        }
        return [report, logfile, shaderUpgrades];
    }

    private string[] RestorePlayerEnlighten(string app, string run, string scratch)
    {
        var playerData = Path.Combine(app, "Contents", "Resources", "Data");
        var database = PlayerEnlighten.RequireClassDatabase(config.CacheDirectory(repository));
        var stage = Path.Combine(scratch, "enlighten-stage");
        var data = PlayerEnlighten.Stage(config.GameData, playerData, database, PlayerEnlighten.ClassDatabaseSha256, stage, config.UnityVersion);
        var stageReceipt = Path.Combine(Receipts, run + "-enlighten-stage.json");
        File.Copy(Path.Combine(stage, "enlighten-stage.json"), stageReceipt);
        var installed = PlayerEnlighten.Install(stage, playerData, SafeFiles.Hash(stageReceipt), Path.Combine(scratch, "enlighten-backups"));
        var installReceipt = Path.Combine(Receipts, run + "-enlighten-install.json");
        File.Copy(Path.Combine(installed.EvidenceDirectory, "enlighten-install.json"), installReceipt);
        Console.WriteLine($"Restored {data.Scenes.Sum(scene => scene.ProbeSets)} Steam Enlighten probe sets across {data.Scenes.Length} scenes.");
        return [stageReceipt, installReceipt];
    }

    /// <summary>Shared receipt check used by resume and the focused generated-output tests.</summary>
    internal static bool ReceiptOutputsMatch(StepReceipt receipt) =>
        receipt.Files.All(f => File.Exists(f.Key) && SafeFiles.Hash(f.Key) == f.Value);

    internal static string PreserveFailedBundlePlan(string temporaryPlan, string receiptsDirectory)
    {
        var diagnosticPath = Path.Combine(receiptsDirectory, "bundle-plan.failed.json");
        Directory.CreateDirectory(receiptsDirectory);
        if (!File.Exists(diagnosticPath)) File.Copy(temporaryPlan, diagnosticPath);
        return diagnosticPath;
    }

    private void RefreshEditorSettingsReceipt(string logfile, string shaderUpgrades)
    {
        // Only an accepted Unity run may refresh settings that Unity serializes on
        // import. Unrecorded external changes are checked before launching the editor.
        var path = Path.Combine(Receipts, "project.json");
        var receipt = JsonSerializer.Deserialize<StepReceipt>(File.ReadAllText(path), ConversionConfig.Json)!;
        var files = new Dictionary<string, string>(receipt.Files);
        var roots = new[] { "ProjectSettings", "Packages", "Assets/XR" }.Select(p => Path.Combine(config.Project, p)).ToArray();
        foreach (var key in files.Keys.Where(p => roots.Any(r => SafeFiles.Contains(r, p))).ToArray()) files.Remove(key);
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var file in SafeFiles.Files(root)) files[file] = SafeFiles.Hash(file);
        var upgrades = ImportShaderUpgrades(config.Project, files, File.ReadLines(logfile));
        SafeFiles.AtomicJson(shaderUpgrades, upgrades);
        SafeFiles.AtomicJson(path, receipt with { Files = files });
    }

    internal sealed record ShaderUpgrade(string Path, string Before, string After);

    internal static IReadOnlyList<ShaderUpgrade> ImportShaderUpgrades(
        string project, Dictionary<string, string> files, IEnumerable<string> log)
    {
        const string shaderPrefix = "Upgraded shader: ";
        const string includePrefix = "Upgraded shader include: ";
        var result = new List<ShaderUpgrade>();
        foreach (var line in log.Where(line => line.StartsWith(shaderPrefix + "Assets/", StringComparison.Ordinal)
            || line.StartsWith(includePrefix + "Assets/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            var include = line.StartsWith(includePrefix, StringComparison.Ordinal);
            var relative = line[(include ? includePrefix.Length : shaderPrefix.Length)..].TrimEnd();
            var path = SafeFiles.Inside(project, relative);
            var recognized = include ? GraphicsShaderOverlay.IsInclude(relative) : relative.EndsWith(".shader", StringComparison.OrdinalIgnoreCase);
            if (!recognized || !SafeFiles.Contains(Path.Combine(project, "Assets"), path) ||
                !files.TryGetValue(path, out var before) || !File.Exists(path) || new FileInfo(path).LinkTarget is not null)
                continue;
            var after = SafeFiles.Hash(path);
            if (before == after) continue;
            files[path] = after;
            result.Add(new ShaderUpgrade(relative, before, after));
        }
        return result;
    }
}
