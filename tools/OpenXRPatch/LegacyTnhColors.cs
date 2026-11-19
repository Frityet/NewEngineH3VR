using Mono.Cecil;
using Mono.Cecil.Cil;

public static class LegacyTnhColors
{
    const string Node = "FistVR.TNH_HoldPointSystemNode";
    const string Core = "FistVR.TNH_HoldPointSystemCore";
    const string HelperName = "SetStockLinearRimColor";
    const string SetColor = "System.Void UnityEngine.Material::SetColor(System.String,UnityEngine.Color)";
    const string GetMaterial = "UnityEngine.Material UnityEngine.Renderer::get_material()";
    const string Evaluate = "UnityEngine.Color UnityEngine.Gradient::Evaluate(System.Single)";
    const string GetLinear = "UnityEngine.Color UnityEngine.Color::get_linear()";

    public static int Apply(ModuleDefinition module, bool importLegacyLinearColors)
    {
        if (!importLegacyLinearColors || module.Assembly.Name.Name != "NativeH3VR.Game") return 0;
        var node = module.GetType(Node) ?? throw new InvalidDataException("Missing imported stock HoldPoint node.");
        var core = module.GetType(Core) ?? throw new InvalidDataException("Missing imported stock HoldPoint core.");
        var helper = core.Methods.SingleOrDefault(method => method.Name == HelperName);
        if (helper != null) ValidateHelper(helper);

        var pending = new List<Instruction>();
        int existing = 0;
        foreach (var (owner, name, parameter, colors) in new[]
        {
            (node, "SetNodeMode", Node + "/SystemNodeMode", new[] { "Color_Passive", "Color_Hacking", "Color_Analyzing", "Color_Indentified" }),
            (core, "SetCoreMode", Core + "/SystemCoreMode", new[] { "Color_Protected", "Color_Exposed", "Color_Dying" }),
            (core, "Damage", "FistVR.Damage", new[] { "" })
        })
        {
            var method = owner.Methods.Single(m => m.Name == name && m.HasBody &&
                m.IsPublic && !m.IsStatic && m.ReturnType.MetadataType == MetadataType.Void &&
                m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == parameter);
            var calls = method.Body.Instructions.Where(instruction =>
                Calls(instruction, OpCodes.Callvirt, SetColor) ||
                (helper != null && Calls(instruction, OpCodes.Call, helper.FullName))).ToArray();
            if (calls.Length != colors.Length)
                throw new InvalidDataException("Unexpected stock rim setter count: " + method.FullName);
            for (int i = 0; i < calls.Length; i++)
            {
                RequireStockRimArguments(method, calls[i], colors[i]);
                if (calls[i].OpCode == OpCodes.Callvirt) pending.Add(calls[i]);
                else existing++;
            }
        }

        if (existing == 8 && pending.Count == 0) return 0;
        if (existing != 0 || pending.Count != 8 || helper != null)
            throw new InvalidDataException("Partially transformed stock T&H rim setters.");

        var setter = (MethodReference)pending[0].Operand;
        helper = MakeHelper(core, setter);
        ValidateHelper(helper);
        foreach (var call in pending)
        {
            // A one-byte call replaces a one-byte callvirt. All instruction
            // offsets and existing branch targets are preserved.
            call.OpCode = OpCodes.Call;
            call.Operand = helper;
        }
        return pending.Count;
    }

    static void RequireStockRimArguments(MethodDefinition method, Instruction call, string colorField)
    {
        var color = call.Previous;
        Instruction property;
        if (colorField.Length == 0)
        {
            // Damage still evaluates the authored Blend gradient first, into
            // its original local. Convert that result only at the sink.
            property = color.Previous;
            var gradient = property.Previous?.Previous?.Previous?.Previous?.Previous;
            if (color.OpCode != OpCodes.Ldloc_1 ||
                method.Body.Variables.Count < 2 || method.Body.Variables[1].VariableType.FullName != "UnityEngine.Color" ||
                gradient?.Next?.OpCode != OpCodes.Stloc_1 ||
                !Calls(gradient, OpCodes.Callvirt, Evaluate))
                throw new InvalidDataException("Unexpected stock T&H damage gradient setter.");
        }
        else
        {
            property = color.Previous.Previous;
            if (color.OpCode != OpCodes.Ldfld || color.Operand is not FieldReference source ||
                source.DeclaringType.FullName != method.DeclaringType.FullName || source.Name != colorField ||
                source.FieldType.FullName != "UnityEngine.Color" || color.Previous.OpCode != OpCodes.Ldarg_0)
                throw new InvalidDataException("Unexpected authored stock T&H color field: " + colorField);
        }

        var receiver = property.Previous?.Previous;
        if (property.OpCode != OpCodes.Ldstr || (string)property.Operand != "_RimColor" ||
            !Calls(property.Previous, OpCodes.Callvirt, GetMaterial) ||
            receiver?.OpCode != OpCodes.Ldfld || receiver.Operand is not FieldReference field ||
            field.DeclaringType.FullName != method.DeclaringType.FullName ||
            field.Name != "NodeRenderer" || field.FieldType.FullName != "UnityEngine.Renderer")
            throw new InvalidDataException("Unexpected stock T&H renderer or rim property.");
    }

    static MethodDefinition MakeHelper(TypeDefinition owner, MethodReference setter)
    {
        var materialType = setter.DeclaringType;
        var colorType = setter.Parameters[1].ParameterType;
        var helper = new MethodDefinition(HelperName, MethodAttributes.Assembly | MethodAttributes.Static |
            MethodAttributes.HideBySig, owner.Module.TypeSystem.Void);
        helper.Parameters.Add(new("material", ParameterAttributes.None, materialType));
        helper.Parameters.Add(new("property", ParameterAttributes.None, setter.Parameters[0].ParameterType));
        helper.Parameters.Add(new("authored", ParameterAttributes.None, colorType));
        owner.Methods.Add(helper);
        var linear = new MethodReference("get_linear", colorType, colorType) { HasThis = true };
        var il = helper.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldarga_S, helper.Parameters[2]));
        il.Append(il.Create(OpCodes.Call, linear));
        il.Append(il.Create(OpCodes.Callvirt, setter));
        il.Append(il.Create(OpCodes.Ret));
        return helper;
    }

    static void ValidateHelper(MethodDefinition helper)
    {
        if (!helper.IsAssembly || !helper.IsStatic || !helper.HasBody || helper.Body.Variables.Count != 0 ||
            helper.Body.ExceptionHandlers.Count != 0 || helper.ReturnType.MetadataType != MetadataType.Void ||
            !helper.Parameters.Select(p => p.ParameterType.FullName)
                .SequenceEqual(new[] { "UnityEngine.Material", "System.String", "UnityEngine.Color" }))
            throw new InvalidDataException("Unexpected stock T&H linear setter helper.");
        var il = helper.Body.Instructions;
        if (il.Count != 6 || il[0].OpCode != OpCodes.Ldarg_0 || il[1].OpCode != OpCodes.Ldarg_1 ||
            il[2].OpCode != OpCodes.Ldarga_S || !ReferenceEquals(il[2].Operand, helper.Parameters[2]) ||
            !Calls(il[3], OpCodes.Call, GetLinear) || !Calls(il[4], OpCodes.Callvirt, SetColor) || il[5].OpCode != OpCodes.Ret)
            throw new InvalidDataException("Unexpected stock T&H linear setter body.");
    }

    static bool Calls(Instruction? instruction, OpCode opCode, string target) =>
        instruction?.OpCode == opCode && instruction.Operand is MethodReference method && method.FullName == target;
}
