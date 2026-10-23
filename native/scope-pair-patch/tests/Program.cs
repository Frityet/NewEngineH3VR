using NativeH3VR;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

int assertions = 0;
void Check(bool ok, string name) {
    assertions++;
    if (!ok)
        throw new Exception(name);
}
string setting = Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_PAIR_FIX");
if (!string.IsNullOrEmpty(setting) && setting != "0" && setting != "1") {
    try {
        bool ignored = NewEngineScopeActivation.Enabled;
    } catch (TypeInitializationException e) {
        Check(e.InnerException is InvalidOperationException, "Invalid config exception");
        Console.WriteLine("PASS invalid configuration rejected");
        return;
    }
    throw new Exception("Invalid config accepted");
}
bool enabled = setting != "0";
Check(NewEngineScopeActivation.Enabled == enabled, "Selected variant");
var outside = new Behaviour();
NewEngineScopeActivation.SetEnabled(outside, true);
NewEngineScopeActivation.SetEnabled(outside, false);
Check(!outside.enabled && outside.Creates == 1 && outside.Releases == 1 && outside.Writes == 2,
      "Original outside-pair lifecycle");
bool nullThrows = false;
try {
    NewEngineScopeActivation.SetEnabled(null, false);
} catch (NullReferenceException) {
    nullThrows = true;
}
Check(nullThrows, "Original null setter outside pair");
Environment.SetEnvironmentVariable("NEWENGINEH3VR_SCOPE_PAIR_FIX", enabled ? "0" : "1");
Check(NewEngineScopeActivation.Enabled == enabled, "Cached selector");
if (!enabled) {
    var baseline = new Behaviour();
    NewEngineScopeActivation.BeginPair();
    NewEngineScopeActivation.SetEnabled(baseline, true);
    NewEngineScopeActivation.SetEnabled(baseline, false);
    NewEngineScopeActivation.EndPair();
    Check(!NewEngineScopeActivation.PairActive && !baseline.enabled && baseline.Creates == 1 &&
              baseline.Releases == 1,
          "Exact per-eye baseline");
    Console.WriteLine("PASS " + assertions + " baseline configuration assertions");
    return;
}
var left = new Behaviour();
NewEngineScopeActivation.BeginPair();
NewEngineScopeActivation.SetEnabled(left, true);
NewEngineScopeActivation.SetEnabled(left, false);
Check(left.enabled, "Left selected survives right rejection");
NewEngineScopeActivation.EndPair();
Check(left.enabled && left.Creates == 1 && left.Releases == 0, "Left target retained after pair");
var right = new Behaviour(true);
NewEngineScopeActivation.BeginPair();
NewEngineScopeActivation.SetEnabled(right, false);
Check(right.enabled, "Left rejection deferred");
NewEngineScopeActivation.SetEnabled(right, true);
NewEngineScopeActivation.EndPair();
Check(right.enabled && right.Creates == 0 && right.Releases == 0, "Right selected target retained");
NewEngineScopeActivation.BeginPair();
NewEngineScopeActivation.SetEnabled(left, false);
NewEngineScopeActivation.SetEnabled(left, false);
Check(left.enabled, "Neither eye waits for pair completion");
NewEngineScopeActivation.EndPair();
Check(!left.enabled && left.Releases == 1 && !NewEngineScopeActivation.PairActive,
      "Neither-eye target released exactly once");
var interrupted = new Behaviour(true);
bool caught = false;
try {
    NewEngineScopeActivation.BeginPair();
    try {
        NewEngineScopeActivation.SetEnabled(interrupted, false);
        throw new Exception("Eye render failure");
    } finally {
        NewEngineScopeActivation.EndPair();
    }
} catch (Exception e) {
    caught = e.Message == "Eye render failure";
}
Check(caught && !interrupted.enabled && !NewEngineScopeActivation.PairActive,
      "Exception executes bounded cleanup");
var broken = new Behaviour(true) { ThrowOnDisable = true };
var other = new Behaviour(true);
NewEngineScopeActivation.BeginPair();
NewEngineScopeActivation.SetEnabled(broken, false);
NewEngineScopeActivation.SetEnabled(other, false);
caught = false;
try {
    NewEngineScopeActivation.EndPair();
} catch (InvalidOperationException) {
    caught = true;
}
Check(caught && !broken.enabled && !other.enabled && !NewEngineScopeActivation.PairActive,
      "Cleanup continues after failing OnDisable");
var destroyed = new Behaviour(true);
NewEngineScopeActivation.BeginPair();
NewEngineScopeActivation.SetEnabled(destroyed, false);
destroyed.Destroyed = true;
NewEngineScopeActivation.EndPair();
Check(destroyed.Writes == 0 && !NewEngineScopeActivation.PairActive,
      "Destroyed pending scope skipped");
NewEngineScopeActivation.BeginPair();
caught = false;
try {
    NewEngineScopeActivation.BeginPair();
} catch (InvalidOperationException) {
    caught = true;
}
Check(caught && NewEngineScopeActivation.PairActive,
      "Nested pair rejected without losing outer scope");
NewEngineScopeActivation.EndPair();
NewEngineScopeActivation.EndPair();
var selectedField = typeof(NewEngineScopeActivation)
                        .GetField("selected", System.Reflection.BindingFlags.Static |
                                                  System.Reflection.BindingFlags.NonPublic);
var pendingField = typeof(NewEngineScopeActivation)
                       .GetField("pending", System.Reflection.BindingFlags.Static |
                                                System.Reflection.BindingFlags.NonPublic);
Check(((System.Collections.ICollection)selectedField.GetValue(null)).Count == 0 &&
          ((System.Collections.ICollection)pendingField.GetValue(null)).Count == 0,
      "No retained scope references after cleanup");
