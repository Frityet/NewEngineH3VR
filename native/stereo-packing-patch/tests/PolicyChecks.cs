using System;
namespace NativeH3VR {
public static class NativeEyeProjection {
    public static bool IsRenderingEye;
}
}
static class PolicyChecks {
    static void Require(bool value) {
        if (!value)
            throw new Exception("Scoped stereo policy failed");
    }
    static void Main(string[] args) {
        string value = Environment.GetEnvironmentVariable("NEWENGINEH3VR_PACKED_STEREO_FIX");
        bool invalid = !string.IsNullOrEmpty(value) && value != "0" && value != "1";
        if (invalid) {
            try {
                bool ignored = NativeH3VR.NewEnginePostProcessing.Enabled;
            } catch (TypeInitializationException e) {
                Require(e.InnerException is InvalidOperationException);
                Console.WriteLine("PASS invalid configuration rejected");
                return;
            }
            throw new Exception("Invalid configuration accepted");
        }
        bool enabled = value != "0";
        Require(NativeH3VR.NewEnginePostProcessing.Enabled == enabled);
        Require(NativeH3VR.NewEnginePostProcessing.IsPackedStereo);
        NativeH3VR.NativeEyeProjection.IsRenderingEye = true;
        Require(NativeH3VR.NewEnginePostProcessing.IsPackedStereo == !enabled);
        NativeH3VR.NativeEyeProjection.IsRenderingEye = false;
        Require(NativeH3VR.NewEnginePostProcessing.IsPackedStereo);
        Environment.SetEnvironmentVariable("NEWENGINEH3VR_PACKED_STEREO_FIX", enabled ? "0" : "1");
        Require(NativeH3VR.NewEnginePostProcessing.Enabled == enabled);
        NativeH3VR.NativeEyeProjection.IsRenderingEye = true;
        Require(NativeH3VR.NewEnginePostProcessing.IsPackedStereo == !enabled);
        Require(NativeH3VR.NewEnginePostProcessing.Describe().Contains(
            enabled ? "correction enabled" : "diagnostic baseline"));
        Console.WriteLine("PASS 7 policy assertions: " + (enabled ? "correction" : "baseline"));
    }
}
