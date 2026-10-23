using Mono.Cecil;
using System.Text.Json;
var r = new DefaultAssemblyResolver();
r.AddSearchDirectory("recovered/AuxiliaryFiles/GameAssemblies");
r.AddSearchDirectory("/Applications/Unity/Unity.app/Contents/MonoBleedingEdge/lib/mono/2.0-api");
var a =
    AssemblyDefinition.ReadAssembly("recovered/AuxiliaryFiles/GameAssemblies/Assembly-CSharp.dll",
                                    new ReaderParameters { AssemblyResolver = r });
TypeDefinition? Resolve(TypeReference t) {
    try {
        return t.Resolve();
    } catch {
        return null;
    }
}
bool IsObject(TypeDefinition t) => t.FullName == "UnityEngine.Object" ||
                                   (t.BaseType != null && Resolve(t.BaseType) is {} b &&
                                    IsObject(b));
IEnumerable<FieldDefinition> Fields(TypeDefinition t) {
    if (t.BaseType != null && Resolve(t.BaseType) is {} b && b.FullName != "System.Object" &&
        b.FullName != "System.ValueType" && b.FullName != "UnityEngine.MonoBehaviour")
        foreach (var f in Fields(b))
            yield return f;
    foreach (var f in t.Fields)
        if (!f.IsStatic && !f.IsInitOnly && !f.IsNotSerialized &&
            (f.IsPublic || f.CustomAttributes.Any(x => x.AttributeType.Name == "SerializeField")))
            yield return f;
}
object Schema(TypeReference t, int depth = 0) {
    if (depth > 12)
        throw new Exception(t.FullName);
    if (t is ArrayType arr)
        return new { kind = "array", element = Schema(arr.ElementType, depth + 1) };
    if (t is GenericInstanceType gi &&
        gi.ElementType.FullName == "System.Collections.Generic.List`1")
        return new { kind = "array", element = Schema(gi.GenericArguments[0], depth + 1) };
    if (t.Namespace == "System" && t is not GenericInstanceType)
        return new { kind = t.Name };
    var d = Resolve(t) ?? throw new Exception("resolve " + t.FullName);
    if (d.IsEnum)
        return new { kind = "Int32", type = t.FullName };
    if (IsObject(d))
        return new { kind = "PPtr", type = t.FullName };
    return new { kind = "struct", type = t.FullName,
                 fields = Fields(d)
                              .Select(f => new { name = f.Name,
                                                 schema = Schema(f.FieldType, depth + 1) })
                              .ToArray() };
}
var target = a.MainModule.GetType("RenderHeads.Media.AVProVideo.MediaPlayer");
var schema = new {
    kind = "struct", type = target.FullName,
    fields =
        Fields(target).Select(f => new { name = f.Name, schema = Schema(f.FieldType) }).ToArray()
};
File.WriteAllText("build/video/serialization/schema.json",
                  JsonSerializer.Serialize(schema,
                                           new JsonSerializerOptions { WriteIndented = true }));
