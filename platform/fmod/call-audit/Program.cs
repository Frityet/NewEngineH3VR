using Mono.Cecil;
using System.Text.Json;
var source = Path.GetFullPath("build/composed/final");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(source);
resolver.AddSearchDirectory("/Applications/Unity/Unity.app/Contents/PlaybackEngines/" +
                            "MacStandaloneSupport/Variations/mono/Managed");
resolver.AddSearchDirectory(
    "/Applications/Unity/Unity.app/Contents/MonoBleedingEdge/lib/mono/4.7.1-api");
using var
    game = AssemblyDefinition.ReadAssembly(Path.Combine(source, "NativeH3VR.Game.dll"),
                                           new ReaderParameters { AssemblyResolver = resolver });
IEnumerable<TypeDefinition> Types(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var x in Types(n))
            yield return x;
}
bool Fmod(TypeReference t) =>
    t.Namespace == "FMOD" || t.Namespace.StartsWith("FMOD.") || t.Namespace == "FMODUnity";
var direct = new List<object>();
var queue = new Queue<MethodDefinition>();
var visited = new HashSet<string>();
var imports = new HashSet<string>();
var errors = new List<string>();
foreach (var caller in game.MainModule.Types.SelectMany(Types)
             .SelectMany(t => t.Methods)
             .Where(m => m.HasBody))
    foreach (var i in caller.Body.Instructions) {
        if (i.Operand is MethodReference m && Fmod(m.DeclaringType)) {
            direct.Add(new { caller = caller.FullName, target = m.FullName });
            try {
                var definition = m.Resolve();
                if (definition != null)
                    queue.Enqueue(definition);
            } catch (Exception e) {
                errors.Add(e.Message);
            }
        }
    }
while (queue.TryDequeue(out var method)) {
    if (!visited.Add(method.FullName))
        continue;
    if (method.IsPInvokeImpl) {
        imports.Add(method.PInvokeInfo.Module.Name + ":" + method.PInvokeInfo.EntryPoint);
        continue;
    }
    if (!method.HasBody)
        continue;
    foreach (var i in method.Body.Instructions)
        if (i.Operand is MethodReference m && Fmod(m.DeclaringType))
            try {
                var definition = m.Resolve();
                if (definition != null)
                    queue.Enqueue(definition);
            } catch (Exception e) {
                errors.Add(e.Message);
            }
}
var report = new { source,
                   directGameCalls = direct,
                   reachableFmodMethods = visited.Order().ToArray(),
                   reachableNativeImports = imports.Order().ToArray(),
                   staticCallGraphOnly = true,
                   errors };
Directory.CreateDirectory("build/fmod/parity");
File.WriteAllText("build/fmod/parity/call-surface.json",
                  JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(
    $"Game FMOD call sites {direct.Count}, reachable FMOD/FMODY methods {visited.Count}, native imports {imports.Count}, resolve errors {errors.Count}");
