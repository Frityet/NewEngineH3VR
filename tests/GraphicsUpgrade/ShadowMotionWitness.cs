using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FistVR;
using UnityEngine;
using Valve.Newtonsoft.Json;

namespace NativeH3VR.QA
{
    public sealed partial class GraphicsProfileWitness
    {
        private const string ShadowScene = "TakeAndHold_WinterWasteland";
        private const float MinimumContrast = 35f;
        private const float MinimumLitLuminance = 100f;
        private const float MaximumPatchDrift = 8f;

        private readonly struct ShadowSample
        {
            public readonly string preset, view;
            public readonly int eye;
            public readonly float shaded, lit;

            internal ShadowSample(string preset, string view, int eye, float shaded, float lit)
            {
                this.preset = preset;
                this.view = view;
                this.eye = eye;
                this.shaded = shaded;
                this.lit = lit;
            }
        }

        private IEnumerator WitnessShadowMotion()
        {
            Require(Application.CanStreamedLevelBeLoaded(ShadowScene), "The stock Northest Dakota scene is missing");
            SteamVR_LoadLevel.Begin(ShadowScene);
            yield return WaitForScene(ShadowScene);

            var containerView = new Vector3(375.6f, 7.8f, -200.7f);
            yield return MeasureShadowView("shadow-motion.json", "container", "polySurface3940",
                containerView, Quaternion.LookRotation(new Vector3(378f, 8.4f, -185f) - containerView),
                new Vector3(377.085876f, 6.48126554f, -196.273682f), new Vector3(373.7251f, 6.43337154f, -196.418137f),
                5f, 5f);
            yield return MeasureShadowView("shadow-motion-tree.json", "first Hold tree", "polySurface3955",
                new Vector3(134.621765f, 37.53f, -462.051331f), new Quaternion(0f, -.6376955f, 0f, .770288564f),
                new Vector3(88.7678146f, 18.94717f, -451.910248f), new Vector3(95.4599f, 18.94717f, -453.6191f),
                3f, 1.5f);
        }

        private IEnumerator MeasureShadowView(string filename, string landmark, string groundCollider,
            Vector3 neutralCenter, Quaternion neutralRotation, Vector3 shadedPoint, Vector3 litPoint,
            float turnDegrees, float pitchDegrees)
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var renderEye = typeof(NativeStereoRenderer).GetMethod("RenderEye", Private, null,
                new[] { typeof(int), typeof(MetalOpenXR.View), typeof(RenderTexture) }, null);
            var positionField = typeof(MetalOpenXR.Pose).GetField("position", Private);
            var rotationField = typeof(MetalOpenXR.Pose).GetField("rotation", Private);
            Require(renderEye != null && positionField != null && rotationField != null && XRBridge.RenderEye != null,
                "The native Metal stereo eye route is unavailable");
            var optics = stereo.LatestFrame;
            Require(stereo.EyeWidth == 1440 && stereo.EyeHeight == 1600 && stereo.SourceCamera,
                "The regression requires native-sized 1440 × 1600 eye targets");

            var forward = Vector3.ProjectOnPlane(neutralRotation * Vector3.forward, Vector3.up).normalized;
            GM.CurrentMovementManager.TeleportToPoint(neutralCenter - Vector3.up * 1.6f, false, forward);
            yield return new WaitForSecondsRealtime(1f);

