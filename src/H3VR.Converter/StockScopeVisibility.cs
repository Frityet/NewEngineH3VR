using Mono.Cecil;
using Mono.Cecil.Cil;

namespace H3VR.Converter;

internal static class StockScopeVisibility
{
    internal static void Install(ModuleDefinition module)
    {
        var scope = module.GetType("PIPScope");
        if (scope is null) return;
        var method = scope.Methods.Single(m => m.Name == "EvaluateScopeViewVisible" && m.IsStatic && m.HasBody &&
            m.ReturnType.MetadataType == MetadataType.Boolean && m.Parameters.Count == 3 &&
            m.Parameters[0].ParameterType.FullName == "UnityEngine.Vector3" &&
            m.Parameters[1].ParameterType.FullName == "UnityEngine.Vector4" &&
            m.Parameters[2].ParameterType.MetadataType == MetadataType.Single);
        var instructions = method.Body.Instructions;
        var accepted = instructions[^2];
        var scaleCheck = instructions.SingleOrDefault(i =>
            i.OpCode.Code is Code.Bne_Un or Code.Bne_Un_S && i.Previous is { OpCode.Code: Code.Ldc_R4, Operand: 0f } &&
            i.Previous.Previous?.Operand is FieldReference { Name: "z", DeclaringType.FullName: "UnityEngine.Vector4" });
        if (scaleCheck is null || accepted.OpCode != OpCodes.Ldc_I4_1 || instructions[^1].OpCode != OpCodes.Ret)
            throw new InvalidDataException("Unrecognized stock scope exit-pupil check.");
        if (scaleCheck.Next.OpCode == OpCodes.Br && scaleCheck.Next.Operand == accepted) return;
        var rejected = scaleCheck.Next;
        var opacityCheck = scaleCheck.Previous.Previous.Previous?.Previous;
        if (rejected.OpCode != OpCodes.Ldc_I4_0 || rejected.Next.OpCode != OpCodes.Ret ||
            scaleCheck.Operand != rejected.Next.Next || opacityCheck?.OpCode.Code is not (Code.Ble or Code.Ble_S) ||
            opacityCheck.Operand != rejected)
            throw new InvalidDataException("Unexpected stock scope exit-pupil branches.");

        // At the eye-relief plane the lens uses a flat vignette. The earlier opacity and framing checks
        // still apply. A zero radial scale selects that flat case without dividing by zero.
        var il = method.Body.GetILProcessor();
        il.InsertAfter(scaleCheck, il.Create(OpCodes.Br, accepted));
    }
}
