// Rebuilds original H3VR content bundles with Unity 2021 asset data while preserving addresses.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace H3VRConverter
{
    public static class BundleConversion
    {
        [Serializable]
        private sealed class Plan
        {
            public int schemaVersion;
            public string project;
            public string sourceStreamingAssets;
            public Bundle[] bundles;
            public string[] issues;
            public Source[] sourceFiles;
        }
        [Serializable] private sealed class Bundle { public string name; public string[] dependencies; public Asset[] assets; }
        [Serializable] private sealed class Asset { public string address; public string asset; }
        [Serializable] private sealed class Source { public string name; public string sha256; public long bytes; }

        public static void Validate(string planPath)
        {
            var plan = ReadAndValidate(planPath, true);
            Debug.Log("H3VR_BUNDLE_PLAN_VALIDATION bundles=" + plan.bundles.Length + " addresses=" + plan.bundles.Sum(b => b.assets.Length));
        }

        public static void Build(string planPath, string outputDirectory)
        {
            var plan = ReadAndValidate(planPath, true);
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var assetBundleOutput = Path.GetFullPath(outputDirectory);
            if (IsInside(projectRoot, assetBundleOutput)) throw new InvalidOperationException("Temporary bundle output must be outside the Unity project.");
            if (Directory.Exists(assetBundleOutput) && Directory.EnumerateFileSystemEntries(assetBundleOutput).Any()) throw new IOException("Temporary bundle output directory must be empty: " + assetBundleOutput);
            Directory.CreateDirectory(assetBundleOutput);

            var builds = plan.bundles.Select(bundle => new AssetBundleBuild
            {
                assetBundleName = bundle.name,
                assetNames = bundle.assets.Select(asset => asset.asset).ToArray(),
                addressableNames = bundle.assets.Select(asset => asset.address).ToArray()
            }).ToArray();
            var errors = new List<string>();
            Application.LogCallback capture = (message, stack, type) => { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) lock (errors) errors.Add(message); };
            AssetBundleManifest manifest;
            Application.logMessageReceivedThreaded += capture;
            try
            {
                manifest = BuildPipeline.BuildAssetBundles(assetBundleOutput, builds,
                    BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                    EditorUserBuildSettings.activeBuildTarget);
            }
            finally { Application.logMessageReceivedThreaded -= capture; }
            lock (errors) if (errors.Count > 0) throw new InvalidOperationException("Unity reported bundle build errors: " + errors[0]);
            if (manifest == null) throw new InvalidOperationException("Unity did not produce an AssetBundleManifest.");
            VerifyManifest(plan, manifest);
            var generated = GeneratedPaths(plan, assetBundleOutput);
            foreach (var item in generated) if (!File.Exists(item)) throw new FileNotFoundException("Unity did not produce a planned bundle output.", item);
            // Validate every destination before copying the first file. A conflict must not leave a
            // partially replaced set of runtime bundles in the candidate project.
            var streamingAssets = Path.Combine(projectRoot, "Assets", "StreamingAssets");
            foreach (var item in generated)
            {
                var destination = Path.Combine(streamingAssets, Path.GetFileName(item));
                if (!destination.StartsWith(Path.GetFullPath(streamingAssets) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("Bundle output target escaped StreamingAssets.");
                if (File.Exists(destination) && !HashFile(destination).Equals(HashFile(item), StringComparison.Ordinal))
                    throw new IOException("Refusing to replace an existing StreamingAssets file: " + Path.GetFileName(item));
            }
            foreach (var item in generated) CopyIntoStreamingAssets(projectRoot, item);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("H3VR_BUNDLE_REBUILD_COMPLETE bundles=" + plan.bundles.Length + " addresses=" + plan.bundles.Sum(b => b.assets.Length) + " target=" + EditorUserBuildSettings.activeBuildTarget);
        }

        private static Plan ReadAndValidate(string planPath, bool verifySources)
        {
            if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath)) throw new FileNotFoundException("Original bundle address plan is missing.", planPath);
            var plan = JsonUtility.FromJson<Plan>(File.ReadAllText(planPath));
            if (plan == null || plan.schemaVersion != 1 || plan.bundles == null || plan.bundles.Length == 0 || plan.issues == null || plan.sourceFiles == null)
                throw new InvalidDataException("Bundle plan is missing required schema-1 records.");
            if (plan.issues.Length != 0) throw new InvalidDataException("Bundle address plan contains unresolved entries: " + plan.issues[0]);
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!Path.GetFullPath(plan.project).Equals(projectRoot, StringComparison.Ordinal)) throw new InvalidOperationException("Bundle plan belongs to a different Unity project.");
            var names = new HashSet<string>(plan.bundles.Select(b => b.name), StringComparer.Ordinal);
            if (names.Count != plan.bundles.Length) throw new InvalidDataException("Bundle plan contains duplicate names.");
            foreach (var bundle in plan.bundles)
            {
                if (string.IsNullOrWhiteSpace(bundle.name) || bundle.name.IndexOfAny(new[] { '/', '\\' }) >= 0 || bundle.name == "." || bundle.name == ".." || bundle.assets == null || bundle.assets.Length == 0 || bundle.dependencies == null)
                    throw new InvalidDataException("Bundle plan contains an empty or unsafe bundle record.");
                var addressSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var asset in bundle.assets)
                {
                    if (string.IsNullOrWhiteSpace(asset.address) || string.IsNullOrWhiteSpace(asset.asset) || !addressSet.Add(asset.address)) throw new InvalidDataException("Bundle has empty or duplicate address: " + bundle.name);
                    var full = ProjectPath(projectRoot, asset.asset);
                    if (!asset.asset.StartsWith("Assets/", StringComparison.Ordinal) || !IsInside(Path.Combine(projectRoot, "Assets"), full))
                        throw new InvalidDataException("Planned bundle assets must resolve inside Assets: " + asset.asset);
                    if (!File.Exists(full)) throw new FileNotFoundException("Planned Unity asset is missing.", full);
                }
                if (bundle.dependencies.Distinct(StringComparer.Ordinal).Count() != bundle.dependencies.Length || bundle.dependencies.Any(d => !names.Contains(d) || d == bundle.name)) throw new InvalidDataException("Bundle has duplicate, cyclic-self or unknown dependencies: " + bundle.name);
            }
            if (verifySources)
            {
                var sourceRoot = Path.GetFullPath(plan.sourceStreamingAssets);
                foreach (var source in plan.sourceFiles)
                {
                    var path = SourcePath(sourceRoot, source.name);
                    if (!File.Exists(path) || new FileInfo(path).Length != source.bytes || !HashFile(path).Equals(source.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Original bundle input changed after planning: " + source.name);
                }
            }
            return plan;
        }

        private static void VerifyManifest(Plan plan, AssetBundleManifest manifest)
        {
            var actualNames = new HashSet<string>(manifest.GetAllAssetBundles(), StringComparer.Ordinal);
            if (!actualNames.SetEquals(plan.bundles.Select(b => b.name))) throw new InvalidDataException("Unity generated bundle names that differ from the original plan.");
            foreach (var bundle in plan.bundles)
            {
                var actual = manifest.GetDirectDependencies(bundle.name);
                if (!new HashSet<string>(actual, StringComparer.Ordinal).SetEquals(bundle.dependencies) || actual.Length != bundle.dependencies.Length)
                    throw new InvalidDataException("Unity generated a different dependency graph for " + bundle.name);
            }
        }

        private static IEnumerable<string> GeneratedPaths(Plan plan, string output)
        {
            foreach (var bundle in plan.bundles)
            {
                if (bundle.name.IndexOfAny(new[] { '/', '\\' }) >= 0 || bundle.name == "." || bundle.name == "..") throw new InvalidDataException("Unsafe original bundle name: " + bundle.name);
                yield return OutputPath(output, bundle.name); yield return OutputPath(output, bundle.name + ".manifest");
            }
            var manifestName = new DirectoryInfo(output).Name;
            yield return OutputPath(output, manifestName); yield return OutputPath(output, manifestName + ".manifest");
        }

        private static void CopyIntoStreamingAssets(string projectRoot, string source)
        {
            var root = Path.Combine(projectRoot, "Assets", "StreamingAssets");
            var name = Path.GetFileName(source); var destination = Path.Combine(root, name);
            if (!destination.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Bundle output target escaped StreamingAssets.");
            if (File.Exists(destination))
            {
                if (HashFile(destination).Equals(HashFile(source), StringComparison.Ordinal)) return;
                throw new IOException("Refusing to replace an existing StreamingAssets file: " + name);
            }
            Directory.CreateDirectory(root); File.Copy(source, destination, false);
        }

        private static string ProjectPath(string projectRoot, string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains("..")) throw new InvalidDataException("Bundle asset path must remain project-relative: " + relative);
            var full = Path.GetFullPath(Path.Combine(projectRoot, relative));
            if (!IsInside(projectRoot, full)) throw new InvalidDataException("Bundle asset path escapes project: " + relative);
            return full;
        }
        private static string SourcePath(string sourceRoot, string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains("..")) throw new InvalidDataException("Bundle source path must remain relative: " + relative);
            var full = Path.GetFullPath(Path.Combine(sourceRoot, relative));
            if (!IsInside(sourceRoot, full)) throw new InvalidDataException("Bundle source path escapes its root: " + relative);
            return full;
        }
        private static string OutputPath(string outputRoot, string name)
        {
            if (Path.IsPathRooted(name) || name.Split('/', '\\').Contains("..")) throw new InvalidDataException("Bundle output name is unsafe: " + name);
            var full = Path.GetFullPath(Path.Combine(outputRoot, name));
            if (!IsInside(outputRoot, full)) throw new InvalidDataException("Bundle output path escapes its temporary directory.");
            return full;
        }
        private static bool IsInside(string root, string child)
        {
            root = Path.GetFullPath(root); child = Path.GetFullPath(child);
            var volume = Path.GetPathRoot(root) ?? "";
            if (root.Length > volume.Length) root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = root == volume ? root : root + Path.DirectorySeparatorChar;
            return child.StartsWith(prefix, StringComparison.Ordinal) || child.Equals(root, StringComparison.Ordinal);
        }
        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