            var options = new GraphicsPresetControls();
            var poses = new[]
            {
                (name: "neutral", yaw: 0f, pitch: 0f, lateral: 0f),
                (name: "right", yaw: turnDegrees, pitch: 0f, lateral: .35f),
                (name: "left", yaw: -turnDegrees, pitch: 0f, lateral: -.35f),
                (name: "angled", yaw: turnDegrees * .7071068f, pitch: pitchDegrees, lateral: .247487f),
                (name: "return", yaw: 0f, pitch: 0f, lateral: 0f),
            };
            var target = new RenderTexture(stereo.EyeTexture(0).descriptor);
            var patch = new Texture2D(7, 7, TextureFormat.RGBA32, false);
            var samples = new List<ShadowSample>();
            var failures = new List<string>();
            var end = new WaitForEndOfFrame();
            bool previouslySuspended = stereo.SuspendSceneRendering;
            float previousTimeScale = Time.timeScale;
            try
            {
                Require(target.Create(), "Could not create the native-sized shadow witness target");
                stereo.SuspendSceneRendering = true;
                // Northest animates its trees. Hold scene time fixed while the
                // tracked-eye matrices and TAA histories still advance per frame.
                Time.timeScale = 0f;
                for (int preset = 1; preset <= 2; preset++)
                {
                    options.Set("Preset", preset);
                    string name = options.Label("Preset");
                    Require(name == (preset == 1 ? "Quality" : "Ultra"), "Unexpected regression preset");
                    foreach (var pose in poses)
                    {
                        var center = neutralCenter + neutralRotation * Vector3.right * pose.lateral;
                        var rotation = neutralRotation * Quaternion.Euler(pose.pitch, pose.yaw, 0f);
                        for (int frame = 0; frame <= 12; frame++)
                        {
                            yield return end;
                            for (int eye = 0; eye < 2; eye++)
                            {
                                var view = eye == 0 ? optics.left : optics.right;
                                var midpoint = (optics.left.pose.Position + optics.right.pose.Position) * .5f;
                                var fromLeft = Quaternion.Inverse(optics.left.pose.Rotation);
                                var worldEye = center + rotation * (fromLeft * (view.pose.Position - midpoint));
                                var worldRotation = rotation * (fromLeft * view.pose.Rotation);
                                var origin = stereo.TrackingOrigin;
                                var local = origin.InverseTransformPoint(worldEye);
                                var localRotation = Quaternion.Inverse(origin.rotation) * worldRotation;
                                object nativePose = view.pose;
                                positionField.SetValue(nativePose, new Vector3(local.x, local.y, -local.z));
                                rotationField.SetValue(nativePose, new Quaternion(-localRotation.x, -localRotation.y,
                                    localRotation.z, localRotation.w));
                                view.pose = (MetalOpenXR.Pose)nativePose;
                                renderEye.Invoke(stereo, new object[] { eye, view, target });
                                if (frame != 12)
                                    continue;
                                var source = stereo.SourceCamera;
                                var clip = view.Projection(source.nearClipPlane, source.farClipPlane) *
                                           Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) *
                                           Matrix4x4.TRS(worldEye, worldRotation, Vector3.one).inverse;
                                samples.Add(new ShadowSample(name, pose.name, eye,
                                    ShadowLuminance(worldEye, shadedPoint, groundCollider, clip, target, patch),
                                    ShadowLuminance(worldEye, litPoint, groundCollider, clip, target, patch)));
                            }
                        }
                    }
                }
            }
            finally
            {
                Time.timeScale = previousTimeScale;
                stereo.SuspendSceneRendering = previouslySuspended;
                target.Release();
                Destroy(target);
                Destroy(patch);
            }

            foreach (var group in samples.GroupBy(sample => (sample.preset, sample.eye)))
            {
                float shadeDrift = group.Max(sample => sample.shaded) - group.Min(sample => sample.shaded);
                float litDrift = group.Max(sample => sample.lit) - group.Min(sample => sample.lit);
                if (shadeDrift > MaximumPatchDrift || litDrift > MaximumPatchDrift)
                    failures.Add($"{group.Key.preset} eye {group.Key.eye} patch drift shaded={shadeDrift:F2}, lit={litDrift:F2}, limit={MaximumPatchDrift}");
                foreach (var sample in group)
                    if (sample.lit < MinimumLitLuminance || sample.lit - sample.shaded < MinimumContrast)
                        failures.Add($"{sample.preset} {sample.view} eye {sample.eye} lost the visible lit/shaded contrast");
            }
            File.WriteAllText(Path.Combine(output, filename), JsonConvert.SerializeObject(new
            {
                scene = ShadowScene, landmark, groundCollider, width = 1440, height = 1600, settleFrames = 12, simulationPaused = true,
                minimumLitMinusShaded = MinimumContrast, minimumLitLuminance = MinimumLitLuminance,
                maximumPatchDrift = MaximumPatchDrift,
                shadedPatch = new { shadedPoint.x, shadedPoint.y, shadedPoint.z, range = Vector3.Distance(neutralCenter, shadedPoint) },
                litPatch = new { litPoint.x, litPoint.y, litPoint.z, range = Vector3.Distance(neutralCenter, litPoint) },
                poses = poses.Select(pose => new { pose.name, pose.yaw, pose.pitch, pose.lateral }), passed = failures.Count == 0, failures, samples,
            }, Formatting.Indented));
            Require(failures.Count == 0, string.Join("; ", failures));
        }

        private static float ShadowLuminance(Vector3 eye, Vector3 world, string groundCollider, Matrix4x4 clip,
            RenderTexture target, Texture2D patch)
        {
            var ray = world - eye;
            Require(Physics.Raycast(eye, ray.normalized, out var hit, ray.magnitude + .25f, ~0,
                        QueryTriggerInteraction.Ignore) &&
                    hit.collider.name == groundCollider && hit.normal.y > .8f &&
                    Vector3.Distance(hit.point, world) < .25f,
                $"The known snow patch at {world} is occluded or its ground has changed");
            var projected = clip * new Vector4(world.x, world.y, world.z, 1f);
            Require(projected.w > 0f, "A shadow patch is behind the native eye");
            int x = Mathf.FloorToInt((projected.x / projected.w * .5f + .5f) * target.width) - patch.width / 2;
            int y = Mathf.FloorToInt((projected.y / projected.w * .5f + .5f) * target.height) - patch.height / 2;
            Require(x >= 0 && y >= 0 && x + patch.width <= target.width && y + patch.height <= target.height,
                "A shadow patch is outside the native eye image");
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                patch.ReadPixels(new Rect(x, y, patch.width, patch.height), 0, 0, false);
                var pixels = patch.GetRawTextureData<Color32>();
                float sum = 0;
                foreach (var color in pixels)
                    sum += .2126f * color.r + .7152f * color.g + .0722f * color.b;
                return sum / pixels.Length;
            }
            finally { RenderTexture.active = previous; }
        }
    }
}
