using Mono.Cecil;
using System.Text.Json;
using System.Security.Cryptography;
static IEnumerable<TypeDefinition> All(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var v in All(n))
            yield return v;
}
var rows = new List<object>();
foreach (var p in Directory.GetFiles(args[0], "NativeH3VR*.dll")) {
    using var a = AssemblyDefinition.ReadAssembly(p);
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
    foreach (var m in a.MainModule.Types.SelectMany(All)
                 .SelectMany(t => t.Methods)
                 .Where(m => m.HasPInvokeInfo))
        rows.Add(new { assembly = a.Name.Name, path = p, sha256 = hash, method = m.FullName,
                       library = m.PInvokeInfo.Module.Name, entryPoint = m.PInvokeInfo.EntryPoint,
                       convention = m.PInvokeInfo.Attributes.ToString() });
}
File.WriteAllText(args[1],
                  JsonSerializer.Serialize(rows,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(rows.Count + " imports");
var methodProof = new List<object>();
foreach (var p in Directory.GetFiles(args[0], "NativeH3VR*.dll")) {
    using var a = AssemblyDefinition.ReadAssembly(p);
    foreach (var t in a.MainModule.Types.SelectMany(All))
        foreach (var m in t.Methods.Where(m => m.HasBody))
            if ((t.Name == "CVRSettings" && m.Name == "GetBool") ||
                (t.Name == "CVRScreenshots" && m.Name == "HookScreenshot") ||
                (t.Name == "SteamVR_Ears" && m.Name == "OnEnable"))
                methodProof.Add(
                    new { assembly = a.Name.Name, method = m.FullName,
                          instructions = m.Body.Instructions.Select(i => i.ToString()).ToArray() });
}
File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[1]), "unavailable-method-il.json"),
                  JsonSerializer.Serialize(methodProof,
                                           new JsonSerializerOptions { WriteIndented = true }));
