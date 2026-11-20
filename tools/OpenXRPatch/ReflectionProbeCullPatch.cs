using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ReflectionProbeCullPatch
{
    internal static MethodDefinition Apply(TypeDefinition manager, MethodReference isProbeVisible)
    {
        var lateUpdate = manager.Methods.Single(m => m.Name == "LateUpdate" && m.Parameters.Count == 0 && m.HasBody);
        var bounds = manager.Fields.Single(f => f.Name == "m_bounds" && f.FieldType.FullName == "UnityEngine.BoundingSphere[]");
        var isVisible = lateUpdate.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt &&
            i.Operand is MethodReference call && call.FullName == "System.Boolean UnityEngine.CullingGroup::IsVisible(System.Int32)");
        var il = lateUpdate.Body.GetILProcessor();
        // The original group and index are on the stack. Pass the same authored
        // sphere array to the manual-camera visibility helper.
        isVisible.OpCode = OpCodes.Ldarg_0;
        isVisible.Operand = null;
        var loadBounds = il.Create(OpCodes.Ldfld, bounds);
        il.InsertAfter(isVisible, loadBounds);
        il.InsertAfter(loadBounds, il.Create(OpCodes.Call, isProbeVisible));
        return lateUpdate;
    }
}
