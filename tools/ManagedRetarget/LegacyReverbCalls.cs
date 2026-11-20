using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class LegacyReverbCalls
{
    private static readonly string[] Parameters =
    [
        "Room", "RoomHF", "RoomLF", "DecayTime", "DecayHFRatio", "Reflections",
        "ReflectionsDelay", "Reverb", "ReverbDelay", "HFReference", "LFReference", "Diffusion", "Density"
    ];

    internal static int Apply(ModuleDefinition game, TypeDefinition helper)
    {
        var settings = game.GetType("FistVR.SM/ReverbSettings");
        var method = settings?.Methods.SingleOrDefault(m =>
            m.Name == "UpdateMixerParams" && m.HasBody && m.HasThis && m.Parameters.Count == 0);
        if (method == null)
            return 0;

        var calls = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt &&
            i.Operand is MethodReference original && original.Name == "SetFloat" &&
            original.DeclaringType.FullName == "UnityEngine.Audio.AudioMixer" &&
            original.HasThis && original.ReturnType.FullName == "System.Boolean" &&
            original.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(["System.String", "System.Single"]))
            .ToArray();
        if (calls.Length != Parameters.Length)
            return 0;

        // Guard each stock receiver, literal, value field, and ignored return value.
        for (int index = 0; index < calls.Length; index++)
        {
            var site = calls[index];
            string parameter = Parameters[index];
            if (site.Next?.OpCode != OpCodes.Pop ||
                site.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference value } ||
                value.DeclaringType.FullName != settings!.FullName || value.Name != parameter ||
                site.Previous.Previous?.OpCode != OpCodes.Ldarg_0 ||
                site.Previous.Previous.Previous is not { OpCode.Code: Code.Ldstr, Operand: string literal } ||
                literal != "MReverb_" + parameter ||
                site.Previous.Previous.Previous.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference mixer } ||
                mixer.DeclaringType.FullName != settings.FullName || mixer.Name != "MasterMixer")
                return 0;
        }

        var replacement = game.ImportReference(helper.Methods.Single(m => m.Name == "SetFloat" && m.IsStatic));
        foreach (var site in calls)
        {
            site.OpCode = OpCodes.Call;
            site.Operand = replacement;
        }
        return calls.Length;
    }
}
