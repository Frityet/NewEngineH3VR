using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq.Expressions;

namespace H3VR.Converter.Tests;

internal static class GraphicsOptionsHookTests
{
    internal static (string, Action) Case() =>
        ("graphics screen invokes its optional initialization callback after the stock handler", InvokesCallback);

    private static void InvokesCallback()
    {
        var root = Directory.CreateTempSubdirectory("h3vr-graphics-options-hook-");
        try
        {
            var path = Path.Combine(root.FullName, "Assets", "Plugins", "NativeH3VR.Game.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteFixture(path);
            GraphicsOptionsHook.Install(root.FullName);

            var game = System.Reflection.Assembly.Load(File.ReadAllBytes(path));
            var type = game.GetType("FistVR.OptionsScreen_Quality", throwOnError: true)!;
            var screen = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            var initialize = type.GetMethod("InitScreen")!;
            var count = type.GetField("Calls")!;
            var hook = type.GetField("NativeH3VRGraphicsScreenInitialized")!;

            initialize.Invoke(screen, null);
            if ((int)count.GetValue(screen)! != 1)
                throw new Exception("Stock initialization did not run when no callback was registered.");

            int calls = 0;
            Action<object> observe = instance =>
            {
                if (instance != screen || (int)count.GetValue(screen)! != 2)
                    throw new Exception("The callback did not receive the initialized stock instance.");
                calls++;
            };
            var argument = Expression.Parameter(type);
            var callback = Expression.Lambda(hook.FieldType, Expression.Invoke(Expression.Constant(observe), argument), argument);
            hook.SetValue(null, callback.Compile());

            initialize.Invoke(screen, null);
            if (calls != 1)
                throw new Exception("Stock initialization did not dispatch the callback once.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void WriteFixture(string path)
    {
        using var game = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("GraphicsOptionsHookFixture", new Version(1, 0)),
            "GraphicsOptionsHookFixture", ModuleKind.Dll);
        var module = game.MainModule;
        var screen = new TypeDefinition("FistVR", "OptionsScreen_Quality", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(screen);
        var count = new FieldDefinition("Calls", FieldAttributes.Public, module.TypeSystem.Int32);
        screen.Fields.Add(count);
        var initialize = new MethodDefinition("InitScreen", MethodAttributes.Public, module.TypeSystem.Void);
        screen.Methods.Add(initialize);
        var il = initialize.Body.GetILProcessor();
        var exit = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldfld, count);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stfld, count);
        il.Emit(OpCodes.Br_S, exit);
        il.Append(exit);
        game.Write(path);
    }
}
