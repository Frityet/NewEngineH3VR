using Mono.Cecil;
using Mono.Cecil.Cil;
if (args.Length != 3)
    throw new ArgumentException("baseline Game, candidate XR, baseline XR");
int passed = 0;
void Reject(string name, Action<AssemblyDefinition, AssemblyDefinition> mutate,
            bool oldHelper = false) {
    using var game = AssemblyDefinition.ReadAssembly(args[0]);
    using var xr = AssemblyDefinition.ReadAssembly(args[oldHelper ? 2 : 1]);
    mutate(game, xr);
    try {
        StereoPackingPatch.Apply(game, xr);
    } catch (InvalidOperationException) {
        Console.WriteLine("PASS rejected " + name);
        passed++;
        return;
    }
    throw new Exception("Accepted invalid layout: " + name);
}
MethodDefinition
Target(AssemblyDefinition g) => g.MainModule.GetType(StereoPackingPatch.TargetType)
                                    .Methods.Single(m => m.Name == StereoPackingPatch.TargetName);
Reject("constant false", (g, x) => Target(g).Body.Instructions[0].OpCode = OpCodes.Ldc_I4_0);
Reject("extra instruction",
       (g, x) => Target(g).Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop)));
Reject("local variable", (g, x) => Target(g).Body.Variables.Add(
                             new VariableDefinition(g.MainModule.TypeSystem.Boolean)));
Reject("instance getter", (g, x) => Target(g).IsStatic = false);
Reject("changed return", (g, x) => Target(g).ReturnType = g.MainModule.TypeSystem.Int32);
Reject("helper unavailable", (g, x) => {}, true);
Reject("wrong Game identity", (g, x) => g.Name.Name = "Unexpected.Game");
Reject("wrong XR identity", (g, x) => x.Name.Name = "Unexpected.XR");
Reject("helper made private",
       (g, x) => x.MainModule.GetType(StereoPackingPatch.HelperType)
                     .Methods.Single(m => m.Name == StereoPackingPatch.HelperName)
                     .IsPrivate = true);
Reject("already patched", (g, x) => StereoPackingPatch.Apply(g, x));
using (var game = AssemblyDefinition.ReadAssembly(args[0])) using (
    var xr = AssemblyDefinition.ReadAssembly(args[1])) {
    var target = StereoPackingPatch.Apply(game, xr);
    if (target.Body.Instructions.Count != 2 || target.Body.Instructions[0].OpCode != OpCodes.Call ||
        target.Body.Instructions[1].OpCode != OpCodes.Ret)
        throw new Exception("Unexpected successful patch shape");
    passed++;
}
Console.WriteLine("PASS " + passed + " patch admission checks");
