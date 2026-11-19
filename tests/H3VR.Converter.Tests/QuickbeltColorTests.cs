using Mono.Cecil;
using Mono.Cecil.Cil;

namespace H3VR.Converter.Tests;

public static class QuickbeltColorTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("legacy quickbelt conversion preserves original color roles, alpha and Gamma", PreservesOriginalRoles);
        yield return ("legacy quickbelt conversion guards unexpected or partial IL before edits", RejectsUnexpectedBody);
    }

    private static void PreservesOriginalRoles()
    {
        using var native = Create("NativeH3VR.Game");
        using var other = Create("Example.Game");
        Assert(LegacyQuickbeltColors.Apply(native.MainModule, false) == 0, "Gamma source changed");
        Assert(LegacyQuickbeltColors.Apply(other.MainModule, true) == 0, "Other game stock-like method changed");
        Assert(LegacyQuickbeltColors.Apply(native.MainModule, true) == 4, "Native should convert the four tint components");
        Assert(LegacyQuickbeltColors.Apply(native.MainModule, true) == 0, "Reapplying should preserve the converted components");

        float l = 0.07323897f;
        float[] expected = [l, l, 1, 1, l, 1, l, 1, 1, 1, 1, 1, 1, 1, 1, 1];
        Assert(Literals(native).Select(i => (float)i.Operand).SequenceEqual(expected),
            "Blue, green, white selection, and alpha should remain independent");
    }

    private static void RejectsUnexpectedBody()
    {
        using var broken = Create("NativeH3VR.Game");
        var operands = Literals(broken);
        operands[0].Operand = 0.07323897f;
        var before = operands.Select(i => (float)i.Operand).ToArray();
        try
        {
            LegacyQuickbeltColors.Apply(broken.MainModule, true);
        }
        catch (InvalidDataException)
        {
            Assert(operands.Select(i => (float)i.Operand).SequenceEqual(before), "Unexpected IL was partly edited");
            return;
        }
        throw new Exception("Partially changed legacy literals were accepted");
    }

    private static Instruction[] Literals(AssemblyDefinition a) => a.MainModule.GetType("FistVR.FVRQuickBeltSlot")
        .Methods.Single(m => m.Name == "Update").Body.Instructions.Where(i => i.OpCode == OpCodes.Ldc_R4).ToArray();

    private static AssemblyDefinition Create(string name)
    {
        var a = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
        var m = a.MainModule;
        var t = new TypeDefinition("FistVR", "FVRQuickBeltSlot", TypeAttributes.Public, m.TypeSystem.Object);
        var update = new MethodDefinition("Update", MethodAttributes.Private | MethodAttributes.HideBySig, m.TypeSystem.Void);
        t.Methods.Add(update);
        m.Types.Add(t);
        var color = new TypeReference("UnityEngine", "Color", m, m.TypeSystem.CoreLibrary, true);
        var material = new TypeReference("UnityEngine", "Material", m, m.TypeSystem.CoreLibrary);
        var create = new MethodReference(".ctor", m.TypeSystem.Void, color) { HasThis = true };
        for (int i = 0; i < 4; i++) create.Parameters.Add(new(m.TypeSystem.Single));
        var set = new MethodReference("SetColor", m.TypeSystem.Void, material) { HasThis = true };
        set.Parameters.Add(new(m.TypeSystem.String));
        set.Parameters.Add(new(color));
        var il = update.Body.GetILProcessor();
        float[][] colors = [[.3f, .3f, 1, 1], [.3f, 1, .3f, 1], [1, 1, 1, 1], [1, 1, 1, 1]];
        foreach (var c in colors)
        {
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Ldstr, "_RimColor"));
            foreach (var v in c) il.Append(il.Create(OpCodes.Ldc_R4, v));
            il.Append(il.Create(OpCodes.Newobj, create));
            il.Append(il.Create(OpCodes.Callvirt, set));
        }
        il.Append(il.Create(OpCodes.Ret));
        return a;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
