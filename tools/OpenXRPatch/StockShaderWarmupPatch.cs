using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class StockShaderWarmupPatch
{
    internal static bool Apply(ModuleDefinition game, ModuleDefinition adapter)
    {
        const string collection = "UnityEngine.ShaderVariantCollection";
        var manager = game.GetType("FistVR.GM");
        var awake = manager?.Methods.SingleOrDefault(m =>
            m.Name == "Awake" && m.HasBody && m.HasThis && m.Parameters.Count == 0);
        if (awake == null)
            return false;
        var calls = awake.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt &&
            i.Operand is MethodReference call &&
            call.DeclaringType.FullName == collection && call.Name == "WarmUp" &&
            call.HasThis && call.Parameters.Count == 0 && call.ReturnType.FullName == "System.Void").ToArray();
        if (calls.Length != 1)
            return false;
        var callSite = calls[0];
        var fieldLoad = callSite.Previous;
        if (fieldLoad?.OpCode != OpCodes.Ldfld ||
            fieldLoad.Operand is not FieldReference field ||
            field.DeclaringType.FullName != "FistVR.GM" || field.Name != "ShaderVariants" ||
            field.FieldType.FullName != collection || fieldLoad.Previous?.OpCode != OpCodes.Ldarg_0)
            return false;

        var queue = adapter.GetType("NativeH3VR.StockShaderWarmup").Methods.Single(m =>
            m.Name == "Queue" && m.IsStatic && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == collection && m.ReturnType.FullName == "System.Void");
        callSite.OpCode = OpCodes.Call;
        callSite.Operand = game.ImportReference(queue);
        return true;
    }
}
