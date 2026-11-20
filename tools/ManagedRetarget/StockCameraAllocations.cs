using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class StockCameraAllocations
{
    internal static void Apply(ModuleDefinition module, TypeDefinition scratch, List<object> log)
    {
        var main = module.GetType("PIPScope_MainCamera")?.Methods.SingleOrDefault(m => m.Name == "OnPreCull" && m.HasBody);
        if (main != null && ReuseEyeMatrices(main, scratch))
            log.Add(new { caller = main.FullName, target = "Reuse eye matrix scratch" });

        var relative = module.GetType("CameraRelativeRenderer")?.Methods.SingleOrDefault(m => m.Name == "EnableAll" && m.HasBody);
        if (relative != null && DeferPropertyBlock(relative, scratch))
            log.Add(new { caller = relative.FullName, target = "Allocate a property block only on write" });
    }

    private static bool ReuseEyeMatrices(MethodDefinition method, TypeDefinition scratch)
    {
        var body = method.Body;
        var allocation = body.Instructions.SingleOrDefault(i => i.OpCode == OpCodes.Newarr &&
            i.Operand is TypeReference type && type.FullName == "UnityEngine.Matrix4x4");
        var submit = body.Instructions.SingleOrDefault(i => Calls(i, "UnityEngine.Shader", "SetGlobalMatrixArray"));
        if (allocation?.Previous?.OpCode != OpCodes.Ldc_I4_2 || allocation.Next?.OpCode != OpCodes.Stloc_0 ||
            submit?.Previous?.OpCode != OpCodes.Ldloc_0 || allocation.Offset >= submit.Offset ||
            body.Variables[0].VariableType.FullName != "UnityEngine.Matrix4x4[]" ||
            body.Instructions.Any(i => i.Offset > submit.Offset && i.OpCode == OpCodes.Ldloc_0) ||
            body.Instructions.Any(i => i.Offset > allocation.Offset && i.Offset < submit.Offset &&
                i.Operand is MethodReference call && call.DeclaringType.FullName is not ("UnityEngine.Matrix4x4" or "UnityEngine.Camera")))
            return false;

        // Both original array stores and the native copy remain exactly where they
        // were. No scope callback can re-enter before the copy.
        allocation.Previous.OpCode = OpCodes.Nop;
        allocation.OpCode = OpCodes.Call;
        allocation.Operand = method.Module.ImportReference(scratch.Methods.Single(m => m.Name == "EyeMatrices"));
        return true;
    }

    private static bool DeferPropertyBlock(MethodDefinition method, TypeDefinition scratch)
    {
        const string blockType = "UnityEngine.MaterialPropertyBlock";
        var body = method.Body;
        var allocation = body.Instructions.SingleOrDefault(i => i.OpCode == OpCodes.Newobj && Calls(i, blockType, ".ctor"));
        var clear = body.Instructions.SingleOrDefault(i => Calls(i, blockType, "Clear"));
        var firstRead = body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ldloc_0);
        if (allocation?.Next?.OpCode != OpCodes.Stloc_0 || clear == null || firstRead == null || clear.Previous != firstRead ||
            body.Variables[0].VariableType.FullName != blockType ||
            !Calls(clear.Next?.Next?.Next?.Next?.Next?.Next, "UnityEngine.Renderer", "GetPropertyBlock"))
            return false;

        allocation.OpCode = OpCodes.Ldnull;
        allocation.Operand = null;
        firstRead.OpCode = OpCodes.Ldloca;
        firstRead.Operand = body.Variables[0];
        clear.OpCode = OpCodes.Call;
        clear.Operand = method.Module.ImportReference(scratch.Methods.Single(m => m.Name == "ClearOrCreate"));
        return true;
    }

    private static bool Calls(Instruction? instruction, string type, string name) =>
        instruction?.Operand is MethodReference method && method.DeclaringType.FullName == type && method.Name == name;
}
