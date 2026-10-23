#if NEWENGINE_UNITY_2021
// Unity 2021 graphics integration only. CPU optimizations remain in OptiH3VR.
namespace NativeH3VR {
public static class NewEnginePostProcessing {
    static readonly bool enabled = ReadEnabled();
    public static bool Enabled {
        get { return enabled; }
    }
    static bool ReadEnabled() {
        string value = System.Environment.GetEnvironmentVariable("NEWENGINEH3VR_PACKED_STEREO_FIX");
        if (string.IsNullOrEmpty(value) || value == "1")
            return true;
        if (value == "0")
            return false;
        throw new System.InvalidOperationException(
            "NEWENGINEH3VR_PACKED_STEREO_FIX must be 0 or 1 (unset defaults to 1)");
    }
    public static string Describe() {
        return enabled ? "packed-stereo: scoped separate-eye correction enabled"
                       : "packed-stereo: original constant-true policy (diagnostic baseline)";
    }
    // The recovered Windows player compiled this policy to constant true
    // for packed single-pass stereo. Our bounded eye render uses a separate
    // full-size target per eye; retain the original policy everywhere else.
    public static bool IsPackedStereo {
        get { return !enabled || !NativeEyeProjection.IsRenderingEye; }
    }
}
}
#endif
