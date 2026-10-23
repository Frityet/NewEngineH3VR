using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR;
namespace NativeH3VR {
public static class Compatibility {
    public static byte[] EncodeToPNG(Texture2D texture) {
        return ImageConversion.EncodeToPNG(texture);
    }
    public static byte[] EncodeToEXR(Texture2D texture) {
        return ImageConversion.EncodeToEXR(texture, Texture2D.EXRFlags.None);
    }
    public static bool LoadImage(Texture2D texture, byte[] data) {
        return ImageConversion.LoadImage(texture, data);
    }
    public static bool IsWebRequestError(UnityWebRequest request) {
        return request.isNetworkError || request.isHttpError;
    }
    public static string GetVRModel() {
        return InputDevices.GetDeviceAtXRNode(XRNode.Head).name ?? string.Empty;
    }
    public static Delegate CreateDelegate(Type type, MethodInfo method) {
        return Delegate.CreateDelegate(type, method);
    }
    static List<Vector4> To4(List<Vector2> input) {
        if (input == null)
            return null;
        var result = new List<Vector4>(input.Count);
        foreach (var v in input)
            result.Add(v);
        return result;
    }
    static void Copy2(List<Vector4> input, List<Vector2> output) {
        if (input == null || output == null)
            return;
        output.Clear();
        foreach (var v in input)
            output.Add(v);
    }
    public static void AddUIVertexStream(List<UIVertex> vertices, List<Vector3> positions,
                                         List<Color32> colors, List<Vector2> uv0, List<Vector2> uv1,
                                         List<Vector3> normals, List<Vector4> tangents) {
        var a = To4(uv0);
        var b = To4(uv1);
        CanvasRenderer.AddUIVertexStream(vertices, positions, colors, a, b, normals, tangents);
        Copy2(a, uv0);
        Copy2(b, uv1);
    }
    public static void SplitUIVertexStreams(List<UIVertex> vertices, List<Vector3> positions,
                                            List<Color32> colors, List<Vector2> uv0,
                                            List<Vector2> uv1, List<Vector3> normals,
                                            List<Vector4> tangents, List<int> indices) {
        var a = To4(uv0);
        var b = To4(uv1);
        CanvasRenderer.SplitUIVertexStreams(vertices, positions, colors, a, b, normals, tangents,
                                            indices);
        Copy2(a, uv0);
        Copy2(b, uv1);
    }
    public static void CreateUIVertexStream(List<UIVertex> vertices, List<Vector3> positions,
                                            List<Color32> colors, List<Vector2> uv0,
                                            List<Vector2> uv1, List<Vector3> normals,
                                            List<Vector4> tangents, List<int> indices) {
        CanvasRenderer.CreateUIVertexStream(vertices, positions, colors, To4(uv0), To4(uv1),
                                            normals, tangents, indices);
    }
}
}
