using System;
using System.Collections.Generic;
using System.Reflection;
using FistVR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace NativeH3VR.GraphicsUpgrade
{
    internal sealed class H3VRReticlePass : ScriptableRenderPass
    {
        private const string ReflexShader = "Reflex Sight/Reflex Sight";
        private const string HolographicShader = "H3VR/HolograpicSight";
        private static readonly Action<HolographicSight> UpdateHologram = (Action<HolographicSight>)
            typeof(HolographicSight).GetMethod("OnWillRenderObject", BindingFlags.Instance | BindingFlags.NonPublic)
                .CreateDelegate(typeof(Action<HolographicSight>));

        private readonly HashSet<Renderer> lenses = new();
        private readonly List<Material> materials = new();
        private readonly List<Draw> visible = new();
        private readonly Plane[] frustum = new Plane[6];
        private int collectedFrame = -1;

        private readonly struct Draw
        {
            internal readonly Renderer renderer;
            internal readonly Material material;
            internal readonly int submesh;
            internal readonly float distance;
            internal readonly bool holographic;

            internal Draw(Renderer renderer, Material material, int submesh, float distance, bool holographic)
            {
                this.renderer = renderer;
                this.material = material;
                this.submesh = submesh;
                this.distance = distance;
                this.holographic = holographic;
            }
        }

        private sealed class PassData
        {
            internal readonly List<Draw> draws = new();
        }

        internal H3VRReticlePass() => renderPassEvent = RenderPassEvent.AfterRenderingTransparents;

        internal bool HasReticles()
        {
            if (collectedFrame == Time.frameCount) return lenses.Count != 0;
            collectedFrame = Time.frameCount;
            lenses.Clear();
            // Standalone sights can hide their interaction controls while the lens stays visible.
            foreach (var controller in Object.FindObjectsByType<ReflexSightController>(FindObjectsInactive.Include))
                if (controller.ReflexSightRenderers != null)
                    foreach (var lens in controller.ReflexSightRenderers)
                        if (lens) lenses.Add(lens);

            foreach (var hologram in Object.FindObjectsByType<HolographicSight>())
            {
                if (!hologram.isActiveAndEnabled || !hologram.VirtualQuad ||
                    !hologram.TryGetComponent<Renderer>(out var lens)) continue;
                // SRP does not run this Built-in per-object callback. It supplies the projected quad.
                UpdateHologram(hologram);
                lenses.Add(lens);
            }
            return lenses.Count != 0;
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var color = resources.activeColorTexture;
            var depth = resources.activeDepthTexture;
            if (!color.IsValid() || !depth.IsValid()) return;

            var camera = frameData.Get<UniversalCameraData>().camera;
            var eye = camera.transform.position;
            GeometryUtility.CalculateFrustumPlanes(camera, frustum);
            visible.Clear();
            bool writesStencil = false;
            foreach (var lens in lenses)
            {
                if (!lens || !lens.enabled || lens.forceRenderingOff || !lens.gameObject.activeInHierarchy ||
                    (camera.cullingMask & (1 << lens.gameObject.layer)) == 0 ||
                    (!camera.stereoEnabled && !GeometryUtility.TestPlanesAABB(frustum, lens.bounds))) continue;

                lens.GetSharedMaterials(materials);
                float distance = (lens.bounds.center - eye).sqrMagnitude;
                for (int slot = 0; slot < materials.Count; slot++)
                {
                    var material = materials[slot];
                    if (!material || !material.shader) continue;
                    bool holographic = material.shader.name == HolographicShader && material.passCount >= 3;
                    if (!holographic && (material.shader.name != ReflexShader || material.passCount < 2)) continue;
                    visible.Add(new Draw(lens, material, slot, distance, holographic));
                    writesStencil |= holographic;
                }
            }
            materials.Clear();
            if (visible.Count == 0) return;
            visible.Sort(static (a, b) => b.distance.CompareTo(a.distance));

            using var builder = graph.AddRasterRenderPass<PassData>("H3VR | Sight reticles", out var data);
            data.draws.Clear();
            data.draws.AddRange(visible);
            builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
            builder.SetRenderAttachmentDepth(depth, writesStencil ? AccessFlags.ReadWrite : AccessFlags.Read);
            builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
            {
                // URP uses one matching pass for the glass. Draw its separate reticle pass with the original property block.
                // The older hologram needs its own stencil mask around each reticle.
                foreach (var draw in pass.draws)
                    if (draw.holographic) context.cmd.DrawRenderer(draw.renderer, draw.material, draw.submesh, 2);
                foreach (var draw in pass.draws)
                {
                    if (draw.holographic) context.cmd.DrawRenderer(draw.renderer, draw.material, draw.submesh, 0);
                    context.cmd.DrawRenderer(draw.renderer, draw.material, draw.submesh, 1);
                    if (draw.holographic) context.cmd.DrawRenderer(draw.renderer, draw.material, draw.submesh, 2);
                }
            });
        }
    }
}
