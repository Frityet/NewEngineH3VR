using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
if (args.Length != 4)
    throw new ArgumentException("input adapter, output adapter, report.json, intent source.cs");
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]),
       report = Path.GetFullPath(args[2]), intent = Path.GetFullPath(args[3]);
if (new[] { input, output, report, intent }.Distinct().Count() != 4)
    throw new ArgumentException("Input/output/report/source must differ");
byte[] original = File.ReadAllBytes(input);
using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(original));
if (assembly.Name.Name != "NativeH3VR.SteamVR.Adapter" || assembly.Name.HasPublicKey)
    throw new InvalidOperationException("Expected unsigned owning SteamVR adapter");
var getters = assembly.MainModule.GetTypes()
                  .Where(t => t.FullName == "NativeH3VR.NativeSteamVRBootstrap")
                  .SelectMany(t => t.Methods)
                  .Where(m => m.Name == "get_RuntimeRoot")
                  .ToArray();
if (getters.Length != 1)
    throw new InvalidOperationException("Expected one RuntimeRoot getter in owning adapter");
var getter = getters[0];
if (!getter.IsStatic || getter.Parameters.Count != 0 ||
    getter.ReturnType.FullName != "System.String" || getter.Body.ExceptionHandlers.Count != 0)
    throw new InvalidOperationException("Getter signature/EH changed");
var code = getter.Body.Instructions;
Code[] expected = { Code.Ldstr, Code.Call,  Code.Dup,  Code.Brtrue, Code.Pop,
                    Code.Call,  Code.Ldstr, Code.Call, Code.Ret };
if (!code.Select(i => i.OpCode.Code).SequenceEqual(expected) ||
    (string)code[0].Operand != "NATIVEH3VR_RUNTIME" || (string)code[6].Operand != "NativeRuntime" ||
    code[3].Operand != code[8] ||
    ((MethodReference)code[1].Operand).FullName !=
        "System.String System.Environment::GetEnvironmentVariable(System.String)" ||
    ((MethodReference)code[5].Operand).FullName !=
        "System.String UnityEngine.Application::get_streamingAssetsPath()" ||
    ((MethodReference)code[7].Operand).FullName !=
        "System.String System.IO.Path::Combine(System.String,System.String)")
    throw new InvalidOperationException("Unsupported complete RuntimeRoot getter layout");
var references = assembly.MainModule.GetTypes()
                     .SelectMany(t => t.Methods)
                     .Where(m => m.HasBody)
                     .SelectMany(m => m.Body.Instructions
                                          .Where(i => i.OpCode == OpCodes.Ldstr &&
                                                      Equals(i.Operand, "NativeRuntime"))
                                          .Select(i => m.FullName))
                     .ToArray();
if (references.Length != 1 || references[0] != getter.FullName)
    throw new InvalidOperationException("Runtime path string has unexpected additional consumers");
using var pe = new PEReader(new MemoryStream(original));
byte[] il = pe.GetMethodBody((int)getter.RVA).GetILBytes()!;
int token = BitConverter.ToInt32(il, code[6].Offset + 1);
if ((token & unchecked((int)0xff000000)) != 0x70000000)
    throw new InvalidOperationException("Expected direct user-string token");
int metadata = pe.PEHeaders.MetadataStartOffset;
int position = (metadata + 16 + BitConverter.ToInt32(original, metadata + 12) + 3) & ~3;
int count = BitConverter.ToUInt16(original, position + 2);
position += 4;
int heap = 0, heapSize = 0;
for (int index = 0; index < count; index++) {
    int relative = BitConverter.ToInt32(original, position),
        size = BitConverter.ToInt32(original, position + 4), nameStart = position + 8;
    int end = Array.IndexOf(original, (byte)0, nameStart);
    string name = Encoding.ASCII.GetString(original, nameStart, end - nameStart);
    position = (end + 4) & ~3;
    if (name == "#US") {
        heap = metadata + relative;
        heapSize = size;
    }
}
if (heap == 0)
    throw new InvalidOperationException("Missing user-string heap");
byte[] Entry(string text) {
    byte[] data = Encoding.Unicode.GetBytes(text);
    return new[] { checked((byte)(data.Length + 1)) }.Concat(data).Append((byte)0).ToArray();
}
byte[] oldEntry = Entry("NativeRuntime"), newEntry = Entry("NewEngine");
int offset = heap + (token & 0x00ffffff);
if (offset < heap || offset + oldEntry.Length > heap + heapSize ||
    !original.AsSpan(offset, oldEntry.Length).SequenceEqual(oldEntry))
    throw new InvalidOperationException("Unexpected original user-string encoding/span");
// Reserve the original span so every later string token retains its exact offset.
// The trailing valid three-character ASCII user-string entry is unreferenced.
byte[] padding = Entry("pad");
byte[] replacement = newEntry.Concat(padding).ToArray();
if (replacement.Length != oldEntry.Length)
    throw new InvalidOperationException("Replacement does not preserve heap span");
byte[] candidate = (byte[])original.Clone();
replacement.CopyTo(candidate, offset);
if (!candidate.AsSpan(0, offset).SequenceEqual(original.AsSpan(0, offset)) ||
    !candidate.AsSpan(offset + replacement.Length)
         .SequenceEqual(original.AsSpan(offset + oldEntry.Length)))
    throw new InvalidOperationException("Unexpected bytes changed outside the one heap entry");
using (var verified = AssemblyDefinition.ReadAssembly(new MemoryStream(candidate))) {
    var updated = verified.MainModule.GetType("NativeH3VR.NativeSteamVRBootstrap")
                      .Methods.Single(m => m.Name == "get_RuntimeRoot");
    if (!Equals(updated.Body.Instructions[6].Operand, "NewEngine"))
        throw new InvalidOperationException("Candidate getter string did not resolve correctly");
}
string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllBytes(output, candidate);
var proof =
    new { schema_version = 1,
          strategy = "one user-string heap entry, offset-preserving; no assembly re-emission",
          input,
          input_sha256 = Hash(original),
          output,
          output_sha256 = Hash(candidate),
          assembly_identity = assembly.Name.FullName,
          target = getter.FullName,
          old_string = "NativeRuntime",
          new_string = "NewEngine",
          string_references = references.Length,
          changed_heap_offset = offset,
          reserved_bytes = oldEntry.Length,
          all_other_file_bytes_identical = true,
          raw_method_il_identical = true,
          metadata_tables_and_non_userstring_heaps_identical = true,
          intent_source = intent,
          intent_source_sha256 = Hash(File.ReadAllBytes(intent)),
          source_scope = "Future source builds use NewEngine plus a read-only legacy fallback. " +
                         "This migration candidate changes only the legacy getter literal; its " +
                         "environment override remains unchanged.",
          patcher_sha256 = Hash(File.ReadAllBytes(typeof(Program).Assembly.Location)) };
File.WriteAllText(report, JsonSerializer.Serialize(proof, new JsonSerializerOptions {
    WriteIndented = true
}) + "\n");
Console.WriteLine("Verified candidate only: one 28-byte #US span; every other file byte, all raw " +
                  "IL and metadata tables unchanged");
