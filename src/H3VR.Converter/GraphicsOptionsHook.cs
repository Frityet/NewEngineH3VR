using Mono.Cecil;
using Mono.Cecil.Cil;

namespace H3VR.Converter;

internal static class GraphicsOptionsHook
{
    private const string CallbackName = "NativeH3VRGraphicsScreenInitialized";

    internal static void Install(string candidate, params string[] referencePaths)
    {
        string path = SafeFiles.Inside(candidate, "Assets/Plugins/NativeH3VR.Game.dll");
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        foreach (string referencePath in referencePaths)
            resolver.AddSearchDirectory(referencePath);
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            InMemory = true,
            AssemblyResolver = resolver
        });
        var module = assembly.MainModule;
        var screen = module.GetType("FistVR.OptionsScreen_Quality");
        var initialize = screen?.Methods.SingleOrDefault(method => method.Name == "InitScreen" && !method.IsStatic &&
            !method.HasParameters && method.ReturnType.MetadataType == MetadataType.Void);
        if (initialize is not { HasBody: true } || screen!.Fields.Any(field => field.Name == CallbackName))
            throw new InvalidDataException("Unable to install the graphics options callback on the stock graphics screen.");

        var action = new TypeReference("System", "Action`1", module, module.TypeSystem.CoreLibrary);
        var argument = new GenericParameter(action);
        action.GenericParameters.Add(argument);
        var closedAction = new GenericInstanceType(action);
        closedAction.GenericArguments.Add(screen);
        var field = new FieldDefinition(CallbackName, FieldAttributes.Public | FieldAttributes.Static, closedAction);
        screen.Fields.Add(field);

        // The delegate remains optional while the runtime assembly registers before the first scene.
        var invoke = new MethodReference("Invoke", module.TypeSystem.Void, closedAction) { HasThis = true };
        invoke.Parameters.Add(new ParameterDefinition(argument));
        var il = initialize.Body.GetILProcessor();
        var returns = initialize.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToArray();
        if (returns.Length != 1 || returns[0] != initialize.Body.Instructions[^1] || initialize.Body.HasExceptionHandlers)
            throw new InvalidDataException("The stock graphics screen no longer has its expected InitScreen exit.");
        var exit = returns[0];

        // Keep the original exit instruction's identity so any branches to it execute the hook.
        var call = il.Create(OpCodes.Ldarg_0);
        exit.OpCode = OpCodes.Ldsfld;
        exit.Operand = field;
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brtrue_S, call));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(call);
        il.Append(il.Create(OpCodes.Callvirt, invoke));
        il.Append(il.Create(OpCodes.Ret));
        UsePipelineShadowAtlas(module);

        string staged = path + ".native-graphics";
        try
        {
            assembly.Write(staged);
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    private static void UsePipelineShadowAtlas(ModuleDefinition module)
    {
        var stock = module.GetType("DirectLightVertexShadows");
        if (stock is null) return;
        foreach (var (name, originalCall) in new[] { ("InitCBuffer", "SetGlobalTexture"),
                     ("ApplyCBuffer", "AddCommandBuffer"), ("RemoveCBuffer", "RemoveCommandBuffer") })
        {
            var method = stock.Methods.Single(m => m.Name == name && !m.HasParameters && m.HasBody &&
                m.ReturnType.MetadataType == MetadataType.Void);
            if (method.Body.HasExceptionHandlers || !method.Body.Instructions.Any(i =>
                i.Operand is MethodReference call && call.Name == originalCall &&
                call.DeclaringType.FullName is "UnityEngine.Rendering.CommandBuffer" or "UnityEngine.Light"))
                throw new InvalidDataException("Unrecognized stock vertex shadow hook: " + method.FullName);
            // URP supplies the main-light atlas to these shaders per camera. Keep the component's
            // environment-probe setup, and skip creating or attaching the Built-in-only buffer.
            method.Body.Instructions.Clear();
            method.Body.Variables.Clear();
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
        }
    }
}
