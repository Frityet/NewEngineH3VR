using Mono.Cecil;
using Mono.Cecil.Cil;

namespace H3VR.Converter.Tests;

internal static class LegacyReverbTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("legacy reverb mixer repair requires the manager GUID, parameter GUID and exposed CRC", MixerRepair);
        yield return ("legacy reverb rewrites the recognized stock method and preserves unknown mixer calls", NarrowPatch);
    }

    private static void MixerRepair()
    {
        string project = Path.Combine(Path.GetTempPath(), "nativeh3vr-mixer-" + Guid.NewGuid().ToString("N"));
        string resources = Path.Combine(project, "Assets/Resources");
        string mixer = Path.Combine(resources, "audiomixers/Master.mixer");
        string manager = Path.Combine(resources, "prefabs/_managers/_GameManager.prefab");
        string unrelated = Path.Combine(resources, "audiomixers/SoundOptions.mixer");
        const string guid = "7388ffafc8d64e59aa28e0542377d4ec";
        Directory.CreateDirectory(Path.GetDirectoryName(mixer)!);
        Directory.CreateDirectory(Path.GetDirectoryName(manager)!);
        try
        {
            string yaml = """
                --- !u!241 &24100000
                AudioMixerController:
                  m_Name: Master
                  m_ExposedParameters:
                  - guid: 00000000000000000000000000000001
                    name: MHuomjH
                  - guid: 00000000000000000000000000000002
                    name: UnverifiedRoomHF
                  - guid: 0000000000000000000000000000000d
                    name: vntwjVJ
                --- !u!244 &24400000
                AudioMixerEffectController:
                  m_EffectName: SFX Reverb
                  m_Parameters:
                """ + "\n" + string.Concat(Enumerable.Range(0, 14).Select(i =>
                    $"  - m_ParameterName: Param_{i}\n    m_GUID: {i:x32}\n"));
            File.WriteAllText(mixer, yaml);
            File.WriteAllText(unrelated, yaml);
            File.WriteAllText(mixer + ".meta", $"guid: {guid}\n");
            File.WriteAllText(manager, "  MasterMixer: {fileID: 24100000, guid: 00000000000000000000000000000000, type: 2}\n");
            if (LegacyReverbMixer.Repair(project) != 0 || File.ReadAllText(mixer) != yaml)
                throw new Exception("Mixer changed without verified manager reference.");

            File.WriteAllText(manager, $"  MasterMixer: {{fileID: 24100000, guid: {guid}, type: 2}}\n");
            string wrongEffect = yaml.Replace("m_EffectName: SFX Reverb", "m_EffectName: Chorus", StringComparison.Ordinal);
            File.WriteAllText(mixer, wrongEffect);
            if (LegacyReverbMixer.Repair(project) != 0 || File.ReadAllText(mixer) != wrongEffect)
                throw new Exception("Mixer changed despite unexpected native effect type.");

            string ambiguous = yaml.Replace("    name: MHuomjH\n",
                "    name: MHuomjH\n  - guid: 00000000000000000000000000000001\n    name: Duplicate\n",
                StringComparison.Ordinal);
            File.WriteAllText(mixer, ambiguous);
            if (LegacyReverbMixer.Repair(project) != 0 || File.ReadAllText(mixer) != ambiguous)
                throw new Exception("Mixer changed despite ambiguous exposed name binding.");

            File.WriteAllText(mixer, yaml);
            var expected = yaml.Replace("name: MHuomjH", "name: MReverb_Room", StringComparison.Ordinal)
                .Replace("m_ParameterName: Param_1\n", "m_ParameterName: Room\n", StringComparison.Ordinal);
            if (LegacyReverbMixer.Repair(project) != 1 || File.ReadAllText(mixer) != expected ||
                File.ReadAllText(unrelated) != yaml || LegacyReverbMixer.Repair(project) != 0)
                throw new Exception("Mixer repair must preserve unrelated values, unsupported bindings, and repaired assets.");
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    private static void NarrowPatch()
    {
        using var helper = AssemblyDefinition.CreateAssembly(new("NativeH3VR.Compat", new Version(1, 0)), "NativeH3VR.Compat", ModuleKind.Dll);
        var adapter = new TypeDefinition("NativeH3VR", "LegacyReverbParameters", TypeAttributes.Public);
        helper.MainModule.Types.Add(adapter);
        adapter.Methods.Add(new MethodDefinition("SetFloat", MethodAttributes.Public | MethodAttributes.Static, helper.MainModule.TypeSystem.Boolean));

        foreach (bool expected in new[] { true, false })
        {
            using var assembly = AssemblyDefinition.CreateAssembly(new("Assembly-CSharp", new Version(1, 0)), "Assembly-CSharp", ModuleKind.Dll);
            var module = assembly.MainModule;
            var manager = new TypeDefinition("FistVR", "SM", TypeAttributes.Public);
            var settings = new TypeDefinition("", "ReverbSettings", TypeAttributes.NestedPublic);
            module.Types.Add(manager);
            manager.NestedTypes.Add(settings);
            var mixerType = new TypeReference("UnityEngine.Audio", "AudioMixer", module,
                new AssemblyNameReference("UnityEngine", new Version(0, 0)));
            var mixer = new FieldDefinition("MasterMixer", FieldAttributes.Public, mixerType);
            settings.Fields.Add(mixer);
            var setFloat = new MethodReference("SetFloat", module.TypeSystem.Boolean, mixerType) { HasThis = true };
            setFloat.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            setFloat.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
            var method = new MethodDefinition("UpdateMixerParams", MethodAttributes.Private, module.TypeSystem.Void);
            settings.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            string[] parameters = ["Room", "RoomHF", "RoomLF", "DecayTime", "DecayHFRatio", "Reflections",
                "ReflectionsDelay", "Reverb", "ReverbDelay", "HFReference", "LFReference", "Diffusion", "Density"];
            var calls = new List<Instruction>();
            foreach (var name in parameters)
            {
                var field = new FieldDefinition(name, FieldAttributes.Public, module.TypeSystem.Single);
                settings.Fields.Add(field);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, mixer);
                il.Emit(OpCodes.Ldstr, expected || name != "Room" ? "MReverb_" + name : "UnknownRoomName");
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, field);
                var call = il.Create(OpCodes.Callvirt, setFloat);
                il.Append(call);
                calls.Add(call);
                il.Emit(OpCodes.Pop);
            }
            il.Emit(OpCodes.Ret);
            int changes = LegacyReverbCalls.Apply(module, adapter);
            if (expected && (changes != 13 || calls.Any(i => i.OpCode != OpCodes.Call ||
                    ((MethodReference)i.Operand).DeclaringType.FullName != adapter.FullName)) ||
                !expected && (changes != 0 || calls.Any(i => i.OpCode != OpCodes.Callvirt || i.Operand != setFloat)))
                throw new Exception("Unexpected modification of stock reverb call sites.");
        }
    }
}
