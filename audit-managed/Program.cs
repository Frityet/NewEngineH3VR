using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
var root = args.Length > 0? args[0]
    : System.IO.Path.Combine(
          System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
          "Library/Application Support/Steam/steamapps/common/H3VR/h3vr_Data/Managed");
var outdir = args.Length > 1? args[1] : "audit-managed/results";
Directory.CreateDirectory(outdir);
var pinvokes = new List<object>();
var platforms = new List<object>();
var unityCalls = new Dictionary<string, HashSet<string>>();
var calls = new List<object>();
var info = new List<object>();
var all = new Dictionary<string, MethodDefinition>();
var edges = new Dictionary<string, HashSet<string>>();
var rootAssembly = new Dictionary<string, string>();
static IEnumerable<TypeDefinition> Types(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var nt in Types(n))
            yield return nt;
}
var assemblies =
    Directory.GetFiles(root, "*.dll").Select(p => AssemblyDefinition.ReadAssembly(p)).ToArray();
foreach (var a in assemblies) {
    var path = Path.Combine(root, a.Name.Name + ".dll");
    var ts = a.MainModule.Types.SelectMany(Types).ToArray();
    var ms = ts.SelectMany(t => t.Methods).ToArray();
    info.Add(new {
        assembly = a.Name.Name, version = a.Name.Version.ToString(),
        runtime = a.MainModule.RuntimeVersion, architecture = a.MainModule.Architecture.ToString(),
        attributes = a.MainModule.Attributes.ToString(),
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
        typeCount = ts.Length, methodCount = ms.Length, ilMethodCount = ms.Count(m => m.HasBody),
        pinvokeCount = ms.Count(m => m.HasPInvokeInfo),
        internalCallCount = ms.Count(m => m.IsInternalCall),
        refs = a.MainModule.AssemblyReferences.Select(r => r.FullName).ToArray(),
        namespaces = ts.GroupBy(t => t.Namespace)
                         .OrderByDescending(g => g.Count())
                         .Select(g => new { name = g.Key, count = g.Count() })
                         .ToArray()
    });
    foreach (var m in ms) {
        string key = a.Name.Name + "!" + m.FullName;
        all[key] = m;
        rootAssembly[key] = a.Name.Name;
        var es = new HashSet<string>();
        edges[key] = es;
        if (m.HasPInvokeInfo)
            pinvokes.Add(new { assembly = a.Name.Name, method = m.FullName,
                               library = m.PInvokeInfo.Module.Name,
                               entryPoint = m.PInvokeInfo.EntryPoint,
                               attributes = m.PInvokeInfo.Attributes.ToString() });
        if (!m.HasBody)
            continue;
        foreach (var i in m.Body.Instructions) {
            if (i.Operand is MethodReference target) {
                string scope = target.DeclaringType.Scope.Name.Replace(".dll", "");
                es.Add(scope + "!" + target.FullName);
                if (scope.StartsWith("UnityEngine")) {
                    string api = target.FullName;
                    if (!unityCalls.TryGetValue(api, out var users)) {
                        users = new HashSet<string>();
                        unityCalls[api] = users;
                    }
                    users.Add(a.Name.Name + "!" + m.FullName);
                }
                if (target.DeclaringType.FullName.Contains("Registry") ||
                    target.DeclaringType.FullName.Contains("Process") ||
                    target.DeclaringType.FullName.Contains("Environment") ||
                    target.DeclaringType.FullName.Contains("Marshal") ||
                    target.DeclaringType.FullName.Contains("AppDomain") ||
                    target.FullName.Contains("get_platform"))
                    platforms.Add(new { assembly = a.Name.Name, caller = m.FullName,
                                        ilOffset = i.Offset, target = target.FullName });
            } else if (i.Operand is string s &&
                       (s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        s.Contains(".exe", StringComparison.OrdinalIgnoreCase) ||
                        s.Contains("C:\\") || s.Contains("user32") || s.Contains("kernel32")))
                platforms.Add(new { assembly = a.Name.Name, caller = m.FullName,
                                    ilOffset = i.Offset, literal = s });
        }
    }
}
foreach (var a in assemblies.Where(
             a => !a.Name.Name.StartsWith("System") && !a.Name.Name.StartsWith("Mono") &&
                  a.Name.Name != "mscorlib" && !a.Name.Name.StartsWith("UnityEngine")))
    foreach (var m in a.MainModule.Types.SelectMany(Types)
                 .SelectMany(t => t.Methods)
                 .Where(m => m.HasPInvokeInfo)) {
        var needle = a.Name.Name + "!" + m.FullName;
        var users = edges.Where(kv => kv.Value.Contains(needle)).Select(kv => kv.Key).ToArray();
        calls.Add(new { pinvoke = needle, library = m.PInvokeInfo.Module.Name, callers = users });
    }
