using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using NativeH3VR;

namespace H3VRConverter
{
    public static class LegacyConvexBake
    {
        [Serializable]
        private sealed class Plan
        {
            public int schemaVersion;
            public string unityVersion;
            public Request[] requests;
        }

        [Serializable]
        private sealed class Request
        {
            public int index;
            public string path;
            public string guid;
            public long sourceFileId;
            public float skinWidth;
        }

        [Serializable] private sealed class Report { public int schemaVersion = 1; public Replacement[] replacements; }

        [Serializable]
        private sealed class Replacement
        {
            public int index;
            public long bakedFileId;
            public bool usedVertexLimit;
            public string issue;
        }

        public static void Run()
        {
            var plan = JsonUtility.FromJson<Plan>(File.ReadAllText(Argument("-nativeH3vrConvexPlan")));
            if (plan == null || plan.schemaVersion != 1 || plan.unityVersion != Application.unityVersion || plan.requests == null)
                throw new InvalidDataException("Invalid convex bake plan or Unity version.");

            EditorSettings.serializationMode = SerializationMode.ForceText;
            var replacements = new List<Replacement>(plan.requests.Length);
            foreach (var batch in plan.requests.GroupBy(request => request.path))
                BakeAsset(batch.Key, batch, replacements);
            var report = new Report { replacements = replacements.ToArray() };
            File.WriteAllText(Argument("-nativeH3vrConvexReport"), JsonUtility.ToJson(report));
        }

        private static void BakeAsset(string path, IEnumerable<Request> batch, List<Replacement> replacements)
        {
            var sources = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToArray();
            var baked = new List<(Request, Replacement)>();

            foreach (var request in batch)
            {
                var result = new Replacement { index = request.index };
                replacements.Add(result);

                var source = sources.SingleOrDefault(mesh => HasIdentity(mesh, request.guid, request.sourceFileId));
                if (source == null)
                {
                    result.issue = "Source mesh could not be imported at the recorded GUID and file ID.";
                    continue;
                }

                string name = BakedName(request.index);
                if (sources.Any(mesh => mesh.name == name))
                    throw new InvalidDataException("Generated convex mesh name collides in " + path);
                using var data = MeshUtility.AcquireReadOnlyMeshData(source);
                using var vertices = new NativeArray<Vector3>(data[0].vertexCount, Allocator.Temp);
                data[0].GetVertices(vertices);
                var expanded = LegacyConvexHull.Create(name, vertices.ToArray(), request.skinWidth, out result.usedVertexLimit);
                if (expanded == null)
                {
                    result.issue = "Legacy convex hull is degenerate or exceeds the supported hull limit.";
                    continue;
                }
                expanded.name = name;
                expanded.hideFlags = HideFlags.None;
                AssetDatabase.AddObjectToAsset(expanded, path);
                baked.Add((request, result));
            }

            if (baked.Count == 0)
                return;
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var saved = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToArray();
            foreach (var (request, result) in baked)
            {
                if (!saved.Any(mesh => HasIdentity(mesh, request.guid, request.sourceFileId)))
                    throw new InvalidDataException("The bake changed the original Mesh identity: " + path);
                var expanded = saved.Single(mesh => mesh.name == BakedName(request.index));
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(expanded, out string guid, out long id) ||
                    guid != request.guid || id == request.sourceFileId || id == 0)
                    throw new InvalidDataException("The expanded Mesh is not a subasset of its original Mesh: " + path);
                result.bakedFileId = id;
            }
        }

        private static bool HasIdentity(Mesh mesh, string guid, long id) =>
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string actualGuid, out long actualId) &&
            actualGuid == guid && actualId == id;

        private static string BakedName(int index) => "NativeH3VR_LegacyConvex_" + index.ToString(CultureInfo.InvariantCulture);

        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name) + 1;
            if (index == 0 || index == args.Length)
                throw new ArgumentException("Missing " + name);
            return args[index];
        }
    }
}
