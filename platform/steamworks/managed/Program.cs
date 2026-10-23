using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;
if (args.Length != 2)
    throw new ArgumentException("Usage: Patch <retargeted FirstPass.dll> <patched FirstPass.dll>");
using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
var module = assembly.MainModule;
static IEnumerable<TypeDefinition> All(TypeDefinition type) {
    yield return type;
    foreach (var nested in type.NestedTypes)
        foreach (var item in All(nested))
            yield return item;
}
var steam = module.Types.Where(t => t.Namespace == "Steamworks").SelectMany(All).ToArray();
var changes = new List<object>();
foreach (var type in steam) {
    if (type.PackingSize == 8) {
        type.PackingSize = 4;
        changes.Add(new { type = type.FullName, operation = "Darwin pack 8 -> 4" });
    }
    foreach (var method in type.Methods.Where(m => m.IsPInvokeImpl)) {
        if (method.PInvokeInfo.Module.Name == "CSteamworks")
            method.PInvokeInfo.Attributes =
                (method.PInvokeInfo.Attributes & ~PInvokeAttributes.CallConvMask) |
                PInvokeAttributes.CallConvCdecl;
    }
}
// SDK 1.41 appended PublishedFileId to this callback; preserve legacy fields and add its ABI tail.
var submitted = steam.Single(t => t.FullName == "Steamworks.SubmitItemUpdateResult_t");
if (!submitted.Fields.Any(f => f.Name == "m_nPublishedFileId")) {
    submitted.Fields.Add(new FieldDefinition("m_nPublishedFileId", FieldAttributes.Public,
                                             module.TypeSystem.UInt64));
    changes.Add(new { type = submitted.FullName,
                      operation = "SDK 1.41 callback tail: PublishedFileId (8 bytes)" });
}
var table = steam.Single(t => t.FullName == "Steamworks.CCallbackBaseVTable");
var callback = table.Fields.Single(f => f.Name == "m_RunCallback");
var callresult = table.Fields.Single(f => f.Name == "m_RunCallResult");
var cbIndex = table.Fields.IndexOf(callback);
var crIndex = table.Fields.IndexOf(callresult);
if (cbIndex > crIndex) {
    table.Fields.Remove(callback);
    table.Fields.Insert(crIndex, callback);
}
changes.Add(new {
    type = table.FullName,
    operation = "Itanium ABI vtable: RunCallback, RunCallResult, GetCallbackSizeBytes"
});
var packsize = steam.Single(t => t.FullName == "Steamworks.Packsize");
packsize.Fields.Single(f => f.Name == "value").Constant = 4;
var test = packsize.Methods.Single(m => m.Name == "Test");
foreach (var instruction in test.Body.Instructions) {
    if (instruction.OpCode == OpCodes.Ldc_I4_S && Convert.ToInt32(instruction.Operand) == 32)
        instruction.Operand = (sbyte)24;
    if (instruction.OpCode == OpCodes.Ldc_I4 && Convert.ToInt32(instruction.Operand) == 32)
        instruction.Operand = 24;
    if (instruction.OpCode == OpCodes.Ldc_I4 && Convert.ToInt32(instruction.Operand) == 616)
        instruction.Operand = 612;
}
changes.Add(new { type = packsize.FullName,
                  operation = "Preserve native packing check: sentinel24 and subscribedfiles612" });
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
assembly.Write(args[1]);
File.WriteAllText(args[1] + ".darwin-abi.json",
                  JsonSerializer.Serialize(changes,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Applied {changes.Count} Darwin ABI changes: {args[1]}");
