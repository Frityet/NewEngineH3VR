using Mono.Cecil;
using Mono.Cecil.Cil;

namespace H3VR.Converter.Tests;

public static class TnhColorTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("legacy stock T&H rim conversion is narrow and follows the gradient evaluation", PatchStockSetters);
        yield return ("legacy stock T&H rim conversion validates all caller paths before editing", RejectUnexpectedSetter);
    }

    static void PatchStockSetters()
    {
        using var a = Create("NativeH3VR.Game");
        using var other = Create("Another.Game");
        var methods = a.MainModule.Types.SelectMany(t => t.Methods).ToArray();
        var priorGradient = methods.Single(m => m.Name == "Damage").Body.Instructions[2];
        Check(LegacyTnhColors.Apply(a.MainModule, false) == 0, "Gamma callers should stay authored");
        Check(LegacyTnhColors.Apply(other.MainModule, true) == 0, "Other projects should keep their color paths");
        Check(LegacyTnhColors.Apply(a.MainModule, true) == 8, "Exactly 8 stock setters should change");
        Check(LegacyTnhColors.Apply(a.MainModule, true) == 0, "Imported colors should convert once");

        var helper = a.MainModule.Types.SelectMany(t => t.Methods).Single(m => m.Name == "SetStockLinearRimColor");
        Check(helper.IsAssembly && helper.IsStatic && helper.Body.Instructions.Count == 6, "The conversion helper is internal and small");
        foreach (var method in methods)
        {
            int expected = method.Name switch { "SetNodeMode" => 4, "SetCoreMode" => 3, "Damage" => 1, _ => 0 };
            Check(method.Body.Instructions.Count(i => i.OpCode == OpCodes.Call && ReferenceEquals(i.Operand, helper)) == expected,
                "Preserve original authored call distribution");
        }
        Check(priorGradient.OpCode == OpCodes.Callvirt &&
            ((MethodReference)priorGradient.Operand).Name == "Evaluate" && priorGradient.Next.OpCode == OpCodes.Stloc_1,
            "Gradient evaluates into its original local before conversion");
    }

    static void RejectUnexpectedSetter()
    {
        using var a = Create("NativeH3VR.Game");
        var m = a.MainModule.Types.Single(t => t.Name == "TNH_HoldPointSystemCore").Methods.Single(m => m.Name == "Damage");
        m.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr).Operand = "_EmissionColor";
        try
        {
            LegacyTnhColors.Apply(a.MainModule, true);
        }
        catch (InvalidDataException)
        {
            Check(a.MainModule.Types.SelectMany(t => t.Methods).All(method => method.Name != "SetStockLinearRimColor"),
                "Validation must precede helper installation");
            return;
        }
        throw new InvalidOperationException("An unrelated stock material property was accepted");
    }

    static AssemblyDefinition Create(string assemblyName)
    {
        var a = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(assemblyName, new Version(0, 0)), assemblyName, ModuleKind.Dll);
        var m = a.MainModule;
        TypeReference Unity(string name, bool value = false) => new("UnityEngine", name, m, m.TypeSystem.CoreLibrary, value);
        var color = Unity("Color", true);
        var renderer = Unity("Renderer");
        var material = Unity("Material");
        var gradient = Unity("Gradient");
        var getMaterial = new MethodReference("get_material", material, renderer) { HasThis = true };
        var evaluate = new MethodReference("Evaluate", color, gradient) { HasThis = true };
        evaluate.Parameters.Add(new(m.TypeSystem.Single));
        var set = new MethodReference("SetColor", m.TypeSystem.Void, material) { HasThis = true };
        set.Parameters.Add(new(m.TypeSystem.String));
        set.Parameters.Add(new(color));
        var node = Owner("TNH_HoldPointSystemNode");
        var core = Owner("TNH_HoldPointSystemCore");
        MakeMode(node, "SetNodeMode", "SystemNodeMode", ["Color_Passive", "Color_Hacking", "Color_Analyzing", "Color_Indentified"]);
        MakeMode(core, "SetCoreMode", "SystemCoreMode", ["Color_Protected", "Color_Exposed", "Color_Dying"]);
        var damage = Method(core, "Damage", new TypeReference("FistVR", "Damage", m, m.TypeSystem.CoreLibrary));
        damage.Body.Variables.Add(new(m.TypeSystem.Single));
        damage.Body.Variables.Add(new(color));
        var damageIl = damage.Body.GetILProcessor();
        damageIl.Append(damageIl.Create(OpCodes.Ldnull));
        damageIl.Append(damageIl.Create(OpCodes.Ldc_R4, .5f));
        damageIl.Append(damageIl.Create(OpCodes.Callvirt, evaluate));
        damageIl.Append(damageIl.Create(OpCodes.Stloc_1));
        Receiver(core, damageIl);
        damageIl.Append(damageIl.Create(OpCodes.Ldloc_1));
        damageIl.Append(damageIl.Create(OpCodes.Callvirt, set));
        damageIl.Append(damageIl.Create(OpCodes.Ret));
        return a;

        TypeDefinition Owner(string name)
        {
            var t = new TypeDefinition("FistVR", name, TypeAttributes.Public, m.TypeSystem.Object);
            t.Fields.Add(new("NodeRenderer", FieldAttributes.Public, renderer));
            m.Types.Add(t);
            return t;
        }

        MethodDefinition Method(TypeDefinition owner, string name, TypeReference parameter)
        {
            var method = new MethodDefinition(name, MethodAttributes.Public, m.TypeSystem.Void);
            method.Parameters.Add(new(parameter));
            owner.Methods.Add(method);
            return method;
        }

        void Receiver(TypeDefinition owner, ILProcessor il)
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, owner.Fields.Single(f => f.Name == "NodeRenderer")));
            il.Append(il.Create(OpCodes.Callvirt, getMaterial));
            il.Append(il.Create(OpCodes.Ldstr, "_RimColor"));
        }

        void MakeMode(TypeDefinition owner, string method, string enumName, string[] colors)
        {
            var mode = new TypeDefinition("", enumName, TypeAttributes.NestedPublic, m.TypeSystem.Object);
            owner.NestedTypes.Add(mode);
            var body = Method(owner, method, mode).Body.GetILProcessor();
            foreach (var fieldName in colors)
            {
                var field = new FieldDefinition(fieldName, FieldAttributes.Public, color);
                owner.Fields.Add(field);
                Receiver(owner, body);
                body.Append(body.Create(OpCodes.Ldarg_0));
                body.Append(body.Create(OpCodes.Ldfld, field));
                body.Append(body.Create(OpCodes.Callvirt, set));
            }
            body.Append(body.Create(OpCodes.Ret));
        }
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
