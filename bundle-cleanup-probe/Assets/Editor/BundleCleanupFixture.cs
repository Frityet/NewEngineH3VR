using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public static class BundleCleanupFixture {
    [Serializable]
    public class Result {
        public bool passed, temporaryManifestDestroyed;
        public string unity, graphics;
        public int addresses, bundles, serializedReferences, minimumCleanupBatches;
        public NativeBundleBuild.BundleInventory[] inventory;
        public List<string> negatives = new List<string>();
        public string error;
    }
    static readonly MethodInfo verify =
        typeof(NativeBundleBuild)
            .GetMethod("VerifyContent", BindingFlags.Static | BindingFlags.NonPublic);
    static void Check(bool ok, string reason) {
        if (!ok)
            throw new Exception(reason);
    }
    static void Invoke(NativeBundleBuild.Plan plan, string output, AssetBundleManifest manifest,
                       NativeBundleBuild.Proof proof) {
        try {
            verify.Invoke(null, new object[] { plan, output, manifest, proof });
        } catch (TargetInvocationException error) {
            throw error.InnerException;
        }
    }
    static NativeBundleBuild.Plan Clone(NativeBundleBuild.Plan plan) {
        return JsonUtility.FromJson<NativeBundleBuild.Plan>(JsonUtility.ToJson(plan));
    }
    static void Reject(NativeBundleBuild.Plan plan, string output, string expected, Result result) {
        var owner = AssetBundle.LoadFromFile(Path.Combine(output, new DirectoryInfo(output).Name));
        Check(owner != null, "Cannot load fixture manifest bundle");
        try {
            var manifest = owner.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
            try {
                Invoke(plan, output, manifest, new NativeBundleBuild.Proof());
            } catch (Exception error) {
                Check(error.Message.Contains(expected), "Wrong rejection: " + error);
                result.negatives.Add(expected);
                return;
            }
            throw new Exception("Accepted invalid fixture: " + expected);
        } finally {
            owner.Unload(true);
        }
    }
    public static void Run() {
        string output = Path.GetFullPath("output/Bundles");
        var result = new Result { unity = Application.unityVersion,
                                  graphics = SystemInfo.graphicsDeviceType.ToString() };
        try {
            Check(result.graphics == "Metal", "Native Metal device required");
            Directory.CreateDirectory("Assets/Fixture");
            Directory.CreateDirectory(output);
            var shader = Shader.Find("Unlit/Color");
            Check(shader != null, "Missing fixture shader");
            var material = new Material(shader) { name = "SharedFixtureMaterial" };
            AssetDatabase.CreateAsset(material, "Assets/Fixture/Dependency.mat");
            var assets = new List<NativeBundleBuild.Asset>();
            for (int index = 0; index < 33; index++) {
                string path = "Assets/Fixture/Prefab" + index.ToString("D2") + ".prefab";
                var go = new GameObject("Fixture" + index.ToString("D2"));
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                PrefabUtility.SaveAsPrefabAsset(go, path);
                UnityEngine.Object.DestroyImmediate(go);
                assets.Add(
                    new NativeBundleBuild.Asset { asset = path,
                                                  address = "fixture/" + index.ToString("D2") });
            }
            AssetDatabase.SaveAssets();
            var plan = new NativeBundleBuild.Plan {
                project = Directory.GetCurrentDirectory(), issues = new string[0],
                bundles = new[] { new NativeBundleBuild.Bundle { name = "main",
                                                                 dependencies = new[] { "shared" },
                                                                 assets = assets.ToArray() },
                                  new NativeBundleBuild.Bundle {
                                      name = "shared", dependencies = new string[0],
                                      assets = new[] { new NativeBundleBuild.Asset {
                                          asset = "Assets/Fixture/Dependency.mat",
                                          address = "fixture/shared-material"
                                      } }
                                  } }
            };
            var builds = plan.bundles
                             .Select(b => new AssetBundleBuild {
                                 assetBundleName = b.name,
                                 assetNames = b.assets.Select(a => a.asset).ToArray(),
                                 addressableNames = b.assets.Select(a => a.address).ToArray()
                             })
                             .ToArray();
            var manifest =
                BuildPipeline.BuildAssetBundles(output, builds,
                                                BuildAssetBundleOptions.ForceRebuildAssetBundle |
                                                    BuildAssetBundleOptions.StrictMode,
                                                BuildTarget.StandaloneOSX);
            Check(manifest != null, "Fixture build failed");
            Check(manifest.GetDirectDependencies("main").SequenceEqual(new[] { "shared" }),
                  "Fixture has no real dependency");
            var proof = new NativeBundleBuild.Proof();
            Invoke(plan, output, manifest, proof);
            result.temporaryManifestDestroyed = manifest == null;
            Check(result.temporaryManifestDestroyed,
                  "BuildAssetBundles temporary manifest destruction was not reproduced");
            Check(proof.addresses == 34 && proof.bundles == 2 && proof.serializedReferences > 0,
                  "Incomplete canonical validation");
            result.addresses = proof.addresses;
            result.bundles = proof.bundles;
            result.serializedReferences = proof.serializedReferences;
            result.minimumCleanupBatches = 4;
            result.inventory = proof.bundleInventory;
            var wrongDependency = Clone(plan);
            wrongDependency.bundles[0].dependencies = new string[0];
            Reject(wrongDependency, output, "dependency graph differs", result);
            var missingAddress = Clone(plan);
            missingAddress.bundles[0].assets[0].address = "fixture/missing";
            Reject(missingAddress, output, "address set differs", result);
            var wrongSource = Clone(plan);
            wrongSource.bundles[0].assets[0].asset = assets[1].asset;
            Reject(wrongSource, output, "object/component/subasset or non-null reference changed",
                   result);
            result.passed = true;
        } catch (Exception error) {
            result.error = error.ToString();
        }
        File.WriteAllText("output/result.json", JsonUtility.ToJson(result, true));
        Debug.Log("BUNDLE_CLEANUP_FIXTURE " + JsonUtility.ToJson(result));
        EditorApplication.Exit(result.passed ? 0 : 1);
    }
}
