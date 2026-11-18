using System;
using System.Collections;
using NativeH3VR;
using UnityEngine;

public sealed class MetalSmoke : MonoBehaviour
{
    NativeStereoRenderer renderer;
    readonly WaitForSecondsRealtime sample = new WaitForSecondsRealtime(.05f);

    IEnumerator Start()
    {
        if (Environment.GetEnvironmentVariable("VR_OPENXR_SIMULATE") != "1" ||
            Environment.GetEnvironmentVariable("VR_OPENXR_HEADLESS") != "1")
        {
            Debug.LogError("UNITY_METAL_SMOKE requires explicit headless simulation");
            Application.Quit(1);
            yield break;
        }

        renderer = NativeStereoRenderer.Install();
        renderer.BindCamera(GetComponent<Camera>(), transform.parent);
        Debug.Log("UNITY_METAL_SAVE_ROOT " + Application.persistentDataPath);
        double deadline = Time.realtimeSinceStartupAsDouble + 60;
        int frameGoal = 180;
        string requested = Environment.GetEnvironmentVariable("NATIVEH3VR_SMOKE_FRAMES");
        if (!string.IsNullOrEmpty(requested)) frameGoal = int.Parse(requested);
        while (Time.realtimeSinceStartupAsDouble < deadline && renderer.CompletedFrames < (ulong)frameGoal)
            yield return sample;

        var frame = renderer.LatestFrame;
        float ipd = Vector3.Distance(frame.left.pose.Position, frame.right.pose.Position);
        var left = renderer.EyeTexture(0);
        var right = renderer.EyeTexture(1);
        bool leftFound = MetalOpenXR.TryHand(0, out var leftHand);
        bool rightFound = MetalOpenXR.TryHand(1, out var rightHand);
        bool haptic = MetalOpenXR.Haptic(0, .25f, 80, .01f) &&
                      MetalOpenXR.Haptic(1, .25f, 0, .01f);
        bool stage = MetalOpenXR.nhxr_stage_bounds(out float roomWidth, out float roomDepth) == 0 &&
                     Mathf.Abs(roomWidth - 2) < .001f && Mathf.Abs(roomDepth - 2) < .001f;
        float leftMarker = left == null ? -1 : MarkerX(left);
        float rightMarker = right == null ? -1 : MarkerX(right);
        float headHeight = frame.head.Position.y;
        bool passed = renderer.CompletedFrames >= (ulong)frameGoal && renderer.Focused && haptic && stage &&
                      ipd > .06f && ipd < .07f && Mathf.Abs(headHeight - 1.6f) < .03f &&
                      leftFound && rightFound && (leftHand.flags & 19) == 19 && (rightHand.flags & 19) == 19 &&
                      leftHand.grip.Position.x < 0 && rightHand.grip.Position.x > 0 &&
                      leftMarker > 0 && rightMarker > 0 && leftMarker - rightMarker >= 2;

        Debug.Log(FormattableString.Invariant(
            $"UNITY_METAL_SMOKE passed={passed} completed={renderer.CompletedFrames} focus={renderer.Focused} haptics={haptic} stage={stage} ipd={ipd:F4} headHeight={headHeight:F4} leftMarkerX={leftMarker:F2} rightMarkerX={rightMarker:F2} leftFlags={leftHand.flags} rightFlags={rightHand.flags}"));
        Application.Quit(passed ? 0 : 1);
    }

    static float MarkerX(RenderTexture source)
    {
        var previous = RenderTexture.active;
        var reader = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        try
        {
            RenderTexture.active = source;
            reader.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            Color32[] pixels = reader.GetPixels32();
            long count = 0, sum = 0;
            for (int y = 0; y < source.height; y++)
            for (int x = 0; x < source.width; x++)
            {
                var color = pixels[y * source.width + x];
                if (color.r < 160 || color.g > 90 || color.b > 90) continue;
                count++;
                sum += x;
            }
            return count > 32 ? (float)sum / count : -1;
        }
        finally
        {
            RenderTexture.active = previous;
            Destroy(reader);
        }
    }
}
