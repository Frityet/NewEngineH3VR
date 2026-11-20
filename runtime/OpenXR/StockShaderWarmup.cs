using UnityEngine;
using Valve.VR;

namespace NativeH3VR
{
    public sealed class StockShaderWarmup : MonoBehaviour
    {
        private const string StockCollectionName = "H3_ShaderAsset3";
        private static StockShaderWarmup driver;
        private ShaderVariantCollection pending;
        private int lastSliceFrame = -1;

        public static void Queue(ShaderVariantCollection collection)
        {
            // Keep original behavior if a scene or a mod supplies a different collection.
            if (!collection || collection.name != StockCollectionName)
            {
                collection.WarmUp();
                return;
            }
            if (collection.isWarmedUp)
                return;
            if (!driver)
            {
                var host = new GameObject("NativeH3VR stock shader warmup");
                DontDestroyOnLoad(host);
                driver = host.AddComponent<StockShaderWarmup>();
            }
            if (driver.pending && driver.pending != collection)
            {
                collection.WarmUp();
                return;
            }
            driver.pending = collection;
            driver.enabled = true;
        }

        private void LateUpdate()
        {
            if (!pending || pending.isWarmedUp)
            {
                pending = null;
                enabled = false;
                return;
            }
            if (!SteamVR_LoadLevel.loading || !SteamVR_Render.pauseRendering || lastSliceFrame == Time.frameCount)
                return;

            lastSliceFrame = Time.frameCount;
            if (pending.WarmUpProgressively(1))
            {
                pending = null;
                enabled = false;
            }
        }

        private void OnDestroy()
        {
            if (driver == this)
                driver = null;
        }
    }
}
