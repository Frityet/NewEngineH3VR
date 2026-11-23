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
        const int SosigCrop = 512;

        private IEnumerator WitnessSosigShadow(TNH_HoldPoint hold)
        {
            var candidates = ((List<Sosig>)HoldUnits.GetValue(hold))
                .Where(unit => unit && unit.BodyState == Sosig.SosigBodyState.InControl)
                .ToArray();
            Require(candidates.Length != 0, "No active stock Sosig in the first Hold");
            var sun = FindObjectsByType<Light>().Where(light => light.isActiveAndEnabled &&
                light.type == LightType.Directional && light.shadows != LightShadows.None)
                .OrderByDescending(light => light.intensity).FirstOrDefault();
            if (sun && sun.shadowStrength <= 0) sun = null;
            var actor = candidates.OrderByDescending(unit => sun ? 0 : GroundClearance(unit))
                .ThenBy(unit => Vector3.Distance(unit.transform.position, stereo.SourceCamera.transform.position))
                .First();
            var links = actor.Links.Where(link => link).ToArray();
            var parts = actor.Renderers.Concat(actor.GetComponentsInChildren<Renderer>(true))
                .Concat(links.SelectMany(link => link.GetComponentsInChildren<Renderer>(true)))
                .Where(renderer => renderer is MeshRenderer or SkinnedMeshRenderer)
                .Distinct().ToArray();
            Require(parts.Any(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy),
                "The stock Sosig has no live body renderers");
            var original = parts.Select(renderer => renderer.shadowCastingMode).ToArray();
            var pose = actor.transform;
            Vector3 originalPosition = pose.position;
            Quaternion originalRotation = pose.rotation;
            var linkPoses = links.Select(link => (link.transform.position, link.transform.rotation)).ToArray();
            var optics = stereo.LatestFrame;
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var render = typeof(NativeStereoRenderer).GetMethod("RenderEye", Private, null,
                new[] { typeof(int), typeof(MetalOpenXR.View), typeof(RenderTexture) }, null);
            bool contact = Environment.GetEnvironmentVariable("NATIVEH3VR_SOSIG_CONTACT_WITNESS") == "1";
            var contactType = Type.GetType(
                "NativeH3VR.GraphicsUpgrade.H3VRCharacterShadowPass, NativeH3VR.GraphicsUpgrade");
            const BindingFlags InternalStatic = BindingFlags.Static | BindingFlags.NonPublic;
            var options = new GraphicsPresetControls();
            bool hasContact = options.Keys.Contains("CharacterShadows");
            int originalContact = hasContact ? options.Index("CharacterShadows") : 0;
            var lastContactDraws = contactType?.GetProperty("LastDrawCount", InternalStatic);
            Require(!contact || hasContact && lastContactDraws != null,
                "The build does not expose Enhanced character shadows and their draw counter");
            var positionField = typeof(MetalOpenXR.Pose).GetField("position", Private);
            var rotationField = typeof(MetalOpenXR.Pose).GetField("rotation", Private);
            Require(render != null && positionField != null && rotationField != null, "Missing native eye render route");
            var target = new RenderTexture(stereo.EyeTexture(0).descriptor);
            var tile = new Texture2D(SosigCrop, SosigCrop, TextureFormat.RGBA32, false);
            var samples = new List<object>();
            var failures = new List<string>();
            bool suspended = stereo.SuspendSceneRendering;
            float timeScale = Time.timeScale;
            try
            {
                Require(target.Create(), "Could not create the Sosig shadow witness target");
                stereo.SuspendSceneRendering = true;
                Time.timeScale = 0;
                for (int movement = 0; movement < 2; movement++)
                {
                    if (movement > 0)
                    {
                        var turn = Quaternion.AngleAxis(35, Vector3.up);
                        var shifted = originalPosition + Vector3.right * .35f;
                        pose.SetPositionAndRotation(shifted, turn * originalRotation);
                        for (int i = 0; i < links.Length; i++)
                            links[i].transform.SetPositionAndRotation(shifted + turn * (linkPoses[i].position - originalPosition),
                                turn * linkPoses[i].rotation);
                        Physics.SyncTransforms();
                    }

                    var enabled = parts.Where(part => part.enabled && part.gameObject.activeInHierarchy).ToArray();
                    var box = enabled[0].bounds;
                    foreach (var part in enabled.Skip(1)) box.Encapsulate(part.bounds);
                    var shadowDirection = sun ? Vector3.ProjectOnPlane(sun.transform.forward, Vector3.up).normalized : Vector3.forward;
                    if (shadowDirection.sqrMagnitude < .1f) shadowDirection = Vector3.forward;
                    var foot = new Vector3(box.center.x, box.min.y, box.center.z);
                    var aim = foot + shadowDirection * (sun ? .65f : .2f);
                    Vector3 center = sun ? aim + Vector3.up * 3.1f + Vector3.Cross(shadowDirection, Vector3.up) * 2.4f
                        : InteriorWitnessPosition(foot);
                    var rotation = Quaternion.LookRotation(aim - center, Vector3.up);
                    var midpoint = (optics.left.pose.Position + optics.right.pose.Position) * .5f;
                    var eyePositions = new Vector3[2];
                    var viewMatrices = new Matrix4x4[2];
                    var eyeViews = new[] { optics.left, optics.right };
                    for (int eye = 0; eye < 2; eye++)
                    {
                        var view = eyeViews[eye];
                        var relative = Quaternion.Inverse(optics.left.pose.Rotation);
                        Vector3 world = center + rotation * (relative * (view.pose.Position - midpoint));
                        Quaternion worldRotation = rotation * relative * view.pose.Rotation;
                        var origin = stereo.TrackingOrigin;
                        var local = origin.InverseTransformPoint(world);
                        var localRotation = Quaternion.Inverse(origin.rotation) * worldRotation;
                        object boxed = view.pose;
                        positionField.SetValue(boxed, new Vector3(local.x, local.y, -local.z));
                        rotationField.SetValue(boxed, new Quaternion(-localRotation.x, -localRotation.y, localRotation.z, localRotation.w));
                        view.pose = (MetalOpenXR.Pose)boxed;
                        eyeViews[eye] = view;
                        eyePositions[eye] = world;
                        var camera = stereo.SourceCamera;
                        viewMatrices[eye] = (view.Projection(camera.nearClipPlane, camera.farClipPlane) *
                            Matrix4x4.Scale(new Vector3(1, 1, -1)) *
                            Matrix4x4.TRS(world, worldRotation, Vector3.one).inverse).inverse;
                    }

                    var captures = new Color32[3][][];
                    var contactDraws = new int?[3];
                    for (int state = 0; state < 3; state++)
                    {
                        bool enabledState = state == 1;
                        if (hasContact) options.Set("CharacterShadows", contact && enabledState ? 1 : 0);
                        for (int i = 0; i < parts.Length; i++)
                            parts[i].shadowCastingMode = contact || enabledState ? original[i] : ShadowCastingMode.Off;
                        captures[state] = new Color32[2][];
                        for (int frame = 0; frame <= 12; frame++)
                        {
                            yield return new WaitForEndOfFrame();
                            for (int eye = 0; eye < 2; eye++)
                            {
                                render.Invoke(stereo, new object[] { eye, eyeViews[eye], target });
                                if (frame != 12) continue;
                                string condition = state == 0 ? "off-before" : enabledState ? "stock" : "off";
                                captures[state][eye] = CaptureSosigCrop(target, tile,
                                    Path.Combine(output, $"sosig-{movement}-{eye}-{condition}.png"));
                                if (movement == 0 && eye == 0 && state != 0)
                                    CaptureSosigOverview(target, Path.Combine(output, $"sosig-overview-{condition}.png"));
                            }
                        }
                        contactDraws[state] = (int?)lastContactDraws?.GetValue(null);
                    }

                    if (contact && (contactDraws[1].GetValueOrDefault() == 0 ||
                        contactDraws[0].GetValueOrDefault() != 0 || contactDraws[2].GetValueOrDefault() != 0))
                        failures.Add($"Pose {movement}: Scene/Enhanced/Scene drew {string.Join("/", contactDraws)} actors");
                    for (int eye = 0; eye < 2; eye++)
                    {
                        int points = 0, unstableControls = 0, darkened = 0, inverted = 0;
                        float maximum = 0f;
                        for (int y = 2; y < SosigCrop; y += 4)
                        for (int x = 2; x < SosigCrop; x += 4)
                        {
                            float ndcX = 2f * ((target.width - SosigCrop) * .5f + x) / target.width - 1f;
                            float ndcY = 2f * ((target.height - SosigCrop) * .5f + y) / target.height - 1f;
                            var projected = viewMatrices[eye] * new Vector4(ndcX, ndcY, 0, 1);
                            var point = new Vector3(projected.x, projected.y, projected.z) / projected.w;
                            if (!Physics.Raycast(eyePositions[eye], (point - eyePositions[eye]).normalized, out var hit,
                                    12f, ~0, QueryTriggerInteraction.Ignore) || hit.normal.y < .75f ||
                                hit.collider.GetComponentInParent<Sosig>() || hit.collider.GetComponentInParent<SosigLink>() ||
                                Mathf.Abs(hit.point.y - aim.y) > 1.2f ||
                                Vector3.Distance(hit.point, aim) > 2.5f)
                                continue;
                            points++;
                            int index = y * SosigCrop + x;
                            float before = SosigLuma(captures[0][eye][index]);
                            float after = SosigLuma(captures[2][eye][index]);
                            if (Mathf.Abs(before - after) > 4f)
                            {
                                unstableControls++;
                                continue;
                            }
                            float delta = (before + after) * .5f - SosigLuma(captures[1][eye][index]);
                            if (delta >= 8f) darkened++;
                            if (delta <= -8f) inverted++;
                            maximum = Mathf.Max(maximum, delta);
                        }
                        samples.Add(new { movement, eye, aim = new { aim.x, aim.y, aim.z }, points, unstableControls, darkened, inverted, maximum, contactDraws });
                        if (points - unstableControls < 20 || ((!contact || !sun) && (darkened < 6 || darkened < inverted * 2)))
                            failures.Add($"Pose {movement}, eye {eye}: {darkened} darkened and {inverted} brightened of {points} visible ground points");
                    }
                }
            }
            finally
            {
                if (hasContact) options.Set("CharacterShadows", originalContact);
                for (int i = 0; i < parts.Length; i++)
                    if (parts[i]) parts[i].shadowCastingMode = original[i];
                if (pose) pose.SetPositionAndRotation(originalPosition, originalRotation);
                for (int i = 0; i < links.Length; i++)
                    if (links[i]) links[i].transform.SetPositionAndRotation(linkPoses[i].position, linkPoses[i].rotation);
                Physics.SyncTransforms();
                Time.timeScale = timeScale;
                stereo.SuspendSceneRendering = suspended;
                target.Release();
                Destroy(target);
                Destroy(tile);
            }

            var asset = GraphicsSettings.currentRenderPipeline;
            File.WriteAllText(Path.Combine(output, "sosig-shadow.json"), JsonConvert.SerializeObject(new
            {
                mode = contact ? "contact-only" : "native-caster-flags",
                scene = actor.gameObject.scene.name, actor = actor.name, initialPose = new { originalPosition.x, originalPosition.y, originalPosition.z },
                preset = options.Label("Preset"), shadowDistance = options.Label("ShadowDistance"),
                shadowDetail = options.Label("ShadowQuality"), pipeline = asset.name,
                directionalSun = sun ? sun.name : null,
                activeLights = FindObjectsByType<Light>().Where(light => light.isActiveAndEnabled).GroupBy(light => new
                    { type = light.type.ToString(), shadows = light.shadows.ToString(), mode = light.bakingOutput.lightmapBakeType.ToString() })
                    .Select(group => new { group.Key, count = group.Count() }),
                candidates = candidates.Select(unit => new
                {
                    selected = unit == actor,
                    floorClearance = GroundClearance(unit),
                    position = new { unit.transform.position.x, unit.transform.position.y, unit.transform.position.z }
                }),
                renderers = parts.Select((part, i) => new
                {
                    part.name, path = SosigRendererPath(part.transform), kind = part.GetType().Name,
                    casting = original[i].ToString(), part.receiveShadows, part.enabled, part.gameObject.activeInHierarchy,
                    layer = part.gameObject.layer, part.renderingLayerMask, bodySegment = Array.IndexOf(actor.Renderers, part),
                    materials = part.sharedMaterials.Where(material => material && material.shader).Select(material =>
                        new { material.name, shader = material.shader.name, material.renderQueue,
                            shadowCaster = material.FindPass("ShadowCaster"), shadowCasterEnabled = material.GetShaderPassEnabled("ShadowCaster") }),
                }),
                samples, failures, passed = failures.Count == 0
            }, Formatting.Indented));
            Require(failures.Count == 0, string.Join("; ", failures));
        }

        private static int GroundClearance(Sosig actor)
        {
            var feet = actor.transform.position;
            int score = 0;
            for (int i = 0; i < 8; i++)
            {
                var direction = Quaternion.AngleAxis(i * 45f, Vector3.up) * Vector3.forward;
                if (Physics.Raycast(feet + direction * .85f + Vector3.up * .4f, Vector3.down, out var floor,
                        .74f, ~0, QueryTriggerInteraction.Ignore) &&
                    floor.normal.y >= .9f && !floor.collider.attachedRigidbody &&
                    !Physics.RaycastAll(feet + Vector3.up * .2f, direction, .85f, ~0,
                        QueryTriggerInteraction.Ignore).Any(hit => !hit.collider.attachedRigidbody))
                    score++;
            }
            return score;
        }

        private static Vector3 InteriorWitnessPosition(Vector3 foot)
        {
            var above = foot + Vector3.up * 1.15f;
            var ideal = foot + Vector3.up * 2.8f;
            float largest = 0f;
            Vector3 center = ideal;
            for (int i = 0; i < 8; i++)
            {
                var candidate = ideal + Quaternion.AngleAxis(i * 45f, Vector3.up) * Vector3.forward * 1.7f;
                var from = candidate - above;
                var distance = from.magnitude;
                foreach (var hit in Physics.RaycastAll(above, from / distance, distance, ~0, QueryTriggerInteraction.Ignore))
                    if (!hit.collider.attachedRigidbody) distance = Mathf.Min(distance, hit.distance);
                if (distance <= largest) continue;
                largest = distance;
                center = above + from.normalized * Mathf.Max(.3f, distance - .18f);
            }
            return center;
        }

        private static string SosigRendererPath(Transform part)
        {
            var names = new List<string>();
            for (var parent = part; parent; parent = parent.parent) names.Add(parent.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private static void CaptureSosigOverview(RenderTexture target, string path)
        {
            var previous = RenderTexture.active;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                image.Apply(false);
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(image);
            }
        }

        private static Color32[] CaptureSosigCrop(RenderTexture target, Texture2D tile, string path)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                tile.ReadPixels(new Rect((target.width - SosigCrop) / 2, (target.height - SosigCrop) / 2,
                    SosigCrop, SosigCrop), 0, 0, false);
                tile.Apply(false);
                File.WriteAllBytes(path, tile.EncodeToPNG());
                return tile.GetPixels32();
            }
            finally { RenderTexture.active = previous; }
        }

        private static float SosigLuma(Color32 pixel) => .2126f * pixel.r + .7152f * pixel.g + .0722f * pixel.b;
    }
}
