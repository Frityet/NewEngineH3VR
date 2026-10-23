using Mono.Cecil;
using Mono.Cecil.Cil;
if (args.Length != 2)
    throw new ArgumentException("<composed FirstPass input> <patched output>");
using var a = AssemblyDefinition.ReadAssembly(args[0]);
var t = a.MainModule.GetType("FMODUnity.RuntimeUtils");
var m = t.Methods.Single(x => x.Name == "GetCurrentPlatform");
m.Body = new MethodBody(m);
var il = m.Body.GetILProcessor();
il.Append(il.Create(OpCodes.Ldc_I4, 9));
il.Append(il.Create(OpCodes.Ret));
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
a.Write(args[1]);
Console.WriteLine("FMOD RuntimeUtils.GetCurrentPlatform => Mac (9), preserving all prior patches");
