using Mono.Cecil;
using System.Text.Json;
string input = args[0], manifest = args[1], output = args[2];
var patched = JsonDocument.Parse(File.ReadAllText(manifest))
                  .RootElement.EnumerateArray()
                  .ToDictionary(x => x.GetProperty("method").GetString()!,
                                x => x.GetProperty("kind").GetString());
static IEnumerable<TypeDefinition> Types(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var v in Types(n))
            yield return v;
}
var calls = new Dictionary<string, HashSet<string>>();
foreach (var file in Directory.GetFiles(input, "*.dll")) {
    using var assembly = AssemblyDefinition.ReadAssembly(file);
    foreach (var method in assembly.MainModule.Types.SelectMany(Types)
                 .SelectMany(t => t.Methods)
                 .Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions) {
            if (instruction.Operand is not MethodReference call ||
                call.DeclaringType.Namespace != "Valve.VR" ||
                !call.DeclaringType.Name.StartsWith("CVR"))
                continue;
            if (!calls.TryGetValue(call.FullName, out var callers))
                calls[call.FullName] = callers = new();
            callers.Add(method.FullName);
        }
}
var rows = calls.OrderBy(p => p.Key)
               .Select(p => new { api = p.Key,
                                  implementation = patched.GetValueOrDefault(p.Key, "not patched"),
                                  callers = p.Value.OrderBy(x => x).ToArray() })
               .ToArray();
File.WriteAllText(output,
                  JsonSerializer.Serialize(rows,
                                           new JsonSerializerOptions { WriteIndented = true }));
foreach (var group in rows.GroupBy(x => x.implementation))
    Console.WriteLine(group.Key + ": " + group.Count() + " APIs / " +
                      group.Sum(x => x.callers.Length) + " call sites");
