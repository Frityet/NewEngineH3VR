using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FistVR;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.Newtonsoft.Json;

namespace NativeH3VR.QA
{
    public sealed partial class GraphicsProfileWitness
    {
        private IEnumerator WitnessAimedOptics()
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            const string range = "WarehouseRange_Rebuilt";
            SteamVR_LoadLevel.Begin(range);
            yield return WaitForScene(range);
            var renderEye = typeof(NativeStereoRenderer).GetMethod("RenderEye", Private, null,
                new[] { typeof(int), typeof(MetalOpenXR.View), typeof(RenderTexture) }, null);
            var eyeRelief = typeof(PIPScope).GetField("eyeReliefOcular", Private);
            Require(renderEye != null && eyeRelief != null, "Missing stock optic render entry points");
            var options = new GraphicsPresetControls();
            var samples = new List<object>();
            bool previousSuspension = stereo.SuspendSceneRendering;

            foreach (string item in new[] { "ScopeClassic3-12x42mm", "ReflexElevatedMRO" })
            {
                Require(IM.OD.TryGetValue(item, out var entry), "Missing stock optic " + item);
                var request = entry.GetGameObjectAsync();
                yield return request;
                Require(request.Result, "Failed to load stock optic " + item);
                var optic = Instantiate(request.Result, new Vector3(0f, 50f, 0f), Quaternion.identity);
                try
                {
                    foreach (var body in optic.GetComponentsInChildren<Rigidbody>(true))
                    {
                        body.useGravity = false;
                        body.isKinematic = true;
                    }
                    yield return null;
                    var scope = optic.GetComponentInChildren<PIPScope>(true);
                    var reflex = optic.GetComponentInChildren<ReflexSightController>(true);
                    Require(scope || reflex, "Missing optic controls on " + item);
                    var lens = scope ? scope.lensRenderer : reflex.ReflexSightRenderers[0];
                    float distance = scope ? (float)eyeRelief.GetValue(scope) : .30f;
                    Require(lens && distance > .02f, "Invalid lens geometry on " + item);

                    foreach (int preset in new[] { 1, 2 })
                    {
                        options.Set("Preset", preset);
                        for (int eye = 0; eye < 2; eye++)
                        {
                            var tracked = stereo.LatestFrame;
                            var view = eye == 0 ? tracked.left : tracked.right;
                            var origin = stereo.TrackingOrigin;
                            var rotation = origin.rotation * view.pose.Rotation;
                            var cameraPosition = origin.TransformPoint(view.pose.Position);
                            var alignment = scope ? scope.transform : lens.transform;
                            optic.transform.rotation = rotation * Quaternion.Inverse(alignment.rotation) * optic.transform.rotation;
                            var lensPosition = scope ? alignment.position : lens.bounds.center;
                            optic.transform.position += cameraPosition + rotation * Vector3.forward * distance - lensPosition;
                            yield return null;

                            var outputEye = stereo.EyeTexture(eye);
                            string label = $"{item}-{options.Label("Preset")}-eye{eye}";
                            stereo.SuspendSceneRendering = true;
                            var block = new MaterialPropertyBlock();
                            int scopeRenders = 0;
                            if (reflex) SetReticle(reflex, block, true);
                            yield return RenderAimedEye();
                            var image = ReadOpticCenter(outputEye);
                            if (scope)
                            {
                                var sensor = scope.scopeCam.targetTexture;
                                Require(sensor && sensor.IsCreated(), label + " has no rendered scope texture");
                                float radialScale = scope.mainProjection.vignetteUVS.z;
                                Require(radialScale == 0f && scope.mainProjection.vignetteFlat > 0f,
                                    label + " missed the flat eye-relief vignette");
                                int brightness = image.Max(OpticLuma);
                                int sensorPeak = ReadOpticCenter(sensor).Max(OpticLuma);
                                Require(scopeRenders == 1 && brightness > 65 && sensorPeak > 12,
                                    label + " shows a blank picture at eye relief");
                                samples.Add(new { label, distance, brightness, sensorPeak, radialScale, scopeRenders });
                            }
                            else
                            {
                                SetReticle(reflex, block, false);
                                yield return RenderAimedEye();
                                var dark = ReadOpticCenter(outputEye);
                                int brightened = image.Zip(dark, (on, off) => RedContrast(on) - RedContrast(off)).Count(value => value > 28);
                                Require(brightened > 8, label + " lost its illuminated reticle");
                                samples.Add(new { label, brightened });
                            }
                            stereo.SuspendSceneRendering = previousSuspension;

                            IEnumerator RenderAimedEye()
                            {
                                for (int frame = 0; frame < 8; frame++)
                                {
                                    yield return new WaitForEndOfFrame();
                                    scopeRenders = 0;
                                    if (scope) RenderPipelineManager.beginCameraRendering += CountScopeRender;
                                    try { renderEye.Invoke(stereo, new object[] { eye, view, outputEye }); }
                                    finally
                                    {
                                        if (scope) RenderPipelineManager.beginCameraRendering -= CountScopeRender;
                                    }
                                }
                            }

                            void CountScopeRender(ScriptableRenderContext context, Camera camera)
                            {
                                if (camera == scope.scopeCam) scopeRenders++;
                            }
                        }
                    }
                }
                finally
                {
                    stereo.SuspendSceneRendering = previousSuspension;
                    Destroy(optic);
                }
                yield return null;
            }
            File.WriteAllText(Path.Combine(output, "aimed-optics.json"), JsonConvert.SerializeObject(samples, Formatting.Indented));
        }

        private static void SetReticle(ReflexSightController controller, MaterialPropertyBlock block, bool illuminated)
        {
            foreach (var renderer in controller.ReflexSightRenderers)
            {
                renderer.GetPropertyBlock(block);
                block.SetFloat("_ReticleIntensity", illuminated ? renderer.sharedMaterial.GetFloat("_ReticleIntensity") : 0f);
                renderer.SetPropertyBlock(block);
            }
        }

        private static int OpticLuma(Color32 pixel) => (pixel.r + pixel.g + pixel.b) / 3;
        private static int RedContrast(Color32 pixel) => pixel.r - Math.Max(pixel.g, pixel.b);

        private static Color32[] ReadOpticCenter(RenderTexture target)
        {
            const int size = 128;
            var previous = RenderTexture.active;
            var image = new Texture2D(size, size, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = target;
                image.ReadPixels(new Rect((target.width - size) / 2, (target.height - size) / 2, size, size), 0, 0, false);
                return image.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(image);
            }
        }
    }
}
