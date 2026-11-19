using Mono.Cecil;
using Mono.Cecil.Cil;

public static class MacOptionalFeatures
{
    public static IReadOnlyList<string> Apply(ModuleDefinition module)
    {
        var changed = new List<string>();
        var renderer = module.GetType("Valve.VR.SteamVR_Render");
        if (renderer != null)
        {
            foreach (var name in new[] { "OnEnable", "OnSteamVRInitialized" })
            {
                var method = renderer.Methods.Single(item => item.Name == name && item.HasBody);
                var hook = method.Body.Instructions.Single(instruction =>
                    instruction.OpCode == OpCodes.Callvirt &&
                    instruction.Operand is MethodReference target &&
                    target.FullName == "Valve.VR.EVRScreenshotError Valve.VR.CVRScreenshots::HookScreenshot(Valve.VR.EVRScreenshotType[])");
                if (hook.Next?.OpCode != OpCodes.Pop)
                    throw new InvalidDataException("Unexpected SteamVR screenshot registration result: " + method.FullName);
                // Skip registration and balance the original receiver and argument.
                // The following pop discards the remaining stack value.
                hook.OpCode = OpCodes.Pop;
                hook.Operand = null;
                changed.Add(method.FullName);
            }
        }

        var liv = module.GetType("LIV.SDK.Unity.SharedTextureProtocol");
        if (liv != null)
        {
            var probe = liv.Methods.Single(method => method.Name == "GetIsCaptureActive" &&
                method.IsPInvokeImpl && method.PInvokeInfo.Module.Name == "LIV_MR" &&
                method.IsStatic && method.ReturnType.MetadataType == MetadataType.Boolean);
            var active = liv.Methods.Single(method => method.Name == "get_IsActive" && method.HasBody &&
                method.Body.Instructions.Any(instruction => ReferenceEquals(instruction.Operand, probe)));
            ReturnUnavailable(active);
            changed.Add(active.FullName);
        }

        var forceTube = module.GetType("ForceTubeVRInterface");
        if (forceTube != null)
        {
            foreach (var method in forceTube.Methods.Where(method => method.HasBody &&
                method.Body.Instructions.Any(instruction =>
                    instruction.Operand is MethodDefinition target && target.DeclaringType == forceTube &&
                    target.IsPInvokeImpl && target.PInvokeInfo.Module.Name.StartsWith("ForceTubeVR_API", StringComparison.Ordinal))))
            {
                ReturnUnavailable(method);
                changed.Add(method.FullName);
            }
        }
        if (UseDefaultAudio(module) is { } audio) changed.Add(audio);
        return changed;
    }

    public static string? UseDefaultAudio(ModuleDefinition module)
    {
        var ears = module.GetType("Valve.VR.SteamVR_Ears");
        if (ears is null) return null;
        var onEnable = ears.Methods.Single(method => method.Name == "OnEnable" && method.HasBody);
        var settings = onEnable.Body.Instructions.Single(instruction =>
            instruction.OpCode == OpCodes.Call &&
            instruction.Operand is MethodReference target &&
            target.FullName == "Valve.VR.CVRSettings Valve.VR.OpenVR::get_Settings()");
        // OpenXR leaves endpoint selection to macOS. Follow the listener's
        // existing default path when there is no legacy surround setting.
        settings.OpCode = OpCodes.Ldnull;
        settings.Operand = null;
        return onEnable.FullName;
    }

    private static void ReturnUnavailable(MethodDefinition method)
    {
        method.Body = new MethodBody(method);
        var instructions = method.Body.GetILProcessor();
        switch (method.ReturnType.MetadataType)
        {
            case MetadataType.Boolean:
            case MetadataType.Byte:
                instructions.Append(instructions.Create(OpCodes.Ldc_I4_0));
                break;
            case MetadataType.Void:
                break;
            default:
                throw new InvalidDataException("Unexpected optional feature return type: " + method.FullName);
        }
        instructions.Append(instructions.Create(OpCodes.Ret));
    }
}
