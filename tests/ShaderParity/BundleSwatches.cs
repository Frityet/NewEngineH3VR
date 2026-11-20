// Both the retail and converted players use their own stock AssetBundle loader.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace NativeH3VR.QA
{
    public sealed partial class MaterialSwatches
    {
        private static bool IsStockBundle(string path)
        {
            if (Path.HasExtension(path)) return false;
            var header = new byte[7];
            using (var stream = File.OpenRead(path))
                return stream.Read(header, 0, header.Length) == header.Length &&
                       Encoding.ASCII.GetString(header) == "UnityFS";
        }

        private static AssetBundle LoadedBundle(string name)
        {
#if UNITY_6000_0_OR_NEWER
            var bundles = AssetBundle.GetAllLoadedAssetBundles();
#else
            var bundles = Resources.FindObjectsOfTypeAll<AssetBundle>();
#endif
            foreach (var bundle in bundles)
                if (string.Equals(bundle.name, name, StringComparison.OrdinalIgnoreCase)) return bundle;
            return null;
        }

        private IEnumerator OpenBundle(string path, List<AssetBundle> owned, Action<AssetBundle> receive)
        {
            var existing = LoadedBundle(Path.GetFileName(path));
            if (existing)
            {
                receive(existing);
                yield break;
            }
            if (!File.Exists(path))
            {
                receive(null);
                yield break;
            }
            var request = AssetBundle.LoadFromFileAsync(path);
            yield return request;
            var opened = request.assetBundle;
            if (opened) owned.Add(opened);
            receive(opened);
        }

        private IEnumerator CaptureBundles(string output, string selection)
        {
            string directory = Application.streamingAssetsPath;
            string manifestPath = Path.Combine(directory, "StreamingAssets");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selection == "1" || selection == "*")
            {
                foreach (string path in Directory.GetFiles(directory))
                    if (!string.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase) && IsStockBundle(path))
                        names.Add(Path.GetFileName(path));
            }
            else
            {
                foreach (string item in selection.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string name = item.Trim();
                    if (name.Length == 0 || Path.GetFileName(name) != name)
                        throw new ArgumentException("Use stock bundle basenames in the swatch selection.");
                    names.Add(name);
                }
            }
            if (names.Count == 0) throw new InvalidOperationException("No stock bundles selected.");
            var manifestOwnership = new List<AssetBundle>();
            try
            {
                AssetBundle manifestBundle = null;
                yield return OpenBundle(manifestPath, manifestOwnership, bundle => manifestBundle = bundle);
                var manifest = manifestBundle ? manifestBundle.LoadAsset<AssetBundleManifest>("AssetBundleManifest") : null;
                if (!manifest) throw new InvalidOperationException("Missing stock AssetBundleManifest: " + manifestPath);
                var manifestNames = new HashSet<string>(manifest.GetAllAssetBundles(), StringComparer.OrdinalIgnoreCase);
                var ordered = new List<string>(names);
                ordered.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string name in ordered)
                {
                    string[] dependencies = manifestNames.Contains(name) ? manifest.GetAllDependencies(name) : new string[0];
                    yield return CaptureBundle(directory, name, dependencies, output);
                    yield return Resources.UnloadUnusedAssets();
                }
            }
            finally
            {
                UnloadOwned(manifestOwnership);
            }
        }

        private IEnumerator CaptureBundle(string directory, string name, string[] dependencies, string output)
        {
            var owned = new List<AssetBundle>();
            var bindings = new MaterialBindings();
            try
            {
                foreach (string dependency in dependencies)
                {
                    AssetBundle bundle = null;
                    yield return OpenBundle(Path.Combine(directory, dependency), owned, value => bundle = value);
                    if (!bundle)
                    {
                        bindings.status = "Could not load dependency " + dependency;
                        break;
                    }
                }
                AssetBundle target = null;
                if (bindings.status == "ok")
                {
                    yield return OpenBundle(Path.Combine(directory, name), owned, value => target = value);
                    if (!target) bindings.status = "Could not load bundle " + name;
                }
                if (target)
                {
                    bindings.AssetAddresses = target.GetAllAssetNames().Length;
                    var request = target.LoadAllAssetsAsync<GameObject>();
                    yield return request;
                    foreach (var asset in request.allAssets)
                    {
                        var prefab = asset as GameObject;
                        if (!prefab) continue;
                        bindings.PrefabAssets++;
                        bindings.AddHierarchy(prefab);
                    }
                }
                yield return Capture("bundle-" + name, output, bindings.All, bindings);
                // End-of-frame destruction of the swatch grid releases its last material pointers.
                yield return null;
            }
            finally
            {
                UnloadOwned(owned);
            }
        }

        private static void UnloadOwned(List<AssetBundle> owned)
        {
            for (int index = owned.Count - 1; index >= 0; index--)
                if (owned[index]) owned[index].Unload(true);
        }
    }
}