void Write(string name, object data) => File.WriteAllText(
    Path.Combine(outdir, name),
    JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
Write("assemblies.json", info);
Write("pinvokes.json", pinvokes);
Write("native-callers.json", calls);
Write("platform-references.json", platforms);
Write("unity-api-usage.json",
      unityCalls.Select(kv => new { api = kv.Key, callers = kv.Value.Order().ToArray() })
          .OrderBy(o => o.api));
Console.WriteLine(
    $"Audited {assemblies.Length} assemblies, {all.Count} methods, {pinvokes.Count} P/Invokes, {unityCalls.Count} distinct Unity calls. Reports: {Path.GetFullPath(outdir)}");
var newroot = "/Applications/Unity/Unity.app/Contents/PlaybackEngines/MacStandaloneSupport/" +
              "Variations/mono/Managed";
var newasm = Directory.GetFiles(newroot, "UnityEngine*.dll")
                 .Select(p => AssemblyDefinition.ReadAssembly(p))
                 .ToArray();
var newtypes = newasm.SelectMany(a => a.MainModule.Types.SelectMany(Types))
                   .GroupBy(t => t.FullName)
                   .ToDictionary(g => g.Key, g => g.First());
var missingTypes = new Dictionary<string, HashSet<string>>();
var missingMethods = new Dictionary<string, HashSet<string>>();
var missingFields = new Dictionary<string, HashSet<string>>();
var renamedTypes = new Dictionary<string, string>();
bool IsUnity(TypeReference t) => t.Scope.Name is "UnityEngine" or "UnityEngine.dll" or
                                                 "UnityEngine.VR" or "UnityEngine.VR.dll";
TypeDefinition? Lookup(TypeReference t) {
    var n = t.GetElementType().FullName;
    if (newtypes.TryGetValue(n, out var v))
        return v;
    var renamed = n.Replace("UnityEngine.VR.VR", "UnityEngine.XR.XR")
                      .Replace("UnityEngine.VR.", "UnityEngine.XR.");
    if (newtypes.TryGetValue(renamed, out v)) {
        renamedTypes[n] = renamed;
        return v;
    }
    return null;
}
string Normalize(string s) => s.Replace("UnityEngine.VR.VR", "UnityEngine.XR.XR")
                                  .Replace("UnityEngine.VR.", "UnityEngine.XR.");
string TypeSig(TypeReference t) => t switch {
    GenericParameter p => (p.Type == GenericParameterType.Method ? "!!" : "!") + p.Position,
    ArrayType ar => TypeSig(ar.ElementType) + "[" + new string(',', ar.Rank - 1) + "]",
    ByReferenceType br => TypeSig(br.ElementType) + "&",
    PointerType ptr => TypeSig(ptr.ElementType) + "*",
    GenericInstanceType gt => TypeSig(gt.ElementType) + "<" +
                              string.Join(",", gt.GenericArguments.Select(TypeSig)) + ">",
    _ => Normalize(t.FullName)
};
string Sig(MethodReference m) =>
    Normalize(m.Name + "(" + string.Join(",", m.Parameters.Select(p => TypeSig(p.ParameterType))) +
              ")->" + TypeSig(m.ReturnType));
bool HasMethod(TypeDefinition td, MethodReference called) {
    for (var at = td; at != null; at = at.BaseType == null ? null : Lookup(at.BaseType))
        if (at.Methods.Any(n => Sig(n) == Sig(called)))
            return true;
    return false;
}
void Missing(Dictionary<string, HashSet<string>> dict, string key, string caller) {
    if (!dict.TryGetValue(key, out var us)) {
        us = new HashSet<string>();
        dict[key] = us;
    }
    us.Add(caller);
}
foreach (var a in assemblies.Where(
             a => a.Name.Name != "UnityEngine" && a.Name.Name != "UnityEngine.VR" &&
                  !a.Name.Name.StartsWith("System") && a.Name.Name != "mscorlib" &&
                  !a.Name.Name.StartsWith("Mono"))) {
    foreach (var t in a.MainModule.GetTypeReferences().Where(IsUnity)) {
        if (Lookup(t) == null)
            Missing(missingTypes, t.FullName, a.Name.Name);
    }
    foreach (var m in a.MainModule.Types.SelectMany(Types)
                 .SelectMany(t => t.Methods)
                 .Where(m => m.HasBody))
        foreach (var i in m.Body.Instructions) {
            if (i.Operand is MethodReference called && IsUnity(called.DeclaringType)) {
                var td = Lookup(called.DeclaringType);
                var cmp = called is GenericInstanceMethod gm ? gm.ElementMethod : called;
                if (td != null && called.DeclaringType is not ArrayType && !HasMethod(td, cmp))
                    Missing(missingMethods, called.FullName, a.Name.Name + "!" + m.FullName);
            }
            if (i.Operand is FieldReference f && IsUnity(f.DeclaringType)) {
                var td = Lookup(f.DeclaringType);
                if (td != null &&
                    !td.Fields.Any(n => n.Name == f.Name && Normalize(n.FieldType.FullName) ==
                                                                Normalize(f.FieldType.FullName)))
                    Missing(missingFields, f.FullName, a.Name.Name + "!" + m.FullName);
            }
        }
}
Write("unity2020-missing-types.json", missingTypes);
Write("unity2020-renamed-types.json", renamedTypes);
Write("unity2020-missing-methods.json", missingMethods);
Write("unity2020-missing-fields.json", missingFields);
Console.WriteLine(
    $"Against Unity 2020: {missingTypes.Count} missing type names, {renamedTypes.Count} VR->XR mappings, {missingMethods.Count} missing method shapes (may include inherited members), {missingFields.Count} missing fields.");

foreach (var name in new[] { "UnityEngine.XR.XRDevice", "UnityEngine.XR.XRSettings",
                             "UnityEngine.WWW", "UnityEngine.Graphics", "UnityEngine.Texture2D",
                             "UnityEngine.Rendering.GraphicsSettings" }) {
    Console.WriteLine("TYPE " + name);
    foreach (var m in newtypes[name].Methods.Where(
                 m => m.Name.Contains("model") || m.Name.Contains("renderScale") ||
                      m.Name.Contains("eyeTextureResolution") || m.Name.Contains("AudioClip") ||
                      m.Name.Contains("DrawProceduralIndirect") ||
                      m.Name.Contains("renderPipelineAsset")))
        Console.WriteLine(m.FullName);
}
foreach (var a in assemblies)
    foreach (var m in a.MainModule.Types.SelectMany(Types)
                 .SelectMany(t => t.Methods)
                 .Where(m => m.HasBody))
        foreach (var i in m.Body.Instructions)
            if (i.Operand is FieldReference f &&
                f.DeclaringType.FullName == "UnityEngine.UIVertex" && f.Name.StartsWith("uv"))
                Console.WriteLine("UVFIELD " + a.Name.Name + " " + i.OpCode + " " + f.FullName +
                                  " in " + m.FullName);
