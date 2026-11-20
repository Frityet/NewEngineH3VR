using Mono.Cecil;
using Mono.Cecil.Cil;

public static class LegacyQuickbeltColors
{
    // Captured from the original player with Mathf.GammaToLinearSpace(.3f).
    private const float LegacyTint = 0.3f;
    private const float LinearTint = 0.07323897f;

    public static int Apply(ModuleDefinition module, bool importLegacyLinearColors)
    {
        if (!importLegacyLinearColors || module.Assembly.Name.Name != "NativeH3VR.Game") return 0;

        var slot = module.GetType("FistVR.FVRQuickBeltSlot")
            ?? throw new InvalidDataException("The imported stock quickbelt component is missing.");
        var update = slot.Methods.Single(method => method.FullName == "System.Void FistVR.FVRQuickBeltSlot::Update()" &&
                                                   !method.IsStatic && method.IsPrivate && method.HasBody);
        var calls = update.Body.Instructions.Where(instruction =>
            instruction.OpCode == OpCodes.Callvirt && instruction.Operand is MethodReference method &&
            method.FullName == "System.Void UnityEngine.Material::SetColor(System.String,UnityEngine.Color)").ToArray();
        float[][] colors = [[LegacyTint, LegacyTint, 1f], [LegacyTint, 1f, LegacyTint], [1f, 1f, 1f], [1f, 1f, 1f]];
        if (calls.Length != colors.Length) throw new InvalidDataException("Unexpected stock quickbelt rim assignments.");

        var edits = new List<Instruction>();
        var alreadyConverted = 0;
        for (int i = 0; i < calls.Length; i++)
        {
            var constructor = calls[i].Previous;
            if (constructor.OpCode != OpCodes.Newobj || constructor.Operand is not MethodReference color ||
                color.FullName != "System.Void UnityEngine.Color::.ctor(System.Single,System.Single,System.Single,System.Single)")
                throw new InvalidDataException("Unexpected stock quickbelt color constructor.");
            var alpha = constructor.Previous;
            var blue = alpha.Previous;
            var green = blue.Previous;
            var red = green.Previous;
            var property = red.Previous;
            if (property.OpCode != OpCodes.Ldstr || (string)property.Operand != "_RimColor" ||
                alpha.OpCode != OpCodes.Ldc_R4 || (float)alpha.Operand != 1f)
                throw new InvalidDataException("Unexpected stock quickbelt rim property.");

            Instruction[] rgb = [red, green, blue];
            for (int c = 0; c < rgb.Length; c++)
            {
                var channel = rgb[c];
                if (channel.OpCode != OpCodes.Ldc_R4 || channel.Operand is not float value)
                    throw new InvalidDataException("Unexpected stock quickbelt rim component.");
                if (value == colors[i][c])
                {
                    if (value == LegacyTint) edits.Add(channel);
                }
                else if (value == LinearTint && colors[i][c] == LegacyTint)
                    alreadyConverted++;
                else
                    throw new InvalidDataException("Unexpected stock quickbelt rim color.");
            }
        }

        if (alreadyConverted == 4) return 0;
        if (alreadyConverted != 0 || edits.Count != 4)
            throw new InvalidDataException("Partially transformed stock quickbelt rim colors.");
        foreach (var edit in edits) edit.Operand = LinearTint;
        return edits.Count;
    }
}
