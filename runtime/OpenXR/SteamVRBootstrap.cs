using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace NativeH3VR
{
    // Preserves the game's SteamVR-facing initialization surface while leaving
    // session creation, stereo rendering, and frame pacing to Unity XR.
    public static class NativeSteamVRBootstrap
    {
        static readonly Dictionary<Type, object> interfaces = new Dictionary<Type, object>();
        public static bool Enabled { get { return XRBridge.IsRunning; } }

        public static T Interface<T>() where T : class
        {
            if (!interfaces.TryGetValue(typeof(T), out object value))
            {
                value = FormatterServices.GetUninitializedObject(typeof(T));
                interfaces.Add(typeof(T), value);
            }
            return (T)value;
        }

        static void Set(Type owner, object target, string name, object value)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance |
                                       BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = owner.GetField(name, flags) ??
                              owner.GetField("<" + name + ">k__BackingField", flags);
            if (field == null)
                throw new MissingFieldException(owner.FullName, name);
            field.SetValue(target, value);
        }

        public static SteamVR Create()
        {
            if (!XRBridge.IsRunning)
                throw new InvalidOperationException(
                    "Unity XR has no active device. Configure and start a Unity XR provider before initializing the game.");

            SteamVR instance = Interface<SteamVR>();
            Set(typeof(SteamVR), null, "_instance", instance);
            Set(typeof(SteamVR), null, "_enabled", true);
            Set(typeof(SteamVR), null, "settings", SteamVR_Settings.instance);
            Set(typeof(SteamVR), instance, "hmd", Interface<CVRSystem>());
            Set(typeof(SteamVR), instance, "compositor", Interface<CVRCompositor>());
            Set(typeof(SteamVR), instance, "overlay", Interface<CVROverlay>());
            Set(typeof(SteamVR), instance, "sceneWidth", (float)XRBridge.EyeWidth);
            Set(typeof(SteamVR), instance, "sceneHeight", (float)XRBridge.EyeHeight);
#if NATIVEH3VR_METAL
            Vector4 leftFov = XRBridge.Metal.Fov(0);
            Vector4 rightFov = XRBridge.Metal.Fov(1);
            float halfX = Mathf.Max(-Mathf.Tan(leftFov.x), Mathf.Tan(leftFov.y),
                                    -Mathf.Tan(rightFov.x), Mathf.Tan(rightFov.y));
            float halfY = Mathf.Max(Mathf.Tan(leftFov.z), -Mathf.Tan(leftFov.w),
                                    Mathf.Tan(rightFov.z), -Mathf.Tan(rightFov.w));
            Set(typeof(SteamVR), instance, "tanHalfFov", new Vector2(halfX, halfY));
            Set(typeof(SteamVR), instance, "aspect", halfY > 0 ? halfX / halfY : 1);
            Set(typeof(SteamVR), instance, "fieldOfView", 2 * Mathf.Atan(halfY) * Mathf.Rad2Deg);
            Set(typeof(SteamVR), instance, "textureBounds", new[]
            {
                new VRTextureBounds_t { uMax = 1, vMax = 1 },
                new VRTextureBounds_t { uMax = 1, vMax = 1 }
            });
            var offsets = new SteamVR_Utils.RigidTransform[2];
            for (int eye = 0; eye < offsets.Length; eye++)
            {
                XRBridge.Pose offset = XRBridge.Metal.EyeOffset(eye);
                offsets[eye] = new SteamVR_Utils.RigidTransform(offset.position, offset.rotation);
            }
            Set(typeof(SteamVR), instance, "eyes", offsets);
            instance.textureType = ETextureType.Metal;
#endif
            NativeSteamVRInput.Synthetic = Environment.GetEnvironmentVariable("VR_OPENXR_SIMULATE") == "1";
            string manifest = ResolveActionManifest();
            EVRInputError inputError = NativeSteamVRInput.SetActionManifestPath(manifest);
            if (inputError != EVRInputError.None)
                throw new InvalidOperationException("Could not load the game's SteamVR action manifest: " + manifest + " (" + inputError + ")");
            SteamVR_Settings.instance.trackingSpace = ETrackingUniverseOrigin.TrackingUniverseStanding;
            SteamVR_Input.Initialize();
            foreach (SteamVR_Action_Skeleton skeleton in SteamVR_Input.actionsSkeleton)
                skeleton.onlyUpdateSummaryData = true;
            NativeSteamVRSystem.Running = true;
            SteamVR.initializedState = SteamVR.InitializedStates.InitializeSuccess;
            SteamVR_Events.Initialized.Send(true);
            Debug.Log("H3VR_STEAMVR_ADAPTER system=" + XRBridge.SystemName);
            return instance;
        }

        public static string ResolveLegacyHeadsetModel(string engineModel)
        {
            return XRBridge.IsRunning ? XRBridge.SystemName : engineModel;
        }

#if NATIVEH3VR_METAL
        public static void CameraEnabled(SteamVR_Camera camera)
        {
            if (XRBridge.Metal == null || camera == null) return;
            Transform origin = camera.head != null ? camera.origin : camera.transform.parent;
            if (origin == null) throw new InvalidOperationException("SteamVR camera has no tracking origin.");
            XRBridge.Metal.BindCamera(camera.GetComponent<Camera>(), origin);
        }

        public static void ResetEyeProjection(Camera camera)
        {
            if (XRBridge.Metal != null) XRBridge.Metal.RestoreProjection(camera);
            else camera.ResetProjectionMatrix();
        }

        public static bool IsEyeCamera(Camera camera) => XRBridge.Metal?.IsEyeCamera(camera) == true;
#endif

        static string ResolveActionManifest()
        {
            string root = Application.streamingAssetsPath;
            string[] candidates = {
                Path.Combine(root, "SteamVR", "actions.json"),
                Path.Combine(root, "actions.json")
            };
            foreach (string candidate in candidates)
                if (File.Exists(candidate))
                    return candidate;
            throw new FileNotFoundException(
                "The migrated project is missing SteamVR/actions.json and its referenced bindings under StreamingAssets.",
                candidates[0]);
        }

        public static IEnumerator EmptyRenderCoroutine() { yield break; }

        public static bool IsHmdPresent() { return XRBridge.IsRunning; }
        public static string GetStringForHmdError(EVRInitError error) { return error.ToString(); }
        public static void Unsupported(string feature)
        {
            throw new NotSupportedException("The Unity XR adapter does not implement " + feature);
        }
    }
}
