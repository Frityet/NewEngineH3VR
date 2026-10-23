using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

public static class NativeBundleBuild {
    [Serializable]
    public class Asset {
        public string address;
        public string asset;
    }
    [Serializable]
    public class Bundle {
        public string name;
        public string[] dependencies;
        public Asset[] assets;
    }
    [Serializable]
    public class Plan {
        public string project;
        public Bundle[] bundles;
        public string[] issues;
    }
    [Serializable]
    public sealed class BundleInventory {
        public string name;
        public string[] addresses, dependencies;
    }
    [Serializable]
    public sealed class Fingerprint {
        public string path, sha256;
        public long bytes;
    }
    [Serializable]
    public sealed class Dependency {
        public string path, hash;
    }
    [Serializable]
    public sealed class Proof {
        public int schemaVersion = 2;
        public bool verified;
        public string mode, platform = "StandaloneOSX", graphics = "Metal", unityVersion,
                            planSha256;
        public int bundles, addresses, serializedReferences;
        public Fingerprint[] managedInputs, sourceFiles, outputs;
        public Fingerprint compilerReceipt, compilerLog;
        public Dependency[] sourceDependencies;
        public BundleInventory[] bundleInventory;
        public string[] limits = {
            "Source-authored null reference fields are allowed; non-null source references must " +
                "survive with type/name and prefab-local identity.",
            "Same-name/type external objects are not distinguished by bundle-local file IDs; " +
                "GPU/audio payload parity has separate audits.",
            "Shader visual correctness and runtime Awake/Start behavior require player tests; no " +
                "prefab is instantiated here."
        };
    }
    [Serializable]
    public sealed class CompilerPatterns {
        public int schemaVersion;
        public string[] patterns;
    }
    [Serializable]
    public sealed class CompilerReceipt {
        public int schemaVersion = 1;
        public bool clean;
        public string runId, planSha256, unityVersion, compilerPolicySha256, buildLogPath;
        public Fingerprint compilerLog;
        public Fingerprint[] outputs, managedInputs, sourceFiles;
    }
    static string PatternPath {
        get {
            return Path.GetFullPath(
                Path.Combine(Application.dataPath, "../../script/unity_compiler_errors.json"));
        }
    }
    static string ReadCleanLog(string requiredMarker = null, string pathOverride = null) {
        var path = pathOverride ?? Application.consoleLogPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException("Actual Unity compiler log is unavailable");
        string content;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                                         FileShare.ReadWrite)) using (var reader =
                                                                          new StreamReader(file))
            content = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(content) ||
            (requiredMarker != null && !content.Contains(requiredMarker)))
            throw new InvalidOperationException(
                "Unity log is empty or does not contain this build run marker");
        var rules = JsonUtility.FromJson<CompilerPatterns>(File.ReadAllText(PatternPath));
        if (rules == null || rules.schemaVersion != 1 || rules.patterns == null ||
            rules.patterns.Length == 0)
            throw new InvalidOperationException("Compiler diagnostic rules missing");
        foreach (var expression in rules.patterns) {
            var match = System.Text.RegularExpressions.Regex.Match(content, expression);
            if (match.Success)
                throw new InvalidOperationException("Unity compiler/error log rejected: " +
                                                    match.Value);
        }
        return content;
    }
    static Fingerprint[] OutputFingerprints(Plan plan, string destination) {
        return plan.bundles.Select(b => b.name)
            .Concat(new[] { new DirectoryInfo(destination).Name })
            .SelectMany(n => new[] { n, n + ".manifest" })
            .Select(n => {
                var item = FingerprintFile(OutputPath(destination, n));
                item.path = n;
                return item;
            })
            .ToArray();
    }
    static bool SameFiles(Fingerprint[] a, Fingerprint[] b) {
        return a != null && b != null &&
               JsonUtility.ToJson(new Fingerprints { files = a }) ==
                   JsonUtility.ToJson(new Fingerprints { files = b });
    }
    static CompilerReceipt ValidateCompilerReceipt(string destination, Proof proof, Plan plan) {
        var receipt = JsonUtility.FromJson<CompilerReceipt>(
            File.ReadAllText(Path.Combine(destination, "native-bundle-compiler-receipt.json")));
        if (receipt == null || receipt.schemaVersion != 1 || !receipt.clean ||
            string.IsNullOrEmpty(receipt.runId) || receipt.planSha256 != proof.planSha256 ||
            receipt.unityVersion != proof.unityVersion ||
            receipt.compilerPolicySha256 != HashFile(PatternPath) ||
            !SameFiles(receipt.managedInputs, proof.managedInputs) ||
            !SameFiles(receipt.sourceFiles, proof.sourceFiles) ||
            !SameFiles(receipt.outputs, OutputFingerprints(plan, destination)))
            throw new InvalidOperationException(
                "Existing bundles lack matching clean compiler evidence; rebuild required");
        var log = OutputPath(destination, receipt.compilerLog.path);
        if (new FileInfo(log).Length != receipt.compilerLog.bytes ||
            HashFile(log) != receipt.compilerLog.sha256)
            throw new InvalidOperationException("Compiler log snapshot changed");
        var content = File.ReadAllText(log);
        if (string.IsNullOrEmpty(receipt.buildLogPath) ||
            !Path.IsPathRooted(receipt.buildLogPath) ||
            !ReadCleanLog("NATIVEH3VR_COMPILER_BUILD_END " + receipt.runId, receipt.buildLogPath)
                 .StartsWith(content, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Originating build log changed or lacks its compiler snapshot prefix");
        if (!content.Contains("NATIVEH3VR_COMPILER_BUILD_END " + receipt.runId))
            throw new InvalidOperationException(
                "Compiler log lacks matching completed build marker");
        var rules = JsonUtility.FromJson<CompilerPatterns>(File.ReadAllText(PatternPath));
        foreach (var expression in rules.patterns)
            if (System.Text.RegularExpressions.Regex.IsMatch(content, expression))
                throw new InvalidOperationException(
                    "Existing build compiler snapshot contains errors");
        return receipt;
    }
    static string HashFile(string path) {
        using (var input = File.OpenRead(path)) using (
            var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter
            .ToString(sha.ComputeHash(input))
            .Replace("-", "")
            .ToLowerInvariant();
    }
    static Fingerprint FingerprintFile(string path) {
        return new Fingerprint { path = path, bytes = new FileInfo(path).Length,
                                 sha256 = HashFile(path) };
    }
    static Fingerprint[] ManagedInputs() {
        var paths = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var importer in PluginImporter.GetAllImporters().Where(
                     i => !i.isNativePlugin &&
                          i.assetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                          NativeAssetIntegrity.IsPluginCompatibleWithPlatform(
                              i, BuildTarget.StandaloneOSX))) {
            paths.Add(importer.assetPath);
            if (File.Exists(importer.assetPath + ".meta"))
                paths.Add(importer.assetPath + ".meta");
        }
        foreach (var assembly in UnityEditor.Compilation.CompilationPipeline.GetAssemblies(
                     UnityEditor.Compilation.AssembliesType.Player))
            foreach (var source in assembly.sourceFiles) {
                paths.Add(source);
                if (File.Exists(source + ".meta"))
                    paths.Add(source + ".meta");
            }
        // Include inactive/editor managed inputs too: a changed importer or
        // newly added DLL/source must not silently alter the later player graph.
        foreach (var path in Directory.GetFiles("Assets", "*", SearchOption.AllDirectories)
                     .Where(p => new[] { ".cs", ".dll", ".asmdef", ".asmref" }.Contains(
                                Path.GetExtension(p).ToLowerInvariant()))) {
            paths.Add(path);
            if (File.Exists(path + ".meta"))
                paths.Add(path + ".meta");
        }
        return paths.OrderBy(p => p, StringComparer.Ordinal).Select(FingerprintFile).ToArray();
    }
    static Dependency[] Dependencies(string[] inputs) {
        return AssetDatabase.GetDependencies(inputs, true)
            .Concat(inputs)
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(
                p => new Dependency { path = p,
                                      hash = AssetDatabase.GetAssetDependencyHash(p).ToString() })
            .ToArray();
    }
    static Fingerprint[] SourceFiles(Dependency[] dependencies) {
        return dependencies.Select(d => d.path)
            .SelectMany(p => new[] { p, p + ".meta" })
            .Where(File.Exists)
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(FingerprintFile)
            .ToArray();
    }
    static void ValidateInputs(Proof proof, string planPath, string[] paths) {
        if (HashFile(planPath) != proof.planSha256 ||
            JsonUtility.ToJson(new Fingerprints { files = ManagedInputs() }) !=
                JsonUtility.ToJson(new Fingerprints { files = proof.managedInputs }) ||
            JsonUtility.ToJson(new DependenciesList { items = Dependencies(paths) }) !=
                JsonUtility.ToJson(new DependenciesList { items = proof.sourceDependencies }) ||
            JsonUtility.ToJson(
                new Fingerprints { files = SourceFiles(proof.sourceDependencies) }) !=
                JsonUtility.ToJson(new Fingerprints { files = proof.sourceFiles }))
            throw new InvalidOperationException(
                "Build inputs changed during verification; output proof refused");
    }
    [Serializable]
    sealed class Fingerprints {
        public Fingerprint[] files;
    }
    [Serializable]
    sealed class DependenciesList {
        public Dependency[] items;
    }
    static string OutputPath(string destination, string name) {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) +
                   Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid bundle path: " + name);
        return path;
    }
    static AssetBundle
    LoadWithDependencies(string name, string destination,
                         System.Collections.Generic.Dictionary<string, string[]> dependencies,
                         System.Collections.Generic.Dictionary<string, AssetBundle> loaded,
                         System.Collections.Generic.HashSet<string> visiting,
                         System.Collections.Generic.HashSet<string> allowed) {
        AssetBundle result;
        if (loaded.TryGetValue(name, out result))
            return result;
        if (!allowed.Contains(name) || !visiting.Add(name))
            throw new InvalidOperationException("Unknown or cyclic bundle dependency: " + name);
        foreach (var dependency in dependencies[name])
            LoadWithDependencies(dependency, destination, dependencies, loaded, visiting, allowed);
        result = AssetBundle.LoadFromFile(OutputPath(destination, name));
        if (result == null)
            throw new InvalidOperationException("Cannot load native bundle dependency: " + name);
        loaded.Add(name, result);
        visiting.Remove(name);
        return result;
    }
    static void VerifyContent(Plan plan, string destination, AssetBundleManifest manifest,
                              Proof proof) {
        var allowed = new System.Collections.Generic.HashSet<string>(
            plan.bundles.Select(b => b.name), StringComparer.Ordinal);
        if (!allowed.SetEquals(manifest.GetAllAssetBundles()))
            throw new InvalidOperationException(
                "Built bundle manifest does not exactly match plan names");
        proof.bundleInventory =
            plan.bundles
                .Select(bundle => {
                    var dependencies = manifest.GetDirectDependencies(bundle.name);
                    if (dependencies.Length != bundle.dependencies.Length ||
                        !new System.Collections.Generic
                             .HashSet<string>(bundle.dependencies, StringComparer.Ordinal)
                             .SetEquals(dependencies))
                        throw new InvalidOperationException(
                            "Native bundle dependency graph differs from original: " + bundle.name);
                    return new BundleInventory {
                        name = bundle.name,
                        dependencies =
                            dependencies.OrderBy(n => n, StringComparer.Ordinal).ToArray()
                    };
                })
                .ToArray();
        // The manifest returned by BuildAssetBundles is a temporary native
        // object. UnloadUnusedAssetsImmediate can destroy it between batches,
        // even while this managed wrapper is in scope. Capture the already
        // validated graph as managed strings before any asset cleanup runs.
        var dependencyGraph = proof.bundleInventory.ToDictionary(
            item => item.name, item => item.dependencies, StringComparer.Ordinal);
        // Loading every prefab in a 1,539-address bundle before unloading would
        // retain its entire native texture graph. Bound retention to 16 addresses
        // and their live dependency closure; snapshots contain strings only.
        const int batchSize = 16;
        foreach (var bundle in plan.bundles) {
            for (int offset = 0; offset < bundle.assets.Length; offset += batchSize) {
                var loaded = new System.Collections.Generic.Dictionary<string, AssetBundle>(
                    StringComparer.Ordinal);
                try {
                    var current = LoadWithDependencies(
                        bundle.name, destination, dependencyGraph, loaded,
                        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal),
                        allowed);
                    var addresses = current.GetAllAssetNames();
                    var names = new System.Collections.Generic.HashSet<string>(
                        addresses, StringComparer.Ordinal);
                    if (addresses.Length != bundle.assets.Length ||
                        !names.SetEquals(bundle.assets.Select(a => a.address.ToLowerInvariant())))
                        throw new InvalidOperationException(
                            "Native bundle address set differs from original: " + bundle.name);
                    if (offset == 0)
                        proof.bundleInventory.Single(item => item.name == bundle.name).addresses =
                            addresses.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    foreach (var asset in bundle.assets.Skip(offset).Take(batchSize)) {
                        if (!names.Contains(asset.address.ToLowerInvariant()))
                            throw new InvalidOperationException(
                                "Missing native address: " + bundle.name + ":" + asset.address);
                        var expected = NativeAssetIntegrity.CaptureSource(asset.asset);
                        var actual = current.LoadAsset(asset.address);
                        var subassets = current.LoadAssetWithSubAssets(asset.address);
                        proof.serializedReferences +=
                            NativeAssetIntegrity.ValidateBundleAsset(expected, actual, subassets);
                        proof.addresses++;
                        if (proof.addresses % 100 == 0)
                            Debug.Log("NATIVEH3VR_BUNDLE_CONTENT_PROGRESS " + proof.addresses);
                    }
                } finally {
                    foreach (var item in loaded.Values.Reverse())
                        item.Unload(true);
                    EditorUtility.UnloadUnusedAssetsImmediate();
                }
            }
            proof.bundles++;
        }
    }
    public static void Build() { Execute(true); }
    public static void VerifyExisting() { Execute(false); }
    static void Execute(bool rebuild) {
        string path = Environment.GetEnvironmentVariable("NATIVEH3VR_BUNDLE_PLAN");
        string destination = Environment.GetEnvironmentVariable("NATIVEH3VR_BUNDLE_OUTPUT");
        if (string.IsNullOrEmpty(destination))
            throw new InvalidOperationException("Bundle output environment variable is required");
        Directory.CreateDirectory(destination);
        var verificationPath = Path.Combine(destination, "native-bundle-verification.json");
        var contentPath = Path.Combine(destination, "native-bundle-content-verification.json");
        var receiptPath = Path.Combine(destination, "native-bundle-compiler-receipt.json");
        var compilerLogPath = Path.Combine(destination, "native-bundle-compiler.log");
        if (rebuild)
            foreach (var stale in new[] { receiptPath, compilerLogPath })
                if (File.Exists(stale))
                    File.Delete(stale);
        var runId = Guid.NewGuid().ToString("N");
        var runMarker = "NATIVEH3VR_COMPILER_SCOPE " + runId;
        Debug.Log(runMarker);
        // Invalidate before plan parsing, preflight, imports or any other fallible work.
        foreach (var marker in new[] { verificationPath, contentPath })
            if (File.Exists(marker))
                File.Delete(marker);
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("Bundle plan environment variable is required");
        var plan = JsonUtility.FromJson<Plan>(File.ReadAllText(path));
        if (plan == null || plan.bundles == null || plan.bundles.Length == 0 ||
            plan.issues == null || plan.issues.Length != 0)
            throw new InvalidOperationException(
                "Bundle address plan is empty or has unresolved entries");
        if (Path.GetFullPath(plan.project) !=
            Path.GetFullPath(Path.Combine(Application.dataPath, "..")))
            throw new InvalidOperationException("Bundle plan belongs to a different Unity project");
        if (plan.bundles.Select(b => b.name).Distinct(StringComparer.Ordinal).Count() !=
                plan.bundles.Length ||
            plan.bundles.Any(
                b => b.assets == null || b.assets.Length == 0 ||
                     b.assets.Select(a => a.address.ToLowerInvariant()).Distinct().Count() !=
                         b.assets.Length))
            throw new InvalidOperationException("Duplicate bundle/address or empty bundle in plan");
        var bundleNames = new System.Collections.Generic.HashSet<string>(
            plan.bundles.Select(b => b.name), StringComparer.Ordinal);
        if (plan.bundles.Any(b => b.dependencies == null ||
                                  b.dependencies.Distinct(StringComparer.Ordinal).Count() !=
                                      b.dependencies.Length ||
                                  b.dependencies.Any(d => d == b.name || !bundleNames.Contains(d))))
            throw new InvalidOperationException(
                "Missing, duplicate or unknown original bundle dependencies in plan");
        var builds = plan.bundles
                         .Select(bundle => new AssetBundleBuild {
                             assetBundleName = bundle.name,
                             assetNames = bundle.assets.Select(a => a.asset).ToArray(),
                             addressableNames = bundle.assets.Select(a => a.address).ToArray()
                         })
                         .ToArray();
        var inputPaths = builds.SelectMany(b => b.assetNames).Distinct().ToArray();
        foreach (var input in inputPaths)
            if (!File.Exists(input))
                throw new FileNotFoundException("Missing planned asset", input);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,
                                       new[] { GraphicsDeviceType.Metal });
        NativeOriginalVariantFilter.Configure();
        NativeAssetIntegrity.InspectAllPrefabs(inputPaths);
        if (rebuild)
            ForceComputeImports.Run();
        var proof = new Proof { mode = rebuild ? "build" : "existing-output",
                                unityVersion = Application.unityVersion,
                                planSha256 = HashFile(path), managedInputs = ManagedInputs(),
                                sourceDependencies = Dependencies(inputPaths) };
        proof.sourceFiles = SourceFiles(proof.sourceDependencies);
        AssetBundle manifestBundle = null;
        try {
            AssetBundleManifest manifest;
            if (rebuild) {
                var compilerErrors = new System.Collections.Generic.List<string>();
                Application.LogCallback capture = (message, stack, type) => {
                    if (type == LogType.Error || type == LogType.Exception ||
                        type == LogType.Assert)
                        lock (compilerErrors) compilerErrors.Add(message);
                };
                Application.logMessageReceivedThreaded += capture;
                try {
                    manifest = BuildPipeline.BuildAssetBundles(
                        destination, builds,
                        BuildAssetBundleOptions.ChunkBasedCompression |
                            BuildAssetBundleOptions.StrictMode |
                            BuildAssetBundleOptions.ForceRebuildAssetBundle,
                        BuildTarget.StandaloneOSX);
                } finally {
                    Application.logMessageReceivedThreaded -= capture;
                }
                lock (compilerErrors) if (
                    compilerErrors.Count !=
                    0) throw new InvalidOperationException("Unity reported build errors despite " +
                                                           "its return status: " +
                                                           compilerErrors[0]);
                if (manifest == null)
                    throw new InvalidOperationException("Native bundle build failed");
                Debug.Log("NATIVEH3VR_COMPILER_BUILD_END " + runId);
                var compilerText = ReadCleanLog("NATIVEH3VR_COMPILER_BUILD_END " + runId);
                ValidateInputs(proof, path, inputPaths);
                File.WriteAllText(compilerLogPath, compilerText);
                var compilerLog = FingerprintFile(compilerLogPath);
                compilerLog.path = Path.GetFileName(compilerLogPath);
                var receipt = new CompilerReceipt { clean = true,
                                                    runId = runId,
                                                    planSha256 = proof.planSha256,
                                                    unityVersion = proof.unityVersion,
                                                    compilerLog = compilerLog,
                                                    compilerPolicySha256 = HashFile(PatternPath),
                                                    buildLogPath = Path.GetFullPath(
                                                        Application.consoleLogPath),
                                                    outputs = OutputFingerprints(plan, destination),
                                                    managedInputs = proof.managedInputs,
                                                    sourceFiles = proof.sourceFiles };
                File.WriteAllText(receiptPath, JsonUtility.ToJson(receipt, true));
            } else {
                ValidateCompilerReceipt(destination, proof, plan);
                string mainName = new DirectoryInfo(destination).Name;
                manifestBundle = AssetBundle.LoadFromFile(OutputPath(destination, mainName));
                if (manifestBundle == null)
                    throw new InvalidOperationException("Cannot reopen native bundle manifest");
                manifest = manifestBundle.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
            }
            if (manifest == null)
                throw new InvalidOperationException("Native asset bundle build/manifest failed");
            ValidateCompilerReceipt(destination, proof, plan);
            ReadCleanLog(runMarker);
            VerifyContent(plan, destination, manifest, proof);
            ValidateInputs(proof, path, inputPaths);
            ReadCleanLog(runMarker);
            proof.outputs = OutputFingerprints(plan, destination);
            proof.compilerReceipt = FingerprintFile(receiptPath);
            proof.compilerReceipt.path = Path.GetFileName(receiptPath);
            proof.compilerLog = FingerprintFile(compilerLogPath);
            proof.compilerLog.path = Path.GetFileName(compilerLogPath);
            proof.verified = true;
            File.WriteAllText(contentPath + ".tmp", JsonUtility.ToJson(proof, true));
            File.Move(contentPath + ".tmp", contentPath);
            File.WriteAllText(
                verificationPath,
                "{\"platform\":\"StandaloneOSX\",\"graphics\":\"Metal\",\"bundles\":" +
                    proof.bundles + ",\"addresses\":" + proof.addresses + ",\"missing\":0}\n");
            Debug.Log("NATIVEH3VR_BUNDLES_SUCCESS count=" + proof.bundles +
                      " loaded=" + proof.addresses + " references=" + proof.serializedReferences +
                      " mode=" + proof.mode);
        } finally {
            if (manifestBundle != null)
                manifestBundle.Unload(true);
        }
        EditorApplication.Exit(0);
    }
}
