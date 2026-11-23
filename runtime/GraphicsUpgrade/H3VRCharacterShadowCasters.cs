using System;
using System.Collections.Generic;
using FistVR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NativeH3VR.GraphicsUpgrade
{
    internal static class H3VRCharacterShadowCasters
    {
        internal const int MaxCapsules = 8;
        private const int MaxActors = 12;
        private const float FadeStart = 14f;
        private const float MaxDistance = 22f;
        private const float RefreshInterval = .4f;

        internal sealed class Actor
        {
            internal readonly Vector4[] starts = new Vector4[MaxCapsules];
            internal readonly Vector4[] ends = new Vector4[MaxCapsules];
            internal int capsuleCount;
            internal Bounds bounds;
            internal Vector4 light;
            internal float strength;
        }

        private sealed class Character
        {
            internal readonly Sosig owner;
            internal Character(Sosig owner) => this.owner = owner;
            internal Vector4 light;
            internal int lightRevision;
        }

        private static readonly Dictionary<Sosig, Character> previous = new();
        private static readonly List<Character> characters = new();
        private static readonly Actor[] actors = new Actor[MaxActors];
        private static readonly Character[] nearest = new Character[MaxActors];
        private static readonly float[] distances = new float[MaxActors];
        private static Light[] lights = Array.Empty<Light>();
        private static Scene scene;
        private static int frame = -1;
        private static int lightRevision;
        private static float refreshAt;
        private static int count;

        internal static Actor[] Collect(Camera camera, out int actorCount)
        {
            var current = SceneManager.GetActiveScene();
            if (current != scene)
            {
                Clear();
                scene = current;
            }
            if (frame != Time.frameCount)
            {
                frame = Time.frameCount;
                Build(camera, Time.unscaledTime);
            }
            actorCount = count;
            return actors;
        }

        private static void Clear()
        {
            scene = default;
            previous.Clear();
            characters.Clear();
            lights = Array.Empty<Light>();
            Array.Clear(nearest, 0, nearest.Length);
            frame = -1;
            refreshAt = 0;
            count = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            Clear();
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Application.quitting += Clear;
        }

        private static void OnSceneUnloaded(Scene _) => Clear();

        private static void Refresh(float time)
        {
            refreshAt = time + RefreshInterval;
            ++lightRevision;
            previous.Clear();
            foreach (var entry in characters)
                if (entry.owner) previous[entry.owner] = entry;
            characters.Clear();
            // The AI entity list excludes dead ragdolls. The slower refresh also
            // covers stock spawns, pools and characters loaded from game bundles.
            foreach (var owner in Object.FindObjectsByType<Sosig>())
            {
                if (!previous.TryGetValue(owner, out var entry))
                    entry = new Character(owner);
                characters.Add(entry);
            }
            previous.Clear();
            lights = Object.FindObjectsByType<Light>();
        }

        private static void Build(Camera camera, float time)
        {
            if (time >= refreshAt) Refresh(time);
            var observer = camera.transform.position;
            int selected = 0;
            count = 0;
            foreach (var entry in characters)
            {
                var owner = entry.owner;
                if (!owner || !owner.isActiveAndEnabled || owner.IsGhosted || owner.Links == null || owner.Renderers == null)
                    continue;
                float distance = MaxDistance * MaxDistance;
                for (int i = 0; i < owner.Links.Count && i < owner.Renderers.Length; ++i)
                {
                    var link = owner.Links[i];
                    if (Visible(owner, link, owner.Renderers[i], camera.cullingMask))
                        distance = Mathf.Min(distance, (link.transform.position - observer).sqrMagnitude);
                }
                if (distance >= MaxDistance * MaxDistance) continue;
                int index = Mathf.Min(selected, MaxActors - 1);
                if (selected == MaxActors && distance >= distances[index]) continue;
                while (index > 0 && distance < distances[index - 1])
                {
                    nearest[index] = nearest[index - 1];
                    distances[index] = distances[index - 1];
                    --index;
                }
                nearest[index] = entry;
                distances[index] = distance;
                selected = Mathf.Min(selected + 1, MaxActors);
            }

            for (int j = 0; j < selected; ++j)
            {
                var entry = nearest[j];
                var owner = entry.owner;
                var actor = actors[count] ??= new Actor();
                actor.capsuleCount = 0;
                for (int i = 0; i < owner.Links.Count && i < owner.Renderers.Length && actor.capsuleCount < MaxCapsules; ++i)
                {
                    var link = owner.Links[i];
                    var renderer = owner.Renderers[i];
                    if (!Visible(owner, link, renderer, camera.cullingMask) ||
                        (link.transform.position - observer).sqrMagnitude >= MaxDistance * MaxDistance ||
                        !Capsule(link, out var start, out var end, out var radius))
                        continue;
                    int slot = actor.capsuleCount++;
                    actor.starts[slot] = new Vector4(start.x, start.y, start.z, radius);
                    actor.ends[slot] = new Vector4(end.x, end.y, end.z, 0f);
                    var size = Vector3.one * (2f * radius);
                    var bound = new Bounds(start, size);
                    bound.Encapsulate(new Bounds(end, size));
                    if (slot == 0) actor.bounds = bound;
                    else actor.bounds.Encapsulate(bound);
                }
                if (actor.capsuleCount == 0) continue;
                actor.strength = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(FadeStart, MaxDistance, Mathf.Sqrt(distances[j])));
                if (entry.lightRevision != lightRevision)
                {
                    entry.light = IncomingLight(actor.bounds.center, camera.cullingMask, owner.Renderers);
                    entry.lightRevision = lightRevision;
                }
                actor.light = entry.light;
                ++count;
            }
            Array.Clear(nearest, 0, selected);
        }

        private static bool Visible(Sosig owner, SosigLink link, Renderer renderer, int mask) =>
            link && link.gameObject.activeInHierarchy && !link.IsExploded && renderer &&
            renderer.enabled && renderer.gameObject.activeInHierarchy && !renderer.forceRenderingOff &&
            renderer.shadowCastingMode != ShadowCastingMode.Off && (mask & (1 << renderer.gameObject.layer)) != 0 &&
            (!owner.VaporizeMaterial || renderer.sharedMaterial != owner.VaporizeMaterial);

        private static bool Capsule(SosigLink link, out Vector3 start, out Vector3 end, out float radius)
        {
            start = end = default;
            radius = 0f;
            if (!(link.C is CapsuleCollider capsule) || !capsule.enabled) return false;
            var t = capsule.transform;
            var scale = t.lossyScale;
            float x = Mathf.Abs(scale.x), y = Mathf.Abs(scale.y), z = Mathf.Abs(scale.z);
            Vector3 axis;
            float axisScale;
            switch (capsule.direction)
            {
                case 0: axis = t.right; axisScale = x; radius = capsule.radius * Mathf.Max(y, z); break;
                case 2: axis = t.forward; axisScale = z; radius = capsule.radius * Mathf.Max(x, y); break;
                default: axis = t.up; axisScale = y; radius = capsule.radius * Mathf.Max(x, z); break;
            }
            if (radius < .025f || radius > .75f) return false;
            float halfSegment = Mathf.Clamp(capsule.height * axisScale * .5f - radius, 0f, 1.1f);
            var center = t.TransformPoint(capsule.center);
            start = center - axis * halfSegment;
            end = center + axis * halfSegment;
            return true;
        }

        private static Vector4 IncomingLight(Vector3 position, int cameraMask, Renderer[] renderers)
        {
            int receiver = 0;
            Renderer anchor = null;
            foreach (var renderer in renderers)
            {
                if (!renderer || !renderer.enabled) continue;
                receiver |= cameraMask & (1 << renderer.gameObject.layer);
                if (!anchor) anchor = renderer;
            }
            Vector3 direction = Vector3.up;
            float strongest = 0f;
            bool sunShadow = false;
            foreach (var candidate in lights)
            {
                if (!candidate || !candidate.isActiveAndEnabled ||
                    candidate.intensity <= 0f || (candidate.cullingMask & receiver) == 0) continue;
                Vector3 incoming;
                Color color = candidate.color.linear;
                float weight = candidate.intensity * (.2126f * color.r + .7152f * color.g + .0722f * color.b);
                bool directional = candidate.type == LightType.Directional;
                if (directional)
                {
                    incoming = -candidate.transform.forward;
                }
                else
                {
                    Vector3 delta = candidate.transform.position - position;
                    float distanceSquared = delta.sqrMagnitude;
                    if (distanceSquared < .04f || distanceSquared >= candidate.range * candidate.range) continue;
                    incoming = delta / Mathf.Sqrt(distanceSquared);
                    if (candidate.type == LightType.Spot &&
                        Vector3.Dot(candidate.transform.forward, -incoming) < Mathf.Cos(candidate.spotAngle * Mathf.Deg2Rad * .5f))
                        continue;
                    weight /= 1f + distanceSquared;
                }
                if (incoming.y < .1f || weight <= strongest) continue;
                strongest = weight;
                direction = incoming;
                sunShadow = directional && candidate.shadows != LightShadows.None &&
                    candidate.shadowStrength > 0f && candidate.bakingOutput.lightmapBakeType != LightmapBakeType.Baked;
            }
            if (strongest < .045f)
            {
                LightProbes.GetInterpolatedProbe(position, anchor, out var probe);
                // Unity's first-order real SH basis is ordered Y, Z, X.
                var gradient = new Vector3(ProbeLuma(probe, 3), ProbeLuma(probe, 1), ProbeLuma(probe, 2));
                if (gradient.sqrMagnitude > .0001f)
                    direction = new Vector3(gradient.x, Mathf.Max(gradient.y, gradient.magnitude * .55f), gradient.z).normalized;
            }
            return new Vector4(direction.x, direction.y, direction.z, sunShadow ? 1f : 0f);
        }

        private static float ProbeLuma(SphericalHarmonicsL2 probe, int coefficient) =>
            .2126f * probe[0, coefficient] + .7152f * probe[1, coefficient] + .0722f * probe[2, coefficient];
    }
}
