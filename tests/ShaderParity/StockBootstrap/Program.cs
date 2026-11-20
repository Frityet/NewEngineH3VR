using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 4)
    throw new ArgumentException("Usage: StockBootstrap ORIGINAL_MANAGED WITNESS_DLL WITNESS_TYPE NEW_OUTPUT_DLL");

var managed = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[3]);
var stock = Directory.GetParent(managed)?.Parent
    ?? throw new ArgumentException("Pass the stock h3vr_Data/Managed directory.");
var relativeOutput = Path.GetRelativePath(stock.FullName, output);
if (relativeOutput != ".." && !relativeOutput.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
    && !Path.IsPathRooted(relativeOutput))
    throw new IOException("Write the bootstrap outside the stock installation.");
if (File.Exists(output) || Directory.Exists(output))
    throw new IOException("The bootstrap output must be a fresh file outside the stock installation.");

using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
using var game = AssemblyDefinition.ReadAssembly(Path.Combine(managed, "Assembly-CSharp.dll"),
    new ReaderParameters { AssemblyResolver = resolver });
using var witness = AssemblyDefinition.ReadAssembly(args[1]);
var scene = game.MainModule.GetType("FistVR.FVRSceneSettings")
    ?? throw new InvalidDataException("Cannot find the stock scene component.");
var awake = scene.Methods.Single(method => method.Name == "Awake" && method.Parameters.Count == 0 && method.HasBody);
var witnessType = witness.MainModule.GetType(args[2])
    ?? throw new InvalidDataException("Cannot find the witness type.");
var initialize = witnessType.Methods.Single(method => method.Name == "Initialize" && method.IsStatic &&
    method.Parameters.Count == 0 && method.ReturnType.MetadataType == MetadataType.Void);
var target = game.MainModule.ImportReference(initialize);
var processor = awake.Body.GetILProcessor();
processor.InsertBefore(awake.Body.Instructions[0], processor.Create(OpCodes.Call, target));
using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
game.Write(file);
