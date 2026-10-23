using Mono.Cecil;
using System.Text.Json;
if (args.Length != 3)
    throw new ArgumentException("Usage: <Game.dll> <output Game.dll> <Unity.app/Contents>");
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var unity = Path.GetFullPath(args[2]);
if (input == output)
    throw new ArgumentException("Input and output must differ");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
resolver.AddSearchDirectory(
    Path.Combine(unity, "PlaybackEngines/MacStandaloneSupport/Variations/mono/Managed"));
resolver.AddSearchDirectory(Path.Combine(unity, "MonoBleedingEdge/lib/mono/4.7.1-api"));
using var a = AssemblyDefinition.ReadAssembly(input,
                                              new ReaderParameters { AssemblyResolver = resolver });
using var core = AssemblyDefinition.ReadAssembly(
    Path.Combine(
        unity,
        "PlaybackEngines/MacStandaloneSupport/Variations/mono/Managed/UnityEngine.CoreModule.dll"),
    new ReaderParameters { AssemblyResolver = resolver });
var settings = core.MainModule.GetType("UnityEngine.RenderSettings");
var oldSignature =
    "System.Void UnityEngine.RenderSettings::set_customReflection(UnityEngine.Cubemap)";
var oldSetter = settings.Methods.SingleOrDefault(m => m.FullName == oldSignature);
var newSetter = settings.Methods.SingleOrDefault(
    m => m.Name == "set_customReflection" && m.IsStatic && m.ReturnType.FullName == "System.Void" &&
         m.Parameters.Count == 1 &&
         m.Parameters[0].ParameterType.FullName == "UnityEngine.Texture");
if (oldSetter == null && newSetter == null)
    throw new InvalidOperationException(
        "Neither known original nor replacement custom-reflection setter exists: " +
        string.Join(";", settings.Methods.Where(m => m.Name.Contains("customReflection"))
                             .Select(m => m.FullName)));
var changes = new List<object>();
var caller = a.MainModule.GetType("FistVR.PhotoStudioControlPanel")
                 .Methods.Single(m => m.Name == "UpdateAll");
var calls = caller.Body.Instructions
                .Where(i => i.Operand is MethodReference m && m.FullName == oldSignature)
                .ToArray();
if (calls.Length != 1)
    throw new InvalidOperationException(
        "Expected exactly one original PhotoStudio custom-reflection setter call; refusing " +
        "ambiguous or duplicate patch");
if (oldSetter == null) {
    calls[0].Operand = a.MainModule.ImportReference(newSetter!);
    changes.Add(new { caller = caller.FullName, old = oldSignature, target = newSetter!.FullName });
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
a.Write(output);
File.WriteAllText(Path.ChangeExtension(output, "unity-api.json"),
                  JsonSerializer.Serialize(new { input, output, unityRoot = unity, changes },
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Custom reflection retarget: " + changes.Count + " calls changed");
