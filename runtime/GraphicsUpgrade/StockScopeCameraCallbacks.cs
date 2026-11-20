using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NativeH3VR.GraphicsUpgrade
{
    internal static class StockScopeCameraCallbacks
    {
        private static readonly Dictionary<Camera, PIPScope_Camera> Rendering = new();
        private static readonly ConditionalWeakTable<PIPScope_MainCamera, MainCameraCallbacks> MainCallbacks = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            RenderPipelineManager.beginCameraRendering -= Begin;
            RenderPipelineManager.endCameraRendering -= End;
            Rendering.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            RenderPipelineManager.beginCameraRendering += Begin;
            RenderPipelineManager.endCameraRendering += End;
        }

        private static void Begin(ScriptableRenderContext context, Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset ||
                camera.cameraType != CameraType.Game ||
                !camera.TryGetComponent<PIPScope_Camera>(out var scope) ||
                !scope.isActiveAndEnabled || scope.cam != camera ||
                !Rendering.TryAdd(camera, scope))
                return;

            try
            {
                // Main PreCull recursively renders scope targets before this camera
                // starts culling. A distinct entry pairs each nested camera's cleanup.
                if (scope is PIPScope_MainCamera main)
                    MainCallbacks.GetValue(main, BindMainCallbacks).PreCull();
                else
                    scope.PreCull();
                scope.PreRender();
            }
            catch
            {
                End(context, camera);
                throw;
            }
        }

        private static void End(ScriptableRenderContext context, Camera camera)
        {
            if (!Rendering.Remove(camera, out var scope) || !scope)
                return;
            if (scope is PIPScope_MainCamera main)
                MainCallbacks.GetValue(main, BindMainCallbacks).PostRender();
            else
                scope.PostRender();
        }

        private static MainCameraCallbacks BindMainCallbacks(PIPScope_MainCamera main) => new(main);

        private sealed class MainCameraCallbacks
        {
            private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            private static readonly MethodInfo PreCullMethod = typeof(PIPScope_MainCamera).GetMethod("OnPreCull", Flags);
            private static readonly MethodInfo PostRenderMethod = typeof(PIPScope_MainCamera).GetMethod("OnPostRender", Flags);

            internal readonly Action PreCull;
            internal readonly Action PostRender;

            internal MainCameraCallbacks(PIPScope_MainCamera main)
            {
                PreCull = (Action)PreCullMethod.CreateDelegate(typeof(Action), main);
                PostRender = (Action)PostRenderMethod.CreateDelegate(typeof(Action), main);
            }
        }
    }
}
