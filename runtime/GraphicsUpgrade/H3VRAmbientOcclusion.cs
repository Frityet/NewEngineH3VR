using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NativeH3VR.GraphicsUpgrade
{
    public sealed class H3VRAmbientOcclusion : ScreenSpaceAmbientOcclusion
    {
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (GraphicsOptions.Occlusion && H3VREyeRendering.IsPlayerView(renderingData.cameraData.camera))
                base.AddRenderPasses(renderer, ref renderingData);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InstallVolume()
        {
            if (!(GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset)) return;
            var host = new GameObject("H3VR ambient occlusion");
            DontDestroyOnLoad(host);
            var volume = host.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = -1;
            var occlusion = volume.profile.Add<ScreenSpaceAmbientOcclusionVolumeOverride>(true);
            occlusion.mode = ScreenSpaceAmbientOcclusionMode.SSAO;
            occlusion.quality = ScreenSpaceAmbientOcclusionQuality.Custom;
            occlusion.intensity = 1.1f;
            occlusion.radius = 0.22f;
            occlusion.falloffDistance = 18f;
            occlusion.afterOpaque = true;
            occlusion.downsample = true;
            occlusion.method = ScreenSpaceAmbientOcclusionNoiseMethod.InterleavedGradient;
            occlusion.depthSource = ScreenSpaceAmbientOcclusionDepthSource.Depth;
            occlusion.normalQuality = ScreenSpaceAmbientOcclusionNormalQuality.Medium;
            occlusion.sampleCount = ScreenSpaceAmbientOcclusionSampleCount.Low;
            occlusion.blurQuality = ScreenSpaceAmbientOcclusionBlurQuality.Medium;
        }
    }
}
