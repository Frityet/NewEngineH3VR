using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace H3VR.Converter.Tests;

internal static class UniversalTemporalRequestsTests
{
    internal static (string Name, Action Run) Case() =>
        ("URP temporal requests allow history only for the scoped camera", Verify);

    private static void Verify()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nativeh3vr-temporal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, UniversalTemporalRequests.AssemblyName + ".dll");
        try
        {
            Fixture(path, malformed: false);
            UniversalTemporalRequests.Install(directory);
            byte[] patched = File.ReadAllBytes(path);
            UniversalTemporalRequests.Install(directory);
            Require(patched.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "reinstall changed the patched assembly");
            ExercisePatchedAssembly(patched);

            Fixture(path, malformed: true);
            byte[] original = File.ReadAllBytes(path);
            try
            {
                UniversalTemporalRequests.Install(directory);
                throw new InvalidOperationException("the installer accepted a missing Update request check");
            }
            catch (InvalidDataException error) when (error.Message.Contains("TaaHistory.Update", StringComparison.Ordinal))
            {
                Require(original.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "failed patch rewrote the malformed assembly");
                Require(Directory.GetFiles(directory).Length == 1, "failed patch left a temporary assembly");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Fixture(string path, bool malformed)
    {
        const string universal = "UnityEngine.Rendering.Universal";
        string name = UniversalTemporalRequests.AssemblyName;
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)),
            name, ModuleKind.Dll);
        var module = assembly.MainModule;
        var camera = new TypeDefinition("UnityEngine", "Camera", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(camera);
        var processing = new FieldDefinition("Processing", FieldAttributes.Public, module.TypeSystem.Boolean);
        camera.Fields.Add(processing);
        var constructor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
        camera.Methods.Add(constructor);
        var initialize = constructor.Body.GetILProcessor();
        initialize.Emit(OpCodes.Ldarg_0);
        initialize.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
        initialize.Emit(OpCodes.Ret);

        var getter = new MethodDefinition("get_isProcessingRenderRequest",
            MethodAttributes.Public | MethodAttributes.SpecialName, module.TypeSystem.Boolean);
        camera.Methods.Add(getter);
        var read = getter.Body.GetILProcessor();
        read.Emit(OpCodes.Ldarg_0);
        read.Emit(OpCodes.Ldfld, processing);
        read.Emit(OpCodes.Ret);

        AddCheckSite("TemporalAA", "Render", true);
        AddCheckSite("TaaHistory", "Update", !malformed);
        assembly.Write(path);

        void AddCheckSite(string typeName, string methodName, bool callsGetter)
        {
            var type = new TypeDefinition(universal, typeName, TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var method = new MethodDefinition(methodName, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Boolean);
            method.Parameters.Add(new ParameterDefinition(camera));
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            if (callsGetter)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, getter);
            }
            else il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
        }
    }

    private static void ExercisePatchedAssembly(byte[] patched)
    {
        const string universal = "UnityEngine.Rendering.Universal.";
        var load = new AssemblyLoadContext("NativeH3VR temporal fixture", isCollectible: true);
        try
        {
            using var stream = new MemoryStream(patched);
            var assembly = load.LoadFromStream(stream);
            var camera = assembly.GetType("UnityEngine.Camera", throwOnError: true)!;
            var temporal = assembly.GetType(universal + "TemporalAA", throwOnError: true)!;
            var history = assembly.GetType(universal + "TaaHistory", throwOnError: true)!;
            var setter = temporal.GetMethod("SetNativeH3VRHistoryCamera", BindingFlags.Static | BindingFlags.NonPublic)!;
            var sites = new[] { temporal.GetMethod("Render")!, history.GetMethod("Update")! };
            var active = Activator.CreateInstance(camera)!;
            var unrelated = Activator.CreateInstance(camera)!;
            var processing = camera.GetField("Processing")!;
            processing.SetValue(active, true);
            processing.SetValue(unrelated, true);

            AssertSites(active, expected: true, "request without any active scope");
            setter.Invoke(null, [active]);
            AssertSites(active, expected: false, "specifically active request camera");
            AssertSites(unrelated, expected: true, "unrelated concurrent still request");
            processing.SetValue(active, false);
            AssertSites(active, expected: false, "active camera rendering normally");
            setter.Invoke(null, [unrelated]);
            processing.SetValue(active, true);
            AssertSites(active, expected: true, "previously active camera after a scope switch");
            AssertSites(unrelated, expected: false, "newly active request camera");
            setter.Invoke(null, [null]);
            AssertSites(unrelated, expected: true, "previously active request after clearing scope");
            processing.SetValue(unrelated, false);
            AssertSites(unrelated, expected: false, "inactive camera rendering normally");

            void AssertSites(object instance, bool expected, string scenario)
            {
                foreach (var site in sites)
                    Require((bool)site.Invoke(null, [instance])! == expected, $"{site.Name} misclassified {scenario}");
            }
        }
        finally
        {
            load.Unload();
        }
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}
