using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("H3VR.Converter.Tests")]

namespace H3VR.Converter;

public sealed record StepReceipt(string Step, string Fingerprint, DateTimeOffset Completed, Dictionary<string, string> Files);

public sealed class Pipeline(string repository, ConversionConfig config)
{
    public static readonly string[] Steps = ["extract", "managed", "project", "validate", "build"];
    private string Receipts => Path.Combine(config.Workspace, ".conversion");

    private string CommonFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        void FileInput(string path) { Add(Path.GetFullPath(path)); Add(SafeFiles.Hash(path)); }
        void Tree(string path, Func<string, bool>? include = null)
        {
            foreach (var file in SafeFiles.Files(path).Where(p => include?.Invoke(p) ?? true).Order(StringComparer.Ordinal)) FileInput(file);
        }
        Add("h3vr-converter-common-inputs-v2");
        Add(Path.GetFullPath(config.GameDirectory));
        Add(config.RecoveredProject is null ? "internal-recovery" : Path.GetFullPath(config.RecoveredProject));
        Add(ConversionConfig.TargetVersion);
        // Hash actual input bytes. File timestamps alone miss replacements with preserved dates.
        Tree(config.GameData);
        foreach (var file in Directory.GetFiles(config.GameDirectory).Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)) FileInput(file);
        foreach (var directory in new[] { "src", "tools", "editor", "runtime" })
            Tree(Path.Combine(repository, directory), p => !p.Split(Path.DirectorySeparatorChar).Any(x => x is "obj" or "bin") && Path.GetExtension(p) is ".cs" or ".csproj" or ".patch");
        foreach (var file in new[] { "Directory.Build.props", "global.json" }) FileInput(Path.Combine(repository, file));
        if (config.RecoveredProject is not null) Tree(config.RecoveredProject);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private string Fingerprint(string step, string common)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        void FileInput(string path) { Add(Path.GetFullPath(path)); Add(SafeFiles.Hash(path)); }
        void Tree(string path) { foreach (var file in SafeFiles.Files(path).Order(StringComparer.Ordinal)) FileInput(file); }
        Add("h3vr-converter-step-inputs-v2");
        Add(step);
        Add(common);

        if (step is "managed" or "project" or "validate" or "build") AddUnityInputs();
        if (step is "project" or "validate" or "build")
        {
            Add(JsonSerializer.Serialize(config with { Offline = false, ToolCacheDirectory = null }));
            if (config.ShaderSources is not null)
                foreach (var file in SafeFiles.Files(config.ShaderSources)
                    .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".shader" or ".cginc" or ".hlsl" or ".compute" or ".glslinc" or ".meta")
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
            Tree(Path.Combine(config.UnityData, "MonoBleedingEdge/lib/mono/4.7.1-api"));
        }
    }

    internal string FingerprintForTests(string step) => Fingerprint(step, CommonFingerprint());

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
        Directory.CreateDirectory(config.Workspace);
        var marker = Path.Combine(config.Workspace, ".h3vr-converter-workspace");
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(config.Workspace).Any()) throw new IOException("Workspace must initially be empty. Choose a fresh workspace; existing projects are never replaced.");
            File.WriteAllText(marker, "H3VR.Converter workspace schema 1\n");
        }
        Directory.CreateDirectory(Receipts);
        using var lease = new FileStream(Path.Combine(Receipts, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("Fingerprinting local inputs before conversion/resume…");
        var commonFingerprint = CommonFingerprint();
        foreach (var step in Steps)
        {
            cancellation.ThrowIfCancellationRequested();
            var fingerprint = Fingerprint(step, commonFingerprint);
            var receiptFile = Path.Combine(Receipts, step + ".json");
            if (File.Exists(receiptFile))
            {
                var receipt = JsonSerializer.Deserialize<StepReceipt>(File.ReadAllText(receiptFile), ConversionConfig.Json)
                    ?? throw new InvalidDataException("Empty step receipt: " + receiptFile);
                if (receipt.Step != step || receipt.Fingerprint != fingerprint || !ReceiptOutputsMatch(receipt))
                    throw new InvalidDataException($"{step} inputs or recorded outputs changed. Use a fresh workspace to preserve the previous conversion.");
                Console.WriteLine("Verified completed step: " + step);
            }
            else
            {
                Console.WriteLine("Starting " + step);
                var outputs = step switch
                {
                    "extract" => await Extract(cancellation),
                    "managed" => await Managed(cancellation),
                    "project" => Prepare(),
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

    private async Task<List<string>> Extract(CancellationToken cancellation)
    {
        if (config.RecoveredProject is null)
        {
            var recovery = Path.Combine(config.Workspace, "recovery");
            if (Directory.Exists(recovery)) throw new IOException("An incomplete recovery exists. Inspect it and choose a fresh workspace before rerunning extraction.");
            var candidate = Path.Combine(config.Workspace, "recovery-candidate-" + Guid.NewGuid().ToString("N"));
            try
            {
                await AssetRipperIntegration.ExportAsync(repository, config, candidate, Path.Combine(Receipts, "assetripper.log"), cancellation);
                if (!File.Exists(Path.Combine(candidate, "ExportedProject/ProjectSettings/ProjectVersion.txt")) ||
                    !Directory.Exists(Path.Combine(candidate, "ExportedProject/Assets")))
                    throw new InvalidDataException("AssetRipper did not produce an ExportedProject. Inspect .conversion/assetripper.log.");
                Directory.Move(candidate, recovery);
            }
            finally { if (Directory.Exists(candidate)) Directory.Delete(candidate, true); }
        }
        var version = Path.Combine(config.Export, "ProjectSettings/ProjectVersion.txt");
        if (!File.Exists(version) || !Directory.Exists(Path.Combine(config.Export, "Assets"))) throw new InvalidDataException("Recovery must contain ExportedProject/Assets and ProjectSettings/ProjectVersion.txt.");
        // External recovered input is already fully fingerprinted. Internally generated
        // recovery is immutable output and must be verified on every resumed run.
        return config.RecoveredProject is null ? SafeFiles.Files(config.Export).ToList() : [version];
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
            await Processes.Run("dotnet", ["run", "--project", Path.Combine(repository, "tools/ManagedRetarget"), "-c", "Release", "--", config.ManagedInput, retargeted, config.EngineAssemblies, Path.Combine(compat, "NativeH3VR.Compat.dll")], repository, cancellation: cancellation);
            await VerifyManaged(retargeted, cancellation);
            var adapter = Path.Combine(candidate, "adapter");
            await Processes.Run("dotnet", ["build", Path.Combine(repository, "runtime/OpenXR/NativeH3VR.OpenXR.csproj"), "-c", "Release", "-o", adapter, "-p:UnityEngineAssemblies=" + config.EngineAssemblies, "-p:GameManaged=" + retargeted], repository, cancellation: cancellation);
            var patched = Path.Combine(candidate, "patched");
            await Processes.Run("dotnet", ["run", "--project", Path.Combine(repository, "tools/OpenXRPatch"), "-c", "Release", "--", retargeted, Path.Combine(adapter, "NativeH3VR.OpenXR.dll"), patched, "--unity-engine", config.EngineAssemblies], repository, cancellation: cancellation);
            await VerifyManaged(patched, cancellation);
            Directory.Move(patched, config.ManagedOutput);
        }
        finally { Directory.Delete(candidate, true); }
        return SafeFiles.Files(config.ManagedOutput).ToList();
    }

    private Task VerifyManaged(string output, CancellationToken cancellation) => Processes.Run("dotnet", ["run", "--project", Path.Combine(repository, "tools/ManagedVerify"), "-c", "Release", "--", output, config.EngineAssemblies, Path.Combine(config.UnityData, "MonoBleedingEdge/lib/mono/4.7.1-api")], repository, cancellation: cancellation);

    private List<string> Prepare()
    {
        if (Directory.Exists(config.Project)) throw new IOException("Unrecorded project exists. Inspect it before using a fresh workspace.");
        var candidate = Path.Combine(config.Workspace, "project-candidate-" + Guid.NewGuid().ToString("N"));
        try
        {
            SafeFiles.CopyTree(config.Export, candidate);
            var report = new ProjectMigration(config, candidate).Run();
            LocalAssets.Stage(config, candidate);
            AssetBaseline.Capture(candidate);
            SourceStaging.Install(repository, candidate);
            if (config.IntegrationManifest is not null) Integration.Apply(config.IntegrationManifest, candidate, config.BuildTarget);
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
        var output = Path.Combine(config.Workspace, "player", config.BuildTarget == "StandaloneOSX" ? "H3VR.app" : config.BuildTarget == "StandaloneWindows64" ? "H3VR.exe" : "H3VR.x86_64");
        if (build && Directory.Exists(Path.GetDirectoryName(output)!)) throw new IOException("Player output exists. Inspect the previous build before using a fresh workspace.");
        var bundleOutput = Path.Combine(config.Workspace, "bundles-" + Guid.NewGuid().ToString("N"));
        // Cecil migration and verification replace Unity's heuristic assembly updater.
        // The latter cannot safely choose among several deprecated Unity 5 overloads.
        await Processes.Run(config.UnityEditor, ["-batchmode", "-disable-assembly-updater", "-projectPath", config.Project, "-buildTarget", config.BuildTarget, "-executeMethod", build ? "H3VRConverter.EditorConversion.Build" : "H3VRConverter.EditorConversion.Validate", "-logFile", logfile, "-h3vrReport", report, "-h3vrOutput", output, "-h3vrBundlePlan", Path.Combine(Receipts, "bundle-plan.json"), "-h3vrBundleOutput", bundleOutput], repository, cancellation: cancellation);
        if (!File.Exists(report)) throw new InvalidDataException("Unity did not produce the expected report. Inspect " + logfile);
        using var document = JsonDocument.Parse(File.ReadAllText(report));
        if (!document.RootElement.GetProperty("passed").GetBoolean()) throw new InvalidDataException("Unity validation failed: " + report);
        if (Regex.IsMatch(File.ReadAllText(logfile), @"(?im)^.*(?:error CS\d+|Shader error|Compute shader error|Build completed with a result of 'Failed'|Unable to update following assemblies)"))
            throw new InvalidDataException("Compiler failures were found in " + logfile);
        RefreshEditorSettingsReceipt();
        if (build)
        {
            if (!File.Exists(output) && !Directory.Exists(output)) throw new InvalidDataException("Unity reported success without creating the player.");
            if (Directory.Exists(bundleOutput)) Directory.Delete(bundleOutput, true);
            return BuildOutputFiles(Path.Combine(config.Workspace, "player"), Path.Combine(config.Project, "Assets/StreamingAssets"))
                .Concat([report, logfile]).ToList();
        }
        return [report, logfile];
    }

    /// <summary>Inventory the player and the rebuilt bundle artifacts that Unity emits into the project.</summary>
    internal static List<string> BuildOutputFiles(string playerDirectory, string streamingAssetsDirectory)
    {
        var files = SafeFiles.Files(playerDirectory).ToList();
        if (Directory.Exists(streamingAssetsDirectory)) files.AddRange(SafeFiles.Files(streamingAssetsDirectory));
        return files;
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

    private void RefreshEditorSettingsReceipt()
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
        SafeFiles.AtomicJson(path, receipt with { Files = files });
    }
}
