using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;
static IEnumerable<TypeDefinition> Types(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var k in Types(n))
            yield return k;
}
var results = new List<object>();
foreach (var dir in args.Take(args.Length - 1))
    foreach (var path in Directory.GetFiles(dir, "*.dll")
                 .Where(p => !Path.GetFileName(p).StartsWith("UnityEngine.") &&
                             !Path.GetFileName(p).StartsWith("UnityEditor.") &&
                             !Path.GetFileName(p).StartsWith("System.") &&
                             !new[] { "mscorlib.dll", "netstandard.dll", "Mono.Security.dll" }
                                  .Contains(Path.GetFileName(p)))) {
        using var a = AssemblyDefinition.ReadAssembly(path);
        var literals = new List<object>();
        var calls = new List<object>();
        var genericSaves = new List<object>();
        foreach (var t in a.MainModule.Types.SelectMany(Types))
            foreach (var m in t.Methods.Where(m => m.HasBody))
                foreach (var i in m.Body.Instructions) {
                    if (i.OpCode == OpCodes.Ldstr && i.Operand is string s &&
                        (s.Contains("Assembly-CSharp") || s.Contains("Assembly-UnityScript") ||
                         s.Contains("NativeH3VR.Game") || s.Contains("NativeH3VR.FirstPass") ||
                         s.Contains("NativeH3VR.UnityScript")))
                        literals.Add(new { method = m.FullName, offset = i.Offset, value = s });
                    if (i.Operand is MethodReference r &&
                        ((r.DeclaringType.FullName == "System.Type" && r.Name == "GetType") ||
                         (r.DeclaringType.FullName == "System.Reflection.Assembly" &&
                          r.Name.StartsWith("Load")) ||
                         r.Name == "get_AssemblyQualifiedName" ||
                         r.Name == "set_TypeNameHandling" || r.Name.Contains("AssemblyResolve")))
                        calls.Add(new { method = m.FullName, offset = i.Offset, call = r.FullName,
                                        context = m.Body.Instructions
                                                      .Skip(Math.Max(
                                                          0, m.Body.Instructions.IndexOf(i) - 8))
                                                      .Take(10)
                                                      .Select(x => x.ToString())
                                                      .ToArray() });
                    if (i.Operand is GenericInstanceMethod g &&
                        (g.DeclaringType.FullName.StartsWith("ES2") ||
                         g.DeclaringType.FullName.Contains("JsonConvert")))
                        genericSaves.Add(
                            new { method = m.FullName, call = g.FullName,
                                  args = g.GenericArguments.Select(x => x.FullName).ToArray() });
                }
        var typeNames = a.MainModule.Types.SelectMany(Types).Select(t => t.FullName).ToArray();
        var serializerMethods =
            a.MainModule.Types.SelectMany(Types)
                .Where(t => t.FullName == "ES2Init" || t.FullName.StartsWith("ES2UserType_") ||
                            t.FullName == "ES2Type")
                .SelectMany(t => t.Methods)
                .Where(m => m.HasBody)
                .Select(m => new {
                    method = m.FullName,
                    il = m.Body.Instructions
                             .Select(
                                 i => i.OpCode.Name + " " +
                                      (i.Operand switch {
                                          Instruction target =>
                                              "@" + m.Body.Instructions.IndexOf(target),
                                          Instruction[] targets => string.Join(
                                              ",", targets.Select(
                                                       v => "@" + m.Body.Instructions.IndexOf(v))),
                                          _ => i.Operand?.ToString()
                                      }))
                             .ToArray()
                })
                .ToArray();
        results.Add(new { path, assembly = a.Name.FullName, literals, calls, genericSaves,
                          typeNames, serializerMethods });
    }
File.WriteAllText(args.Last(),
                  JsonSerializer.Serialize(results,
                                           new JsonSerializerOptions { WriteIndented = true }));
