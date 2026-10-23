using Mono.Cecil;
using System.Text.Json;
var results = new List<object>();
foreach (var path in args) {
    using var a = AssemblyDefinition.ReadAssembly(path);
    foreach (var name in new[] { "UnityEngine.Events.UnityEventBase",
                                 "UnityEngine.Events.PersistentCall" }) {
        var t = a.MainModule.GetType(name);
        if (t == null)
            continue;
        results.Add(
            new { assembly = Path.GetFullPath(path), type = name,
                  fields =
                      t.Fields
                          .Select(f => new {
                              name = f.Name, type = f.FieldType.FullName,
                              notSerialized = f.IsNotSerialized,
                              attributes =
                                  f.CustomAttributes.Select(c => c.AttributeType.FullName).ToArray()
                          })
                          .ToArray(),
                  targetAssemblyGetter =
                      t.Methods.Where(m => m.Name == "get_targetAssemblyTypeName")
                          .Select(m => m.Body.Instructions.Select(i => i.ToString()).ToArray())
                          .ToArray() });
    }
}
Console.WriteLine(JsonSerializer.Serialize(results,
                                           new JsonSerializerOptions { WriteIndented = true }));
