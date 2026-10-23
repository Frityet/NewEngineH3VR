using Mono.Cecil;
using System.Text.Json;
using System.Security.Cryptography;
static IEnumerable<TypeDefinition> Types(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var k in Types(n))
            yield return k;
}
var result = new List<object>();
foreach (var path in Directory.GetFiles(args[0], "*.dll").OrderBy(x => x)) {
    using var a = AssemblyDefinition.ReadAssembly(path);
    result.Add(new {
        path = Path.GetFileName(path), assembly = a.Name.Name,
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
        types =
            a.MainModule.Types.SelectMany(Types).Select(t => t.FullName.Replace('/', '+')).ToArray()
    });
}
File.WriteAllText(args[1],
                  JsonSerializer.Serialize(result,
                                           new JsonSerializerOptions { WriteIndented = true }));
