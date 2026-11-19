using Mono.Cecil;
using Mono.Cecil.Cil;

public static class LegacyStockMaterialColors
{
    const string HelperOwner = "FistVR.TAH_Reticle";
    const string HelperName = "SetStockHdrMaterialColor";
    const string Tag = "NativeH3VRLegacyHdrColor";
    const string Setter = "System.Void UnityEngine.Material::SetColor(System.String,UnityEngine.Color)";
    const string SpeechProperty = "System.String FistVR.ZosigNPCSpeechInput::ColorName";

    // The original Assembly-CSharp MVID survives NativeH3VR's retarget. This
    // allowlist identifies the exact setters and authored value stack in 120p3.
    static readonly Guid SteamGame = new("6d320100-4b8c-4da8-8a19-b53d3475e3c7");
    readonly record struct Site(string Method, int Call, int Property, string? Literal);
    static readonly Site[] Sites =
    [
        new("System.Void FistVR.WW_StandaloneWeatherSystem::FXManagement()", 1375, 1347, "_TintColor"),
        new("System.Void FistVR.WW_TeleportMaster::FXManagement()", 1482, 1454, "_TintColor"),
        new("System.Void FistVR.AmmoCounterAttachment::UpdateDisplay()", 279, 257, "_EmissionColor"),
        new("System.Void FistVR.StudioLight::UpdateLight()", 220, 209, "_Color"),
        new("System.Void FistVR.CompoundPistolPowerSystem::Update()", 213, 191, "_EmissionColor"),
        new("System.Void FistVR.LAPD2019::UpdateElectricalSystem()", 79, 51, "_Color"),
        new("System.Void FistVR.LAPD2019::UpdateElectricalSystem()", 123, 95, "_Color"),
        new("System.Void FistVR.LAPD2019Battery::Update()", 39, 11, "_Color"),
        new("System.Void FistVR.OmniCleric::Update()", 366, 343, "_Color"),
        new("System.Void FistVR.OmniDisc::Damage(FistVR.Damage)", 346, 340, "_Color"),
        new("System.Void FistVR.OmniShape::Init(FistVR.OmniSpawner_Shapes,System.Boolean,FistVR.OmniSpawnDef_Shape/OmniShapeColor)", 56, 39, "_Color"),
        new("System.Void FistVR.TAH_Reticle::Update()", 105, 99, "_EmissionColor"),
        new("System.Void FistVR.ZosigEndingManager::InitPhase(FistVR.ZosigEndingManager/RW_EndPhase)", 2184, 2178, "_EmissionColor"),
        new("System.Void FistVR.ZosigEndingManager::Update()", 299, 293, "_EmissionColor"),
        new("System.Void FistVR.ZosigEndingManager::Update()", 583, 577, "_EmissionColor"),
        new("System.Void FistVR.ZosigGastroCycler::UpdateHealth()", 85, 79, "_EmissionColor"),
        new("System.Void FistVR.ZosigNPCSpeechInput::Awake()", 58, 47, null),
        new("System.Void FistVR.ZosigNPCSpeechInput::Update()", 121, 93, null),
        new("System.Void FistVR.ZosigNPCSpeechInput::Update()", 224, 196, null),
        new("System.Void MedigunBeam::SetLineColor(System.Int32)", 28, 6, "_Color")
    ];

    public static int Apply(ModuleDefinition module, bool importLegacyLinearColors)
    {
        if (!importLegacyLinearColors || module.Assembly.Name.Name != "NativeH3VR.Game" || module.Mvid != SteamGame)
            return 0;

        var owner = module.GetType(HelperOwner) ?? throw new InvalidDataException("Missing original stock reticle type.");
        var existing = owner.Methods.SingleOrDefault(method => method.Name == HelperName);
        var remaining = new List<Instruction>();
        foreach (var site in Sites)
        {
            var type = site.Method["System.Void ".Length..site.Method.IndexOf("::", StringComparison.Ordinal)];
            var method = module.GetType(type)?.Methods.SingleOrDefault(m => m.FullName == site.Method);
            if (method is not { HasBody: true, IsStatic: false })
                throw new InvalidDataException("Missing original stock material setter: " + site.Method);
            var property = method.Body.Instructions.SingleOrDefault(i => i.Offset == site.Property);
            if (site.Literal is string literal
                ? property?.OpCode != OpCodes.Ldstr || !Equals(property.Operand, literal)
                : property?.OpCode != OpCodes.Ldfld || property.Operand is not FieldReference field ||
                  field.FullName != SpeechProperty)
                throw new InvalidDataException("Unexpected stock material setter property: " + site.Method);
            var call = method.Body.Instructions.SingleOrDefault(i => i.Offset == site.Call);
            if (call?.OpCode == OpCodes.Callvirt && call.Operand is MethodReference setter && setter.FullName == Setter)
                remaining.Add(call);
            else if (existing == null || call?.OpCode != OpCodes.Call || call.Operand is not MethodReference helper ||
                     helper.FullName != existing.FullName || !ReferenceEquals(helper.Resolve(), existing))
                throw new InvalidDataException($"Unexpected stock material setter at {site.Method} IL_{site.Call:x4}.");
        }

        if (remaining.Count == 0 && existing != null)
        {
            if (!existing.HasBody || !existing.IsAssembly || !existing.IsStatic ||
                existing.ReturnType.MetadataType != MetadataType.Void || !existing.Body.InitLocals)
                throw new InvalidDataException("Unexpected existing stock HDR material helper definition.");
            var original = existing.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Single(m => m.FullName == Setter);
            var expected = MakeHelper(module, original);
            if (existing.Body.ExceptionHandlers.Count != 0 ||
                !existing.Body.Variables.Select(v => v.VariableType.FullName)
                    .SequenceEqual(expected.Body.Variables.Select(v => v.VariableType.FullName)) ||
                !existing.Parameters.Select(p => p.ParameterType.FullName)
                    .SequenceEqual(expected.Parameters.Select(p => p.ParameterType.FullName)) ||
                !BodyShape(existing).SequenceEqual(BodyShape(expected)))
                throw new InvalidDataException("Unexpected existing stock HDR material compatibility helper.");
            return 0;
        }
        if (existing != null || remaining.Count != Sites.Length)
            throw new InvalidDataException("Partially patched stock HDR material setters.");

        var generated = MakeHelper(module, (MethodReference)remaining[0].Operand);
        owner.Methods.Add(generated);
        foreach (var call in remaining)
        {
            call.OpCode = OpCodes.Call;
            call.Operand = generated;
        }
        return remaining.Count;
    }

