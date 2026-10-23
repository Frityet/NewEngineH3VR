using Mono.Cecil;
using System.Text.Json;
var old = AssemblyDefinition.ReadAssembly("audit-managed/ported/UnityEngine.UI.dll");
var next = AssemblyDefinition.ReadAssembly("build/ugui/runtime/UnityEngine.UI.dll");
static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var x in Flatten(n))
            yield return x;
}
var nt = next.MainModule.Types.SelectMany(Flatten).ToDictionary(t => t.FullName);
bool Serialized(FieldDefinition f) => !f.IsStatic && !f.IsNotSerialized &&
                                      (f.IsPublic ||
                                       f.CustomAttributes.Any(a => a.AttributeType.Name ==
                                                                   "SerializeField"));
var removed = new List<object>();
var changed = new List<object>();
var fields = new List<object>();
foreach (var t in old.MainModule.Types.SelectMany(Flatten)) {
    if (!nt.TryGetValue(t.FullName, out var n)) {
        removed.Add(t.FullName);
        continue;
    }
    foreach (var f in t.Fields.Where(Serialized)) {
        var nf = n.Fields.FirstOrDefault(
            x => x.Name == f.Name ||
                 x.CustomAttributes.Any(
                     a => a.AttributeType.Name == "FormerlySerializedAsAttribute" &&
                          a.ConstructorArguments.Any(c => (string?)c.Value == f.Name)));
        if (nf == null)
            fields.Add(new { type = t.FullName, field = f.Name, oldType = f.FieldType.FullName,
                             change = "removed" });
        else if (nf.FieldType.FullName != f.FieldType.FullName)
            changed.Add(new { type = t.FullName, field = f.Name, oldType = f.FieldType.FullName,
                              newType = nf.FieldType.FullName });
    }
}
var report = new { oldIdentity = old.Name.FullName,
                   newIdentity = next.Name.FullName,
                   references = next.MainModule.AssemblyReferences.Select(a => a.FullName),
                   removedTypes = removed,
                   removedSerializedFields = fields,
                   changedSerializedFields = changed };
File.WriteAllText("build/ugui/serialization-audit.json",
                  JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
