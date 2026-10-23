#nullable enable
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ScopePairPatch {
    internal const string Target =
        "System.Void PIPScope::UpdateAllScopes(PIPScope_Camera,PIPScope,System.Int32,System.Int32)";
    internal const string HelperType = "NativeH3VR.NewEngineScopeActivation";
    internal const string Helper =
        "System.Void " +
        "NativeH3VR.NewEngineScopeActivation::SetEnabled(UnityEngine.Behaviour,System.Boolean)";
    internal const string OldSetter =
        "System.Void UnityEngine.Behaviour::set_enabled(System.Boolean)";
    internal const string ExpectedBodyHash =
        "34456ecd9b17649db695894e936bb76309e8941863d929719c84f904e52f8d25";
    internal static readonly int[] Sites = { 331, 374, 580 };
    internal static string Fingerprint(MethodDefinition m) {
        string Position(Instruction? i)=>i==null?"end":m.Body.Instructions.IndexOf(i).ToString();
        string Operand(object? value)=>value switch {
            Instruction i=>"IL:"+Position(i),Instruction[] a=>string.Join(",",a.Select(Position)),
            VariableDefinition v=>"local:"+v.Index,ParameterDefinition p=>"arg:"+p.Index,
            MemberReference r=>r.FullName+"@"+(r.DeclaringType?.Scope?.Name??(r as TypeReference)?.Scope?.Name),
            float f=>"float:"+BitConverter.SingleToInt32Bits(f),double d=>"double:"+BitConverter.DoubleToInt64Bits(d),_=>value?.ToString()??"null"};
        string text =
            m.FullName + "|" + m.Body.InitLocals + "|" + m.Body.MaxStackSize + "\n" +
            string.Join("\n", m.Body.Variables.Select(v => v.VariableType.FullName)) + "\n" +
            string.Join("\n",
                        m.Body.Instructions.Select(i => i.OpCode + " " + Operand(i.Operand))) +
            "\n" +
            string.Join("\n",
                        m.Body.ExceptionHandlers.Select(
                            e => e.HandlerType + " " + e.CatchType + " " + Position(e.TryStart) +
                                 ":" + Position(e.TryEnd) + ":" + Position(e.HandlerStart) + ":" +
                                 Position(e.HandlerEnd) + ":" + Position(e.FilterStart)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
    }
    internal static MethodDefinition Apply(AssemblyDefinition game, AssemblyDefinition xr) {
        if (game.Name.Name != "NativeH3VR.Game" || xr.Name.Name != "NativeH3VR.XR.Managed")
            throw new InvalidOperationException("Unexpected scope Game/XR identities");
        var helper =
            xr.MainModule.GetType(HelperType)?.Methods.SingleOrDefault(m => m.FullName == Helper);
        if (helper == null || !helper.IsStatic || !helper.IsPublic || helper.HasGenericParameters)
            throw new InvalidOperationException("Scoped pair helper is absent or changed");
        var target =
            game.MainModule.GetType("PIPScope")?.Methods.SingleOrDefault(m => m.FullName == Target);
        if (target == null || !target.HasBody || target.Body.Instructions.Count != 594 ||
            Fingerprint(target) != ExpectedBodyHash)
            throw new InvalidOperationException(
                "Expected exact original PIPScope.UpdateAllScopes IL; refusing " +
                "changed/already-patched method");
        var calls = target.Body.Instructions
                        .Where(i => i.Operand is MethodReference r && r.FullName == OldSetter)
                        .ToArray();
        if (calls.Length != 3 ||
            !calls.Select(i => target.Body.Instructions.IndexOf(i)).SequenceEqual(Sites))
            throw new InvalidOperationException("Unexpected scope setter sites");
        foreach (int index in Sites) {
            var i = target.Body.Instructions[index];
            if (i.OpCode != OpCodes.Callvirt ||
                target.Body.Instructions[index - 1].OpCode !=
                    (index == 580 ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1))
                throw new InvalidOperationException("Unexpected scope setter shape");
        }
        var reference = game.MainModule.ImportReference(helper);
        foreach (var i in calls) {
            i.OpCode = OpCodes.Call;
            i.Operand = reference;
        }
        return target;
    }
}
