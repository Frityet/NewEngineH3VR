using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class StockTargetRendering
{
    internal static int Apply(ModuleDefinition module, TypeDefinition? shim)
    {
        var target = module.GetType("PTarget");
        if (target == null) return 0;
        if (shim == null) throw new InvalidDataException("The stock paper-target rendering helper is missing.");

        var camera = target.Fields.Single(field => field.Name == "renderTextureCamera" &&
            field.FieldType.FullName == "UnityEngine.Camera");
        var commands = target.Fields.Single(field => field.Name == "commands" &&
            field.FieldType.FullName == "UnityEngine.Rendering.CommandBuffer");
        MethodReference Helper(string name) => module.ImportReference(shim.Methods.Single(method => method.Name == name));

        var attach = Helper("Attach");
        var detach = Helper("Detach");
        foreach (string name in new[] { "InitializeRenderTexture", "GetOrCreateCommandBuffer" })
        {
            var method = target.Methods.Single(candidate => candidate.Name == name && candidate.HasBody);
            Rewrite(method, "System.Void UnityEngine.Camera::RemoveAllCommandBuffers()", detach);
            Rewrite(method, "System.Void UnityEngine.Camera::AddCommandBuffer(UnityEngine.Rendering.CameraEvent,UnityEngine.Rendering.CommandBuffer)", attach);
        }

        var begin = Helper("Begin");
        var render = Helper("Render");
        foreach (string name in new[] { "ClearRenderTexture", "UpdateRenderTexture" })
        {
            var method = target.Methods.Single(candidate => candidate.Name == name && candidate.HasBody);
            Rewrite(method, "System.Void UnityEngine.Rendering.CommandBuffer::Clear()", begin, camera);
            Rewrite(method, "System.Void UnityEngine.Camera::Render()", render, commands);
        }
        return 8;
    }

    private static void Rewrite(MethodDefinition method, string original, MethodReference replacement, FieldDefinition? additional = null)
    {
        var call = method.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Callvirt &&
            instruction.Operand is MethodReference reference && reference.FullName == original);
        if (additional == null)
        {
            call.OpCode = OpCodes.Call;
            call.Operand = replacement;
            return;
        }
        var il = method.Body.GetILProcessor();
        call.OpCode = OpCodes.Ldarg_0;
        call.Operand = null;
        var load = il.Create(OpCodes.Ldfld, additional);
        il.InsertAfter(call, load);
        il.InsertAfter(load, il.Create(OpCodes.Call, replacement));
    }
}
