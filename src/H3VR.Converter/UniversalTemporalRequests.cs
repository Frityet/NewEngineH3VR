using Mono.Cecil;
using Mono.Cecil.Cil;

namespace H3VR.Converter;

internal static class UniversalTemporalRequests
{
    internal const string AssemblyName = "Unity.RenderPipelines.Universal.Runtime";
    private const string Setter = "SetNativeH3VRHistoryCamera";
    private const string Predicate = "IsNativeH3VRTransientRenderRequest";

    // URP 17.7 treats every manual render as a still and suppresses both history
    // allocation updates and history writes. The native bridge renders each eye
    // once per display frame through this same API.
    internal static void Install(string managed)
    {
        string path = Path.Combine(managed, AssemblyName + ".dll");
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true
        });
        if (assembly.Name.Name != AssemblyName) throw new InvalidDataException("Unexpected URP player assembly.");
        var module = assembly.MainModule;
        var temporal = module.GetType("UnityEngine.Rendering.Universal.TemporalAA");
        var history = module.GetType("UnityEngine.Rendering.Universal.TaaHistory");
        if (temporal is null || history is null) throw new InvalidDataException("Missing URP 17.7 TAA implementation.");
        var sites = new[] { RequestCheck(temporal, "Render"), RequestCheck(history, "Update") };
        bool installed = temporal.Methods.Any(method => method.Name == Setter);
        if (installed && sites.All(site => site.Operand is MethodReference method && method.Name == Predicate)) return;
        if (installed || sites.Any(site => site.Operand is not MethodReference { Name: "get_isProcessingRenderRequest" }))
            throw new InvalidDataException("Incomplete URP temporal render-request integration.");

        var getter = (MethodReference)sites[0].Operand;
        var camera = getter.DeclaringType;
        var active = new FieldDefinition("s_NativeH3VRHistoryCamera", FieldAttributes.Private | FieldAttributes.Static, camera);
        temporal.Fields.Add(active);
        MethodDefinition Add(string name, TypeReference result)
        {
            var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static, result);
            method.Parameters.Add(new ParameterDefinition("camera", ParameterAttributes.None, camera));
            temporal.Methods.Add(method);
            return method;
        }
        var setter = Add(Setter, module.TypeSystem.Void).Body.GetILProcessor();
        setter.Emit(OpCodes.Ldarg_0);
        setter.Emit(OpCodes.Stsfld, active);
        setter.Emit(OpCodes.Ret);

        var predicate = Add(Predicate, module.TypeSystem.Boolean);
        var il = predicate.Body.GetILProcessor();
        var regularCamera = il.Create(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Brfalse_S, regularCamera);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, active);
        // Reference identity, independent of UnityEngine.Object's overloaded equality.
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Ret);
        il.Append(regularCamera);
        il.Emit(OpCodes.Ret);
        foreach (var site in sites)
        {
            site.OpCode = OpCodes.Call;
            site.Operand = predicate;
        }
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            assembly.Write(temp);
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }

    private static Instruction RequestCheck(TypeDefinition type, string methodName)
    {
        var sites = type.Methods.Where(method => method.Name == methodName && method.HasBody)
            .SelectMany(method => method.Body.Instructions).Where(instruction =>
                instruction.Operand is MethodReference method && method.ReturnType.MetadataType == MetadataType.Boolean &&
                ((instruction.OpCode == OpCodes.Callvirt && method.DeclaringType.FullName == "UnityEngine.Camera" &&
                  method.Name == "get_isProcessingRenderRequest" && method.Parameters.Count == 0) ||
                 (instruction.OpCode == OpCodes.Call && method.DeclaringType.FullName == "UnityEngine.Rendering.Universal.TemporalAA" &&
                  method.Name == Predicate && method.Parameters.Count == 1))).ToArray();
        return sites.Length == 1 ? sites[0] :
            throw new InvalidDataException($"Unexpected URP 17.7 camera history check in {type.Name}.{methodName}.");
    }
}
