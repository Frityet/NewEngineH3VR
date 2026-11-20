using Mono.Cecil;
using Mono.Cecil.Cil;

namespace H3VR.Converter;

internal static class StockThermalScopeHook
{
    private const string CallbackName = "NativeH3VRSetThermalReplacement";

    internal static void Install(ModuleDefinition module)
    {
        var scope = module.GetType("PIPScope");
        if (scope is null) return;
        var apply = scope.Methods.Single(method => method.Name == "ApplyCameraShader" && method.IsStatic &&
            method.Parameters.Count == 5 && method.Parameters[0].ParameterType.FullName == "UnityEngine.Camera" &&
            method.ReturnType.MetadataType == MetadataType.Void && method.HasBody);
        var set = CameraCall(apply, "SetReplacementShader", 2);
        var reset = CameraCall(apply, "ResetReplacementShader", 0);
        var setMethod = (MethodReference)set.Operand;
        var camera = setMethod.DeclaringType;
        var shader = setMethod.Parameters[0].ParameterType;
        var tag = setMethod.Parameters[1].ParameterType;
        if (shader.FullName != "UnityEngine.Shader" || tag.MetadataType != MetadataType.String ||
            set.Previous.Operand as string != "RenderType")
            throw new InvalidDataException("Unrecognized stock thermal scope replacement tag.");
        var action = new TypeReference("System", "Action`3", module, module.TypeSystem.CoreLibrary);
        foreach (var parameter in new[] { "TCamera", "TShader", "TTag" })
            action.GenericParameters.Add(new GenericParameter(parameter, action));
        var signature = new GenericInstanceType(action);
        signature.GenericArguments.Add(camera);
        signature.GenericArguments.Add(shader);
        signature.GenericArguments.Add(tag);
        var callback = new FieldDefinition(CallbackName, FieldAttributes.Public | FieldAttributes.Static, signature);
        scope.Fields.Add(callback);
        var invoke = new MethodReference("Invoke", module.TypeSystem.Void, signature) { HasThis = true };
        foreach (var parameter in action.GenericParameters)
            invoke.Parameters.Add(new ParameterDefinition(parameter));
        Replace(scope, set, callback, invoke, "NativeH3VRApplyThermalReplacement");
        Replace(scope, reset, callback, invoke, "NativeH3VRResetThermalReplacement");
    }

    private static Instruction CameraCall(MethodDefinition method, string name, int parameters) =>
        method.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Callvirt &&
            instruction.Operand is MethodReference call && call.Name == name &&
            call.DeclaringType.FullName == "UnityEngine.Camera" && call.Parameters.Count == parameters &&
            call.ReturnType.MetadataType == MetadataType.Void);

    private static void Replace(TypeDefinition scope, Instruction call, FieldDefinition callback,
        MethodReference invoke, string name)
    {
        var original = (MethodReference)call.Operand;
        var method = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static,
            original.ReturnType);
        method.Parameters.Add(new ParameterDefinition("camera", ParameterAttributes.None, original.DeclaringType));
        foreach (var parameter in original.Parameters)
            method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
        scope.Methods.Add(method);
        var il = method.Body.GetILProcessor();
        var dispatch = il.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, callback);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue_S, dispatch);
        il.Emit(OpCodes.Pop);
        foreach (var parameter in method.Parameters) il.Emit(OpCodes.Ldarg, parameter);
        il.Emit(OpCodes.Callvirt, original);
        il.Emit(OpCodes.Ret);
        il.Append(dispatch);
        if (original.HasParameters)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
        }
        il.Emit(OpCodes.Callvirt, invoke);
        il.Emit(OpCodes.Ret);
        call.OpCode = OpCodes.Call;
        call.Operand = method;
    }
}
