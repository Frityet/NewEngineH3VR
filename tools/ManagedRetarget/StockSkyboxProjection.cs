using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class StockSkyboxProjection
{
    internal static bool ProtectBatchImport(ModuleDefinition game, MethodDefinition isBatchMode)
    {
        var volume = game.GetType("CynBoxProjectionVolume");
        if (volume == null || !volume.CustomAttributes.Any(a =>
                a.AttributeType.FullName == "UnityEngine.ExecuteInEditMode")) return false;
        var update = volume.Methods.SingleOrDefault(m => m.Name == "Update" && m.HasBody && m.Parameters.Count == 0);
        var validate = volume.Methods.SingleOrDefault(m => m.Name == "OnValidate" && m.HasBody && m.Parameters.Count == 0);
        if (update == null || validate == null || !WritesProjection(update) || !WritesProjection(validate)) return false;

        var playing = update.Body.Instructions.SingleOrDefault(i =>
            i.OpCode == OpCodes.Call && i.Operand is MethodReference m &&
            m.FullName == "System.Boolean UnityEngine.Application::get_isPlaying()");
        var branch = playing?.Next;
        if (branch?.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S) || branch.Operand is not Instruction edit ||
            edit.OpCode != OpCodes.Ldloc_0 || edit.Next?.Operand is not MethodReference changed ||
            changed.FullName != "System.Boolean UnityEngine.Transform::get_hasChanged()") return false;

        // The prefab preview has a two-metre volume and shares the live sky material
        // with the outdoor levels. Its edit-mode callbacks can overwrite the asset
        // while the converter imports prefabs or builds the player.
        var batch = game.ImportReference(isBatchMode);
        var updateIl = update.Body.GetILProcessor();
        var check = updateIl.Create(OpCodes.Call, batch);
        updateIl.InsertBefore(edit, check);
        updateIl.InsertBefore(edit, updateIl.Create(OpCodes.Brfalse, edit));
        updateIl.InsertBefore(edit, updateIl.Create(OpCodes.Ret));
        branch.Operand = check;

        var validateIl = validate.Body.GetILProcessor();
        var start = validate.Body.Instructions[0];
        validateIl.InsertBefore(start, validateIl.Create(OpCodes.Call, batch));
        validateIl.InsertBefore(start, validateIl.Create(OpCodes.Brfalse, start));
        validateIl.InsertBefore(start, validateIl.Create(OpCodes.Call, (MethodReference)playing!.Operand));
        validateIl.InsertBefore(start, validateIl.Create(OpCodes.Brtrue, start));
        validateIl.InsertBefore(start, validateIl.Create(OpCodes.Ret));
        return true;
    }

    private static bool WritesProjection(MethodDefinition method) => method.Body.Instructions.Count(i =>
        i.OpCode == OpCodes.Call && i.Operand is MethodReference target &&
        target.FullName == "System.Void CynBoxProjectionVolume::UpdateMaterialValues(UnityEngine.Material,UnityEngine.Transform,UnityEngine.Transform,UnityEngine.Bounds)") == 1;
}