    static IEnumerable<string> BodyShape(MethodDefinition method)
    {
        var code = method.Body.Instructions;
        foreach (var i in code)
        {
            var operand = i.Operand switch
            {
                Instruction target => code.IndexOf(target).ToString(),
                ParameterDefinition parameter => parameter.Index.ToString(),
                VariableDefinition variable => variable.Index.ToString(),
                MemberReference member => member.FullName,
                _ => i.Operand?.ToString()
            };
            yield return i.OpCode.Name + " " + operand;
        }
    }

    static MethodDefinition MakeHelper(ModuleDefinition module, MethodReference setter)
    {
        var engine = setter.DeclaringType.Resolve().Module;
        TypeDefinition EngineType(string name) => engine.GetType(name) ??
            throw new InvalidDataException("Unity color compatibility API is unavailable: " + name);
        MethodReference EngineMethod(string type, string name, params string[] args) => module.ImportReference(
            EngineType(type).Methods.Single(m => m.Name == name &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(args)));
        int EnumValue(string type, string name) =>
            (int)(EngineType(type).Fields.Single(f => f.Name == name).Constant ??
                throw new InvalidDataException("Unity color compatibility enum is unavailable: " + type + "." + name));

        var color = setter.Parameters[1].ParameterType;
        var material = setter.DeclaringType;
        var shader = module.ImportReference(EngineType("UnityEngine.Shader"));
        const string propertyFlags = "UnityEngine.Rendering.ShaderPropertyFlags";
        int hdr = EnumValue(propertyFlags, "HDR");
        int gamma = EnumValue(propertyFlags, "Gamma");
        int shaderColor = EnumValue("UnityEngine.Rendering.ShaderPropertyType", "Color");
        int linear = EnumValue("UnityEngine.ColorSpace", "Linear");
        var equals = new MethodReference("op_Equality", module.TypeSystem.Boolean, module.TypeSystem.String);
        equals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        equals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var method = new MethodDefinition(HelperName, MethodAttributes.Assembly | MethodAttributes.Static |
            MethodAttributes.HideBySig, module.TypeSystem.Void);
        method.Parameters.Add(new("material", ParameterAttributes.None, material));
        method.Parameters.Add(new("property", ParameterAttributes.None, module.TypeSystem.String));
        method.Parameters.Add(new("authored", ParameterAttributes.None, color));
        method.Body.InitLocals = true;
        var shaderLocal = new VariableDefinition(shader);
        var propertyLocal = new VariableDefinition(module.TypeSystem.Int32);
        method.Body.Variables.Add(shaderLocal);
        method.Body.Variables.Add(propertyLocal);
        var il = method.Body.GetILProcessor();
        var write = il.Create(OpCodes.Ldarg_0);
        var convert = il.Create(OpCodes.Ldarga_S, method.Parameters[2]);

        // Convert only when the active player is linear, the exact imported
        // stock Material declared this property, and its current Shader still
        // binds a non-Gamma HDR Color. The original setter always runs once.
        il.Emit(OpCodes.Call, EngineMethod("UnityEngine.QualitySettings", "get_activeColorSpace"));
        il.Emit(OpCodes.Ldc_I4, linear);
        il.Emit(OpCodes.Bne_Un, write);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, Tag);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Callvirt, EngineMethod("UnityEngine.Material", "GetTag", "System.String", "System.Boolean", "System.String"));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, equals);
        il.Emit(OpCodes.Brfalse, write);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, EngineMethod("UnityEngine.Material", "get_shader"));
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Call, EngineMethod("UnityEngine.Object", "op_Implicit", "UnityEngine.Object"));
        il.Emit(OpCodes.Brfalse, write);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, EngineMethod("UnityEngine.Shader", "FindPropertyIndex", "System.String"));
        il.Emit(OpCodes.Stloc_1);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, write);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Callvirt, EngineMethod("UnityEngine.Shader", "GetPropertyType", "System.Int32"));
        il.Emit(OpCodes.Ldc_I4, shaderColor);
        il.Emit(OpCodes.Bne_Un, write);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Callvirt, EngineMethod("UnityEngine.Shader", "GetPropertyFlags", "System.Int32"));
        il.Emit(OpCodes.Ldc_I4, hdr | gamma);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4, hdr);
        il.Emit(OpCodes.Bne_Un, write);
        il.Append(convert);
        il.Emit(OpCodes.Call, EngineMethod("UnityEngine.Color", "get_linear"));
        il.Emit(OpCodes.Starg, method.Parameters[2]);
        il.Append(write);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, setter);
        il.Emit(OpCodes.Ret);
        return method;
    }
}
