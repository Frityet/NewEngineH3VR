using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;
static IEnumerable<TypeDefinition> Types(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var x in Types(n))
            yield return x;
}
var calls = new List<object>();
var imports = new List<object>();
var strings = new List<object>();
int assemblies = 0, methods = 0;
var api = new HashSet<string>();
var fmodCallers = new HashSet<string>();
foreach (var file in Directory.GetFiles("recovered/AuxiliaryFiles/GameAssemblies", "*.dll")) {
    if (Path.GetFileName(file).StartsWith("UnityEngine") ||
        Path.GetFileName(file).StartsWith("System") ||
        Path.GetFileName(file) is "mscorlib.dll" or "Mono.Security.dll")
        continue;
    using var a = AssemblyDefinition.ReadAssembly(file);
    assemblies++;
    foreach (var m in a.MainModule.Types.SelectMany(Types).SelectMany(t => t.Methods)) {
        methods++;
        if (m.IsPInvokeImpl && System.Text.RegularExpressions.Regex.IsMatch(
                                   m.PInvokeInfo.Module.Name, "spatial|oculus|hrtf",
                                   System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            imports.Add(new { method = m.FullName, library = m.PInvokeInfo.Module.Name });
        if (!m.HasBody)
            continue;
        foreach (var i in m.Body.Instructions) {
            if (i.Operand is MethodReference mr) {
                if (mr.DeclaringType.FullName is "UnityEngine.AudioSource" or
                                                 "UnityEngine.AudioSettings") {
                    api.Add(mr.FullName);
                    if (mr.Name.Contains("Spatializ", StringComparison.OrdinalIgnoreCase))
                        calls.Add(new { caller = m.FullName, called = mr.FullName });
                }
                if (mr.DeclaringType.Namespace.StartsWith("FMOD") &&
                    !m.DeclaringType.Namespace.StartsWith("FMOD"))
                    fmodCallers.Add(m.DeclaringType.FullName);
            }
            if (i.OpCode == OpCodes.Ldstr && i.Operand is string s &&
                System.Text.RegularExpressions.Regex.IsMatch(
                    s, "spatializer|AudioPluginMsHRTF|AudioPluginOculus",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                strings.Add(new { caller = m.FullName, value = s });
        }
    }
}
var report = new { assemblies,
                   methods,
                   spatializerApiCalls = calls,
                   spatializerNativeImports = imports,
                   spatializerStringReferences = strings,
                   audioApis = api.Order().ToArray(),
                   fmodCallers = fmodCallers.Order().ToArray() };
File.WriteAllText("build/audio-spatial/managed.json",
                  JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(
    $"{assemblies} assemblies, {methods} methods: spatializer calls {calls.Count}, native imports {imports.Count}, strings {strings.Count}");
