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
        StockThermalScopeHook.Install(module);
        UsePipelineShadowAtlas(module);
        ExposeStockFade(module);

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

    private static void ExposeStockFade(ModuleDefinition module)
    {
        var component = module.GetType("Valve.VR.SteamVR_Fade");
        if (component is null) return;
        var post = component.Methods.Single(m => m.Name == "OnPostRender" && !m.HasParameters && m.HasBody);
        var current = component.Fields.Single(f => f.Name == "currentColor" && f.FieldType.FullName == "UnityEngine.Color");
        var firstMaterial = post.Body.Instructions.First(i => i.OpCode == OpCodes.Ldsfld &&
            i.Operand is FieldReference { Name: "fadeMaterial", FieldType.FullName: "UnityEngine.Material" });
        var exit = post.Body.Instructions.Last();
        if (exit.OpCode != OpCodes.Ret || post.Body.HasExceptionHandlers || firstMaterial.Previous.Operand != exit ||
            firstMaterial.Next.Operand is not MethodReference { Name: "op_Implicit" } ||
            component.Methods.Any(m => m.Name == "LateUpdate") ||
            !post.Body.Instructions.Any(i => i.Operand is MethodReference { Name: "Begin", DeclaringType.FullName: "UnityEngine.GL" }))
            throw new InvalidDataException("Unrecognized stock camera fade draw.");

        // The stock component owns the event, transition and overlay state. URP
        // advances that same component and supplies its own stereo RenderGraph draw.
        var callback = new MethodDefinition(post.Name, post.Attributes, post.ReturnType);
        component.Methods.Add(callback);
        post.Name = "NativeH3VRAdvanceOrDrawFade";
        post.Parameters.Add(new ParameterDefinition("draw", ParameterAttributes.None, module.TypeSystem.Boolean));
        var il = post.Body.GetILProcessor();
        il.InsertBefore(firstMaterial, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(firstMaterial, il.Create(OpCodes.Brfalse, exit));

        var legacy = callback.Body.GetILProcessor();
        legacy.Emit(OpCodes.Ldarg_0);
        legacy.Emit(OpCodes.Ldc_I4_1);
        legacy.Emit(OpCodes.Call, post);
        legacy.Emit(OpCodes.Ret);

        // Render requests can skip game updates or render both eyes in one update.
        // Advance the original transition once per game tick with its own clock.
        var graphics = current.FieldType.Resolve().Module.GetType("UnityEngine.Rendering.GraphicsSettings");
        var pipeline = graphics.Methods.Single(m => m.Name == "get_currentRenderPipeline" && m.IsStatic && !m.HasParameters);
        var tick = new MethodDefinition("LateUpdate", MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void);
        component.Methods.Add(tick);
        var advance = tick.Body.GetILProcessor();
        var done = advance.Create(OpCodes.Ret);
        advance.Emit(OpCodes.Call, module.ImportReference(pipeline));
        advance.Emit(OpCodes.Brfalse_S, done);
        advance.Emit(OpCodes.Ldarg_0);
        advance.Emit(OpCodes.Ldc_I4_0);
        advance.Emit(OpCodes.Call, post);
        advance.Append(done);

        var sample = new MethodDefinition("NativeH3VRGetFadeColor", MethodAttributes.Public | MethodAttributes.HideBySig, current.FieldType);
        component.Methods.Add(sample);
        var render = sample.Body.GetILProcessor();
        render.Emit(OpCodes.Ldarg_0);
        render.Emit(OpCodes.Ldfld, current);
        render.Emit(OpCodes.Ret);
    }
}
