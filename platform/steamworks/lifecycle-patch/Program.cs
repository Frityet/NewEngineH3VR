using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;
if (args.Length != 2)
    throw new ArgumentException("Usage: <latest composed Game.dll> <output Game.dll>");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
resolver.AddSearchDirectory("audit-managed/ported");
resolver.AddSearchDirectory("/Applications/Unity/Unity.app/Contents/PlaybackEngines/" +
                            "MacStandaloneSupport/Variations/mono/Managed");
resolver.AddSearchDirectory(
    "/Applications/Unity/Unity.app/Contents/MonoBleedingEdge/lib/mono/4.7.1-api");
using var a = AssemblyDefinition.ReadAssembly(args[0],
                                              new ReaderParameters { AssemblyResolver = resolver });
var module = a.MainModule;
var manager = module.GetType("SteamManager");
var scene = module.GetType("FistVR.FVRSceneSettings");
var destroy = manager.Methods.Single(m => m.Name == "OnDestroy");
var sync = scene.Methods.Single(m => m.Name == "SteamSyncNow");
if (manager.Fields.Any(f => f.Name == "s_NativeSteamTeardown"))
    throw new InvalidOperationException("Already patched: refuse duplicate lifecycle injection");
var shutdownInstruction = destroy.Body.Instructions.Single(
    i => i.Operand is MethodReference mr &&
         mr.FullName == "System.Void Steamworks.SteamAPI::Shutdown()");
var shutdown = (MethodReference)shutdownInstruction.Operand;
var storeInstruction = sync.Body.Instructions.Single(
    i => i.Operand is MethodReference mr &&
         mr.FullName == "System.Boolean Steamworks.SteamUserStats::StoreStats()");
var store = (MethodReference)storeInstruction.Operand;
if (storeInstruction.Next.OpCode != OpCodes.Pop)
    throw new InvalidOperationException("Unexpected StoreStats return handling; refusing rewrite");
var afterStore = storeInstruction.Next.Next;
var teardown =
    new FieldDefinition("s_NativeSteamTeardown", FieldAttributes.Assembly | FieldAttributes.Static,
                        module.TypeSystem.Boolean);
manager.Fields.Add(teardown);
var flush = new MethodDefinition("NativeFlushStatsAndShutdown",
                                 MethodAttributes.Private | MethodAttributes.Static |
                                     MethodAttributes.HideBySig,
                                 module.TypeSystem.Void);
manager.Methods.Add(flush);
var il = flush.Body.GetILProcessor();
var tryStart = il.Create(OpCodes.Nop);
var finallyStart = il.Create(OpCodes.Nop);
var done = il.Create(OpCodes.Ret);
il.Append(il.Create(OpCodes.Ldc_I4_1));
il.Append(il.Create(OpCodes.Stsfld, teardown));
il.Append(tryStart);
il.Append(il.Create(OpCodes.Call, store));
il.Append(il.Create(OpCodes.Pop));
il.Append(il.Create(OpCodes.Leave, done));
il.Append(finallyStart);
il.Append(il.Create(OpCodes.Call, shutdown));
il.Append(il.Create(OpCodes.Endfinally));
il.Append(done);
flush.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
    TryStart = tryStart, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = done
});
shutdownInstruction.Operand = flush;
var syncIL = sync.Body.GetILProcessor();
syncIL.InsertBefore(storeInstruction, syncIL.Create(OpCodes.Ldsfld, teardown));
syncIL.InsertBefore(storeInstruction, syncIL.Create(OpCodes.Brtrue, afterStore));
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
a.Write(args[1]);
var report =
    new { input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]),
          changes = new[] { "SteamManager.OnDestroy invokes real StoreStats before Shutdown; " +
                                "Shutdown protected by finally",
                            "FVRSceneSettings.SteamSyncNow skips only native StoreStats after " +
                                "teardown starts; local SaveToFile and timer unchanged" },
          serverAcknowledgmentAwaited = false };
File.WriteAllText(Path.ChangeExtension(args[1], "lifecycle.json"),
                  JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
