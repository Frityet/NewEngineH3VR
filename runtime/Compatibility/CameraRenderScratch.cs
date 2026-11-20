using System;
using UnityEngine;

namespace NativeH3VR
{
    public static class CameraRenderScratch
    {
        [ThreadStatic]
        private static Matrix4x4[] eyeMatrices;

        // The stock callback overwrites both matrices and submits them to Unity,
        // which copies the values before the callback starts rendering scope cameras.
        public static Matrix4x4[] EyeMatrices() => eyeMatrices ?? (eyeMatrices = new Matrix4x4[2]);

        public static void ClearOrCreate(ref MaterialPropertyBlock block)
        {
            if (block == null) block = new MaterialPropertyBlock();
            else block.Clear();
        }
    }
}
