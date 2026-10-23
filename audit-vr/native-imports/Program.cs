using Mono.Cecil;
using System.Text.Json;
using System.Security.Cryptography;
string input = args[0], output = args[1];
static IEnumerable<TypeDefinition> Types(TypeDefinition t) {
    yield return t;
    foreach (var n in t.NestedTypes)
        foreach (var v in Types(n))
            yield return v;
}
var assemblies =
    Directory.GetFiles(input, "*.dll").Select(p => AssemblyDefinition.ReadAssembly(p)).ToArray();
var methods = assemblies.SelectMany(a => a.MainModule.Types.SelectMany(Types))
                  .SelectMany(t => t.Methods)
                  .ToArray();
var reverse = new Dictionary<string, HashSet<string>>();
foreach (var method in methods.Where(m => m.HasBody))
    foreach (var i in method.Body.Instructions)
        if (i.Operand is MethodReference called) {
            if (!reverse.TryGetValue(called.FullName, out var callers))
                reverse[called.FullName] = callers = new();
            callers.Add(method.FullName);
        }
string[] Callers(string name) => reverse.GetValueOrDefault(name, new()).Order().ToArray();
var rows =
    methods.Where(m => m.IsPInvokeImpl)
        .Select(m => new {
            assembly = m.Module.Assembly.Name.Name, method = m.FullName,
            library = m.PInvokeInfo.Module.Name, entry = m.PInvokeInfo.EntryPoint,
            callers = Callers(m.FullName).Select(c => new { method = c, callers = Callers(c) })
        })
        .ToArray();
var selected =
    methods
        .Where(m => m.HasBody &&
                    ((m.DeclaringType.FullName == "RenderHeads.Media.AVProVideo.Helper" &&
                      m.Name == "ConvertLongPathToShortDOS83Path") ||
                     (m.DeclaringType.FullName == "RenderHeads.Media.AVProVideo.MediaPlayer" &&
                      (m.Name == "CreateMediaPlayer" || m.Name == "OnApplicationQuit")) ||
                     m.DeclaringType.FullName == "LIV.SDK.Unity.SharedTextureProtocol" ||
                     (m.DeclaringType.FullName == "LIV.SDK.Unity.LIV" &&
                      new[] { "OnEnable", "OnDisable", "Update" }.Contains(m.Name))))
        .Select(m =>
                    new { method = m.FullName, callers = Callers(m.FullName),
                          instructions = m.Body.Instructions.Select(i => i.ToString()).ToArray() });
bool WindowsMedia(string name) =>
    name.StartsWith("RenderHeads.Media.AVProVideo.WindowsMediaPlayer") ||
    name.StartsWith("RenderHeads.Media.AVProVideo.WindowsRtMediaPlayer");
var windowsBoundary =
    methods.Where(m => m.HasBody && !WindowsMedia(m.DeclaringType.FullName))
        .SelectMany(m => m.Body.Instructions
                             .Where(i => i.Operand is MethodReference c &&
                                         WindowsMedia(c.DeclaringType.FullName))
                             .Select(i => new { caller = m.FullName,
                                                target = ((MethodReference)i.Operand).FullName,
                                                callerCallers = Callers(m.FullName) }));
var hashes =
    Directory.GetFiles(input, "*.dll")
        .Order()
        .ToDictionary(
            p => Path.GetFileName(p)!,
            p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant());
File.WriteAllText(output,
                  JsonSerializer.Serialize(new { input = Path.GetFullPath(input),
                                                 assemblySha256 = hashes, imports = rows,
                                                 windowsMediaBoundaryCalls = windowsBoundary,
                                                 selectedMethods = selected },
                                           new JsonSerializerOptions { WriteIndented = true }));
foreach (var group in rows.GroupBy(r => r.library))
    Console.WriteLine(group.Key + ": " + group.Count() + " imports");
