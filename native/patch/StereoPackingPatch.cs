using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class StereoPackingPatch {
    internal const string TargetType = "UnityEngine.Rendering.PostProcessing.RuntimeUtilities";
    internal const string TargetName = "get_isSinglePassStereoEnabled";
    internal const string HelperType = "NativeH3VR.NewEnginePostProcessing";
    internal const string HelperName = "get_IsPackedStereo";

    internal static MethodDefinition Apply(AssemblyDefinition game, AssemblyDefinition xr) {
        if (game.Name.Name != "NativeH3VR.Game" || xr.Name.Name != "NativeH3VR.XR.Managed")
            throw new InvalidOperationException(
                "Expected the existing Game/XR assembly identities");
        var helper =
            xr.MainModule.GetType(HelperType)?.Methods.SingleOrDefault(m => m.Name == HelperName) ??
            throw new InvalidOperationException("Unity 2021 scoped stereo helper is absent");
        if (!helper.IsPublic || !helper.IsStatic || helper.Parameters.Count != 0 ||
            helper.ReturnType.FullName != "System.Boolean")
            throw new InvalidOperationException("Scoped stereo helper signature changed");
        var target = game.MainModule.GetType(TargetType)
                         ?.Methods.SingleOrDefault(m => m.Name == TargetName) ??
                     throw new InvalidOperationException("Original packed-stereo policy is absent");
        if (!target.IsStatic || target.Parameters.Count != 0 ||
            target.ReturnType.FullName != "System.Boolean" || !target.HasBody ||
            target.Body.ExceptionHandlers.Count != 0 || target.Body.Variables.Count != 0 ||
            target.Body.Instructions.Count != 2 ||
            target.Body.Instructions[0].OpCode != OpCodes.Ldc_I4_1 ||
            target.Body.Instructions[1].OpCode != OpCodes.Ret)
            throw new InvalidOperationException("Expected exact constant-true packed-stereo " +
                                                "getter; refusing changed/already-patched IL");
        var entry = target.Body.Instructions[0];
        entry.OpCode = OpCodes.Call;
        entry.Operand = game.MainModule.ImportReference(helper);
        return target;
    }
}
