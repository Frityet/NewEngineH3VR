using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class StockShaderWarmupPatchChecks
{
    internal static void Run()
    {
        using var adapter = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("NativeH3VR.OpenXR", new Version(1, 0)), "NativeH3VR.OpenXR", ModuleKind.Dll);
        var helper = new TypeDefinition("NativeH3VR", "StockShaderWarmup", TypeAttributes.Public);
        adapter.MainModule.Types.Add(helper);
        var queue = new MethodDefinition("Queue", MethodAttributes.Public | MethodAttributes.Static,
            adapter.MainModule.TypeSystem.Void);
        queue.Parameters.Add(new ParameterDefinition(Collection(adapter.MainModule)));
        helper.Methods.Add(queue);

        foreach (var fieldName in new[] { "ShaderVariants", "CustomShaderVariants" })
        {
            using var game = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("NativeH3VR.Game", new Version(1, 0)), "NativeH3VR.Game", ModuleKind.Dll);
            var module = game.MainModule;
            var manager = new TypeDefinition("FistVR", "GM", TypeAttributes.Public);
            module.Types.Add(manager);
            var collection = Collection(module);
            var field = new FieldDefinition(fieldName, FieldAttributes.Public, collection);
            manager.Fields.Add(field);
            var awake = new MethodDefinition("Awake", MethodAttributes.Family, module.TypeSystem.Void);
            manager.Methods.Add(awake);
            var il = awake.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            var warmup = new MethodReference("WarmUp", module.TypeSystem.Void, collection) { HasThis = true };
            var call = il.Create(OpCodes.Callvirt, warmup);
            il.Append(call);
            il.Emit(OpCodes.Ret);

            bool intended = fieldName == "ShaderVariants";
            if (StockShaderWarmupPatch.Apply(module, adapter.MainModule) != intended ||
                intended && (call.OpCode != OpCodes.Call || ((MethodReference)call.Operand).Name != "Queue") ||
                !intended && (call.OpCode != OpCodes.Callvirt || !ReferenceEquals(call.Operand, warmup)))
                throw new InvalidOperationException("The stock warmup patch did not preserve unknown GM shapes.");
        }
        Console.WriteLine("PASS stock shader warmup targets only the known GM.Awake call");
    }

    private static TypeReference Collection(ModuleDefinition module) =>
        new("UnityEngine", "ShaderVariantCollection", module,
            new AssemblyNameReference("UnityEngine.CoreModule", new Version(0, 0)));
}
