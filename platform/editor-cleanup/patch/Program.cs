using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

if (args.Length != 2)
    throw new ArgumentException("Usage: <input Game.dll> <output Game.dll>");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
string engine =
    Environment.GetEnvironmentVariable("UNITY_CONTENTS") ??
    Path.GetFullPath(
        ".tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app/Contents");
if (!Directory.Exists(engine))
    engine = "/Applications/Unity/Unity.app/Contents";
resolver.AddSearchDirectory(Path.Combine(engine, "Managed/UnityEngine"));
resolver.AddSearchDirectory(Path.Combine(engine, "Managed"));
resolver.AddSearchDirectory(
    "/Applications/Unity/Unity.app/Contents/MonoBleedingEdge/lib/mono/4.7.1-api");
using var assembly =
    AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;
var type = module.GetType("UnityEngine.Rendering.PostProcessing.RuntimeUtilities") ??
           throw new InvalidOperationException("Original post-processing RuntimeUtilities absent");
var method =
    type.Methods.Single(m => m.Name == "Destroy" && m.Parameters.Count == 1 &&
                             m.Parameters[0].ParameterType.FullName == "UnityEngine.Object");
if (method.Body.Instructions.Any(i => i.Operand is MethodReference mr &&
                                      mr.Name == "DestroyImmediate"))
    throw new InvalidOperationException(
        "Already patched or unexpected original cleanup implementation");
var call = method.Body.Instructions.Single(
    i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr &&
         mr.FullName == "System.Void UnityEngine.Object::Destroy(UnityEngine.Object)");
var originalDestroy = (MethodReference)call.Operand;
var continuation =
    call.Next ?? throw new InvalidOperationException("Destroy call has no continuation");
if (method.Body.Instructions.Any(i => i.Operand == call ||
                                      i.Operand is Instruction[] targets && targets.Contains(call)))
    throw new InvalidOperationException("Unexpected branch into original Destroy call");
var oldInstructions = method.Body.Instructions.ToArray();
var oldOperands = oldInstructions.Select(i => i.Operand).ToArray();
// Reference the same engine module already used by Object.Destroy. No
// UnityEditor reference, helper type or self AssemblyRef is introduced.
TypeReference application =
    module.GetType("UnityEngine.Application") ??
    new TypeReference("UnityEngine", "Application", module, originalDestroy.DeclaringType.Scope);
var isEditor =
    new MethodReference("get_isEditor", module.TypeSystem.Boolean, application) { HasThis = false };
var isPlaying = new MethodReference("get_isPlaying", module.TypeSystem.Boolean,
                                    application) { HasThis = false };
var immediate = new MethodReference("DestroyImmediate", module.TypeSystem.Void,
                                    originalDestroy.DeclaringType) { HasThis = false };
immediate.Parameters.Add(new ParameterDefinition(method.Parameters[0].ParameterType));
immediate.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
var il = method.Body.GetILProcessor();
// The original non-null object argument remains on the evaluation stack while
// these guards run. Both player states and editor play mode reach the exact
// original deferred Destroy instruction. Null/fake-null skips this block.
foreach (var instruction in new[] { il.Create(OpCodes.Call, isEditor),
                                    il.Create(OpCodes.Brfalse, call),
                                    il.Create(OpCodes.Call, isPlaying),
                                    il.Create(OpCodes.Brtrue, call), il.Create(OpCodes.Ldc_I4_0),
                                    il.Create(OpCodes.Call, immediate),
                                    il.Create(OpCodes.Br, continuation) })
    il.InsertBefore(call, instruction);
method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 2);
if (!oldInstructions.Select((i, n) => Equals(i.Operand, oldOperands[n])).All(x => x))
    throw new InvalidOperationException("An original operand changed");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
assembly.Write(args[1]);
var report = new { input = Path.GetFullPath(args[0]),
                   output = Path.GetFullPath(args[1]),
                   method = method.FullName,
                   originalInstructionsUnchanged = true,
                   addedInstructions = 7,
                   editorCondition = "Application.isEditor && !Application.isPlaying",
                   editorCall = "Object.DestroyImmediate(obj, allowDestroyingAssets: false)",
                   playerCall = originalDestroy.FullName,
                   originalNullGuardPreserved = true,
                   addedAssemblyReferences = 0 };
File.WriteAllText(Path.ChangeExtension(args[1], "editor-cleanup.json"),
                  JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