var stable = new Behaviour(true);
for (int i = 0; i < 1000; i++) {
    NewEngineScopeActivation.BeginPair();
    NewEngineScopeActivation.SetEnabled(stable, true);
    NewEngineScopeActivation.SetEnabled(stable, false);
    NewEngineScopeActivation.EndPair();
}
long before = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 10000; i++) {
    NewEngineScopeActivation.BeginPair();
    NewEngineScopeActivation.SetEnabled(stable, true);
    NewEngineScopeActivation.SetEnabled(stable, false);
    NewEngineScopeActivation.EndPair();
}
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Check(allocated == 0 && stable.Creates == 0 && stable.Releases == 0,
      "No steady-state helper allocation or resource churn");
var many = new Behaviour[16];
for (int i = 0; i < many.Length; i++)
    many[i] = new Behaviour(true);
for (int iteration = 0; iteration < 100; iteration++) {
    NewEngineScopeActivation.BeginPair();
    for (int i = 0; i < many.Length; i++)
        NewEngineScopeActivation.SetEnabled(many[i], false);
    for (int i = 0; i < many.Length; i++)
        NewEngineScopeActivation.SetEnabled(many[i], true);
    NewEngineScopeActivation.EndPair();
}
before = GC.GetAllocatedBytesForCurrentThread();
for (int iteration = 0; iteration < 1000; iteration++) {
    NewEngineScopeActivation.BeginPair();
    for (int i = 0; i < many.Length; i++)
        NewEngineScopeActivation.SetEnabled(many[i], false);
    for (int i = 0; i < many.Length; i++)
        NewEngineScopeActivation.SetEnabled(many[i], true);
    NewEngineScopeActivation.EndPair();
}
Check(GC.GetAllocatedBytesForCurrentThread() == before &&
          many.All(b => b.enabled && b.Creates == 0 && b.Releases == 0),
      "Expanded scope capacity reuses lists with zero steady-state allocation");
Console.WriteLine("PASS " + assertions + " lifecycle assertions; " + allocated +
                  " bytes for 10,000 warmed stable frames (host model)");
if (args.Length >= 2) {
    void Reject(string name, Action<AssemblyDefinition, AssemblyDefinition> change) {
        using var game = AssemblyDefinition.ReadAssembly(args[0]);
        using var xr = AssemblyDefinition.ReadAssembly(args[1]);
        change(game, xr);
        try {
            ScopePairPatch.Apply(game, xr);
        } catch (InvalidOperationException) {
            assertions++;
            Console.WriteLine("PASS rejected " + name);
            return;
        }
        throw new Exception("Accepted " + name);
    }
    MethodDefinition Target(AssemblyDefinition g) =>
        g.MainModule.GetType("PIPScope").Methods.Single(m => m.FullName == ScopePairPatch.Target);
    Reject("changed enable constant",
           (g, x) => Target(g).Body.Instructions[330].OpCode = OpCodes.Ldc_I4_0);
    Reject("extra IL",
           (g, x) => Target(g).Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop)));
    Reject("changed unrelated IL",
           (g, x) => Target(g).Body.Instructions[1].OpCode = OpCodes.Ldc_I4_1);
    Reject("changed helper signature", (g, x) => x.MainModule.GetType(ScopePairPatch.HelperType)
                                                     .Methods.Single(m => m.Name == "SetEnabled")
                                                     .ReturnType = x.MainModule.TypeSystem.Boolean);
    Reject("missing helper",
           (g, x) => x.MainModule.Types.Remove(x.MainModule.GetType(ScopePairPatch.HelperType)));
    Reject("wrong Game identity", (g, x) => g.Name.Name = "Unexpected.Game");
    Reject("repeated application", (g, x) => ScopePairPatch.Apply(g, x));
    using var game = AssemblyDefinition.ReadAssembly(args[0]);
    using var xr = AssemblyDefinition.ReadAssembly(args[1]);
    var target = ScopePairPatch.Apply(game, xr);
    Check(target.Body.Instructions.Count == 594 &&
              ScopePairPatch.Sites.All(
                  i => target.Body.Instructions[i].OpCode == OpCodes.Call &&
                       ((MethodReference)target.Body.Instructions[i].Operand).FullName ==
                           ScopePairPatch.Helper),
          "Exactly three setter replacements");
    if (args.Length >= 3) {
        Directory.CreateDirectory(args[2]);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        if (args.Length >= 4)
            resolver.AddSearchDirectory(args[3]);
        var options = new ReaderParameters { AssemblyResolver = resolver };
        using var malformedXr = AssemblyDefinition.ReadAssembly(args[1], options);
        var iterator = malformedXr.MainModule.GetType("NativeH3VR.NativeStereoRenderer")
                           .NestedTypes.Single(t => t.Name == "<Start>c__Iterator0")
                           .Methods.Single(m => m.Name == "MoveNext");
        var cleanup = iterator.Body.Instructions.Single(
            i => i.Operand is MethodReference call &&
                 call.FullName == "System.Void NativeH3VR.NewEngineScopeActivation::EndPair()");
        cleanup.OpCode = OpCodes.Nop;
        cleanup.Operand = null;
        malformedXr.Write(Path.Combine(args[2], "missing-finally-hook.dll"));
        using var unrelatedGame = AssemblyDefinition.ReadAssembly(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "NativeH3VR.Game.dll"),
            options);
        unrelatedGame.MainModule.GetType("PIPScope")
            .Fields.Single(f => f.Name == "simultaneousRenderingScopeCount")
            .IsPublic = false;
        unrelatedGame.Write(Path.Combine(args[2], "unrelated-field-change.dll"));
        Console.WriteLine("Wrote independent-verifier rejection fixtures");
    }
}
Console.WriteLine("PASS " + assertions + " total assertions");
