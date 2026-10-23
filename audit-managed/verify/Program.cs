using Mono.Cecil;
using System.Text.Json;
var source = args.Length > 0? args[0] : "audit-managed/ported";
var unity = args.Length > 1? Path.GetFullPath(args[1]) : "/Applications/Unity/Unity.app/Contents";
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetFullPath(source));
resolver.AddSearchDirectory(
    Path.Combine(unity, "PlaybackEngines/MacStandaloneSupport/Variations/mono/Managed"));
resolver.AddSearchDirectory(Path.Combine(unity, "MonoBleedingEdge/lib/mono/4.7.1-api"));
resolver.AddSearchDirectory(Path.Combine(unity, "MonoBleedingEdge/lib/mono/4.7.1-api/Facades"));
var errors = new List<object>();
int typeCount = 0, methodCount = 0, fieldCount = 0;
var apis = new HashSet<string>();
static IEnumerable<TypeDefinition> Types(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var nt in Types(n))
            yield return nt;
}
foreach (var path in Directory.GetFiles(source, "*.dll")) {
    var a =
        AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
    foreach (var reference in a.MainModule.AssemblyReferences.Where(r => r.Name == a.Name.Name))
        errors.Add(new { file = path, kind = "self-assembly-reference", member = reference.FullName,
                         error = "Unity PluginImporter rejects self references; caller-owned " +
                                 "types must use module-local scope" });
    foreach (var t in a.MainModule.GetTypeReferences()) {
        try {
            if (t.Resolve() == null)
                errors.Add(new { file = path, kind = "type", member = t.FullName });
            typeCount++;
        } catch (Exception e) {
            errors.Add(new { file = path, kind = "type", member = t.FullName, error = e.Message });
        }
    }
    foreach (var m in a.MainModule.Types.SelectMany(Types)
                 .SelectMany(t => t.Methods)
                 .Where(m => m.HasBody))
        foreach (var i in m.Body.Instructions) {
            try {
                if (i.Operand is MethodReference mr && mr.DeclaringType is not ArrayType &&
                    (mr.DeclaringType.Scope.Name.StartsWith("UnityEngine") ||
                     mr.DeclaringType.Scope.Name == "NativeH3VR.Compat")) {
                    methodCount++;
                    apis.Add(mr.FullName);
                    if (mr.Resolve() == null)
                        errors.Add(new { file = path, kind = "method", member = mr.FullName,
                                         caller = m.FullName });
                }
                if (i.Operand is FieldReference fr &&
                    fr.DeclaringType.Scope.Name.StartsWith("UnityEngine")) {
                    fieldCount++;
                    if (fr.Resolve() == null)
                        errors.Add(new { file = path, kind = "field", member = fr.FullName,
                                         caller = m.FullName });
                }
            } catch (Exception e) {
                errors.Add(new { file = path, kind = "instruction", member = i.ToString(),
                                 caller = m.FullName, error = e.Message });
            }
        }
}
var report = new { unityRoot = unity,
                   source = Path.GetFullPath(source),
                   typeReferencesChecked = typeCount,
                   engineCallSitesChecked = methodCount,
                   distinctEngineMethods = apis.Count,
                   engineFieldAccessesChecked = fieldCount,
                   errors };
File.WriteAllText(Path.Combine(source, "verification.json"),
                  JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(
    $"Validated {typeCount} type refs, {methodCount} engine/compat call sites ({apis.Count} distinct methods), {fieldCount} fields. Errors: {errors.Count}");
Environment.ExitCode = errors.Count == 0 ? 0 : 1;
