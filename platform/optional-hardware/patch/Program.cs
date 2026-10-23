using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

if (args.Length != 2)
    throw new ArgumentException("Usage: <guarded FirstPass.dll> <output FirstPass.dll>");
using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
var module = assembly.MainModule;
var type = module.GetType("ForceTubeVRInterface") ??
           throw new InvalidOperationException("ForceTube interface absent");
if (type.Methods.Any(m => m.Name.StartsWith("NativeWindows_")))
    throw new InvalidOperationException("ForceTube JIT isolation already applied");
var methods = type.Methods
                  .Where(m => m.HasBody &&
                              m.Body.Instructions.Any(
                                  i => i.Operand is MethodReference r &&
                                       r.DeclaringType.FullName == type.FullName &&
                                       type.Methods.Any(n => n.Name == r.Name && n.IsPInvokeImpl)))
                  .ToArray();
if (methods.Length != 7)
    throw new InvalidOperationException("Unexpected ForceTube native caller count: " +
                                        methods.Length);
var changes = new List<object>();
foreach (var method in methods) {
    if (!method.IsStatic ||
        !method.Body.Instructions.Any(i => i.Operand is MethodReference r &&
                                           r.DeclaringType.FullName ==
                                               "NativeH3VR.NativeSteamVRBootstrap" &&
                                           r.Name == "ForceTubeUnavailable"))
        throw new InvalidOperationException("Expected existing macOS unavailable guard in " +
                                            method.FullName);
    foreach (var call in method.Body.Instructions.ToArray()) {
        if (call.Operand is not MethodReference reference ||
            reference.DeclaringType.FullName != type.FullName)
            continue;
        var native = type.Methods.SingleOrDefault(m => m.Name == reference.Name && m.IsPInvokeImpl);
        if (native == null)
            continue;
        if (call.OpCode != OpCodes.Call || !native.IsStatic ||
            !native.PInvokeInfo.Module.Name.StartsWith("ForceTubeVR_API_x"))
            throw new InvalidOperationException("Unexpected native ForceTube signature");
        var thunk = type.Methods.SingleOrDefault(m => m.Name == "NativeWindows_" + native.Name);
        if (thunk == null) {
            thunk = new MethodDefinition("NativeWindows_" + native.Name,
                                         MethodAttributes.Private | MethodAttributes.Static |
                                             MethodAttributes.HideBySig,
                                         native.ReturnType);
            thunk.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed |
                                   MethodImplAttributes.NoInlining;
            foreach (var p in native.Parameters)
                thunk.Parameters.Add(
                    new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
            var il = thunk.Body.GetILProcessor();
            foreach (var p in thunk.Parameters)
                il.Append(il.Create(OpCodes.Ldarg, p));
            il.Append(il.Create(OpCodes.Call, native));
            il.Append(il.Create(OpCodes.Ret));
            type.Methods.Add(thunk);
        }
        call.Operand = thunk;
        changes.Add(
            new { caller = method.FullName, native = native.FullName, thunk = thunk.FullName });
    }
}
if (changes.Count != 14)
    throw new InvalidOperationException("Unexpected ForceTube native call count: " + changes.Count);
if (methods.Any(
        m => m.Body.Instructions.Any(i => i.Operand is MethodDefinition d && d.IsPInvokeImpl)))
    throw new InvalidOperationException("Direct native calls remain in guarded methods");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
assembly.Write(args[1]);
var report = new { input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]),
                   guardedMethods = methods.Length, isolatedCalls = changes.Count, changes };
File.WriteAllText(Path.ChangeExtension(args[1], "optional-hardware.json"),
                  JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
