using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class LegacyTextMesh
{
    internal static string Retarget(ModuleDefinition module)
    {
        var text = module.GetType("UnityEngine.UI.Text");
        var method = text?.Methods.SingleOrDefault(candidate => candidate.Name == "OnPopulateMesh"
            && candidate.Parameters.Count == 1
            && candidate.Parameters[0].ParameterType.FullName == "UnityEngine.UI.VertexHelper");
        if (method is null || !method.HasBody)
            throw new InvalidDataException("The legacy uGUI Text mesh method is missing.");

        var body = method.Body;
        var count = body.Instructions.SingleOrDefault(instruction => instruction.OpCode == OpCodes.Callvirt
            && instruction.Operand is MethodReference reference && reference.Name == "get_Count"
            && reference.DeclaringType is GenericInstanceType collection
            && collection.ElementType.FullName == "System.Collections.Generic.ICollection`1"
            && collection.GenericArguments[0].FullName == "UnityEngine.UIVertex");
        var oldPadding = count?.Next;
        var subtract = oldPadding?.Next;
        var store = subtract?.Next;
        var returnInstruction = body.Instructions.Last();
        var resetCallback = returnInstruction.Previous;
        var cleanup = resetCallback?.Previous?.Previous;
        var clear = body.Instructions.SingleOrDefault(instruction => instruction.Operand is MethodReference reference
            && reference.DeclaringType.FullName == "UnityEngine.UI.VertexHelper" && reference.Name == "Clear")?.Operand as MethodReference;
        if (count is null || oldPadding?.OpCode != OpCodes.Ldc_I4_4 || subtract?.OpCode != OpCodes.Sub
            || store is null || store.OpCode.Code is not (Code.Stloc or Code.Stloc_S or Code.Stloc_0
                or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3)
            || returnInstruction.OpCode != OpCodes.Ret || resetCallback?.OpCode != OpCodes.Stfld
            || resetCallback.Operand is not FieldReference field || field.Name != "m_DisableFontTextureRebuiltCallback"
            || resetCallback.Previous?.OpCode != OpCodes.Ldc_I4_0 || cleanup?.OpCode != OpCodes.Ldarg_0
            || clear is null || body.ExceptionHandlers.Count != 0)
            throw new InvalidDataException("Unexpected legacy uGUI Text mesh layout.");

        // TextGenerator no longer supplies the trailing quad and can return zero vertices.
        // Keep the whole count and clear the mesh before leaving the empty path.
        oldPadding.OpCode = OpCodes.Dup;
        subtract.OpCode = OpCodes.Brtrue;
        subtract.Operand = store;
        var il = body.GetILProcessor();
        il.InsertBefore(store, il.Create(OpCodes.Pop));
        il.InsertBefore(store, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(store, il.Create(OpCodes.Callvirt, clear));
        il.InsertBefore(store, il.Create(OpCodes.Br, cleanup));
        return method.FullName;
    }
}
