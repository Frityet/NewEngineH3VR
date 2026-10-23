using Mono.Cecil;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

static IEnumerable<TypeDefinition> Types(TypeDefinition type) {
    yield return type;
    foreach (var nested in type.NestedTypes.SelectMany(Types))
        yield return nested;
}
static string MethodFingerprint(ModuleDefinition module) {
    var text = new StringBuilder();
    foreach (var type in module.Types.SelectMany(Types))
        foreach (var method in type.Methods) {
            text.AppendLine(method.FullName + ":" + method.Attributes + ":" +
                            method.ImplAttributes);
            if (!method.HasBody)
                continue;
            text.AppendLine(method.Body.InitLocals + ":" + method.Body.MaxStackSize);
            foreach (var local in method.Body.Variables)
                text.AppendLine(local.VariableType.FullName);
            foreach (var instruction in method.Body.Instructions)
                text.AppendLine(instruction.ToString());
            foreach (var handler in method.Body.ExceptionHandlers)
                text.AppendLine(
                    $"{handler.HandlerType}:{handler.CatchType?.FullName}:" +
                    $"{handler.TryStart?.Offset}:{handler.TryEnd?.Offset}:{handler.HandlerStart?.Offset}:" +
                    $"{handler.HandlerEnd?.Offset}:{handler.FilterStart?.Offset}");
        }
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))
        .ToLowerInvariant();
}

// ImportReference on an external helper also imports its signature's references
// back to the caller assembly. Those must be module-local in Unity plugins.
if (args.Length < 1 || args.Length > 2)
    throw new ArgumentException("<managed directory> [--audit]");
bool audit = args.Length == 2 && args[1] == "--audit";
var results = new List<object>();
int total = 0;
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetFullPath(args[0]));
resolver.AddSearchDirectory("/Applications/Unity/Unity.app/Contents/Managed/UnityEngine");
resolver.AddSearchDirectory(
    "/Applications/Unity/Unity.app/Contents/MonoBleedingEdge/lib/mono/4.7.1-api");
foreach (var file in Directory.GetFiles(args[0], "*.dll").Order()) {
    using var assembly =
        AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = resolver });
    var module = assembly.MainModule;
    var self = module.AssemblyReferences.Where(r => r.Name == assembly.Name.Name).ToArray();
    if (self.Length == 0)
        continue;
    if (self.Any(r => r.FullName != assembly.Name.FullName))
        throw new InvalidOperationException("Conflicting identity with same assembly name: " +
                                            file);
    var references =
        module.GetTypeReferences()
            .Where(t => t.Scope is AssemblyNameReference scope && scope.Name == assembly.Name.Name)
            .ToArray();
    var names = references.Select(t => t.FullName).Distinct().Order().ToArray();
    var methodFingerprint = MethodFingerprint(module);
    if (!audit) {
        foreach (var reference in references) {
            if (module.GetType(reference.FullName) == null)
                throw new InvalidOperationException("Self-scoped type has no local definition: " +
                                                    reference.FullName);
            reference.Scope = module;
        }
        foreach (var reference in self)
            module.AssemblyReferences.Remove(reference);
        var temporary = file + ".local-scope.tmp";
        assembly.Write(temporary);
        File.Move(temporary, file, true);
        using var verified = AssemblyDefinition.ReadAssembly(
            file, new ReaderParameters { AssemblyResolver = resolver });
        if (verified.MainModule.AssemblyReferences.Any(r => r.Name == verified.Name.Name))
            throw new InvalidOperationException("Self assembly reference survived repair: " + file);
        if (MethodFingerprint(verified.MainModule) != methodFingerprint)
            throw new InvalidOperationException(
                "Method signature/body changed during scope repair: " + file);
        foreach (var reference in verified.MainModule.GetTypeReferences().Where(
                     t => names.Contains(t.FullName)))
            if (reference.Scope is ModuleDefinition &&
                reference.Resolve()?.Module != verified.MainModule)
                throw new InvalidOperationException(
                    "Local type did not resolve to its own definition: " + reference.FullName);
    }
    total += self.Length;
    results.Add(new { file = Path.GetFileName(file), selfAssemblyReferences = self.Length,
                      localTypeReferences = references.Length, methodFingerprint,
                      methodBodiesUnchanged = !audit, types = names });
}
var report = new { auditOnly = audit, assemblyReferences = total, assemblies = results };
Console.WriteLine(JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
if (!audit)
    File.WriteAllText(Path.Combine(args[0], "local-reference-repair.json"),
                      JsonSerializer.Serialize(report,
                                               new JsonSerializerOptions { WriteIndented = true }));
