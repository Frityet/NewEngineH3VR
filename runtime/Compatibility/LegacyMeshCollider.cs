using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace NativeH3VR
{
    internal static class LegacyMeshCollider
    {
        internal const float DefaultSkinWidth = 0.01f;
        private static readonly ConditionalWeakTable<MeshCollider, State> States =
            new ConditionalWeakTable<MeshCollider, State>();

        internal static void SetInflation(MeshCollider collider, bool enabled)
        {
            var state = States.GetValue(RequireCollider(collider), _ => new State());
            state.Enabled = enabled;
            state.Update(collider, state.SourceMesh(collider), collider.convex);
        }

        internal static void SetSkinWidth(MeshCollider collider, float width)
        {
            var state = States.GetValue(RequireCollider(collider), _ => new State());
            if (!float.IsFinite(width) || width <= 0f)
            {
                Debug.LogWarning("Legacy MeshCollider.skinWidth must be positive and finite.", collider);
                return;
            }
            state.Width = width;
            state.Update(collider, state.SourceMesh(collider), collider.convex);
        }

        internal static void SetConvex(MeshCollider collider, bool convex)
        {
            if (!States.TryGetValue(RequireCollider(collider), out var state))
            {
                collider.convex = convex;
                return;
            }

            // The old API changed the cooked geometry only in convex mode.
            // Restore the source before cooking a non-convex shape.
            state.Update(collider, state.SourceMesh(collider), convex);
            collider.convex = convex;
        }

        internal static Mesh GetSharedMesh(MeshCollider collider) =>
            States.TryGetValue(RequireCollider(collider), out var state) ? state.SourceMesh(collider) : collider.sharedMesh;

        internal static void SetSharedMesh(MeshCollider collider, Mesh source)
        {
            if (States.TryGetValue(RequireCollider(collider), out var state))
                state.Update(collider, source, collider.convex, recook: true);
            else
                collider.sharedMesh = source;
        }

        private static MeshCollider RequireCollider(MeshCollider collider) => collider ?? throw new NullReferenceException();

        private sealed class State
        {
            public bool Enabled;
            public float Width = DefaultSkinWidth;
            private Mesh source;
            private Mesh generated;
            private float generatedWidth;
            private bool warned;
            private LegacyConvexMeshOwner owner;

            public Mesh SourceMesh(MeshCollider collider)
            {
                var assigned = collider.sharedMesh;
                return generated != null && assigned == generated ? source : assigned;
            }

            public void Update(MeshCollider collider, Mesh input, bool convex, bool recook = false)
            {
                Mesh next = input;
                if (Enabled && convex && input != null)
                {
                    bool reuse = !recook && generated != null && source == input && Width == generatedWidth;
                    next = reuse ? generated : LegacyConvexHull.Create(input, Width);
                    if (next == null)
                    {
                        if (!warned)
                        {
                            warned = true;
                            Debug.LogWarning("Legacy convex mesh inflation could not be applied. Unity will cook the supplied mesh.", collider);
                        }
                        next = input;
                    }
                }

                if (recook || collider.sharedMesh != next)
                    collider.sharedMesh = next;
                if (generated != null && generated != next)
                    owner.Release(generated);
                if (next != input && generated != next)
                {
                    if (owner == null)
                        owner = collider.GetComponent<LegacyConvexMeshOwner>() ??
                            collider.gameObject.AddComponent<LegacyConvexMeshOwner>();
                    owner.Track(next);
                }
                source = input;
                generated = next == input ? null : next;
                generatedWidth = Width;
            }
        }
    }

    // Managed wrapper collection does not release native Mesh objects. Match the
    // ownership of generated shapes to the owner GameObject's lifetime.
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    internal sealed class LegacyConvexMeshOwner : MonoBehaviour
    {
        private readonly List<Mesh> meshes = new List<Mesh>();

        internal void Track(Mesh mesh)
        {
            hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
            meshes.Add(mesh);
        }

        internal void Release(Mesh mesh)
        {
            meshes.Remove(mesh);
            DestroyMesh(mesh);
        }

        private void OnDestroy()
        {
            foreach (var mesh in meshes)
                DestroyMesh(mesh);
            meshes.Clear();
        }

        private static void DestroyMesh(Mesh mesh)
        {
            if (Application.isPlaying)
                Destroy(mesh);
            else
                DestroyImmediate(mesh);
        }
    }
}
