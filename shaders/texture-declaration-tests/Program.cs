using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using AssetRipper.Export.UnityProjects.Shaders;
using AssetRipper.Export.Modules.Shaders.ShaderBlob.Parameters;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.DirectXDisassembler;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.DirectXDisassembler.Blocks;
var method = typeof(USCShaderExporter)
                 .GetMethod("GetTextureDeclaration", BindingFlags.NonPublic | BindingFlags.Static)!;
SHDRInstruction Sampler(int index, SamplerMode mode) {
    using var data = new MemoryStream();
    using var w = new BinaryWriter(data);
    w.Write((3 << 24) | (int)Opcode.dcl_sampler | ((int)mode << 11));
    w.Write(0x00106000);
    w.Write(index);
    data.Position = 0;
    return new SHDRInstruction(new BinaryReader(data), null!);
}
int count = 0;
string Emit(byte dimension, int owner, SHDRInstruction[] instructions,
            string name = "_ShadowMapTexture") => (string)method.Invoke(null, new object[] {
    new TextureParameter(name, 3, dimension, owner), instructions
})!;
void Check(byte dimension, SamplerMode mode, string texture, string element, string sampler) {
    string expected =
        $"{texture}<{element}> _ShadowMapTexture; {sampler} sampler_ShadowMapTexture;";
    string actual = Emit(dimension, 7, [
        Sampler(0, mode == SamplerMode.Default ? SamplerMode.Comparison : SamplerMode.Default),
        Sampler(7, mode)
    ]);
    if (actual != expected)
        throw new Exception(actual + " != " + expected);
    count++;
}
foreach (var (dimension, texture)
             in new[] { ((byte)2, "Texture2D"), ((byte)4, "TextureCube"),
                        ((byte)5, "Texture2DArray"), ((byte)6, "TextureCubeArray") }) {
    Check(dimension, SamplerMode.Default, texture, "float4", "SamplerState");
    Check(dimension, SamplerMode.Comparison, texture, "float", "SamplerComparisonState");
}
Check(3, SamplerMode.Default, "Texture3D", "float4", "SamplerState");
if (!Emit(2, 7, [Sampler(7, SamplerMode.Comparison)], "UnrelatedName")
         .Contains("SamplerComparisonState"))
    throw new Exception("Sampler mode must not depend on name");
count++;
foreach (var (dimension, instructions)
             in new[] { ((byte)1, new[] { Sampler(7, SamplerMode.Default) }),
                        ((byte)3, new[] { Sampler(7, SamplerMode.Comparison) }),
                        ((byte)2, new[] { Sampler(7, SamplerMode.Comparison),
                                          Sampler(7, SamplerMode.Default) }) }) {
    bool rejected = false;
    try {
        Emit(dimension, 7, instructions);
    } catch (TargetInvocationException e)
        when (e.InnerException is NotSupportedException or InvalidDataException) {
        rejected = true;
    }
    if (!rejected)
        throw new Exception("Invalid metadata accepted");
    count++;
}
var asm = typeof(USCShaderExporter).Assembly.Location;
var report =
    new { passed = true, cases = count,
          unityProjectsDllSha256 =
              Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asm))).ToLowerInvariant(),
          scope = "Original dimension metadata and decoded dcl_sampler index/mode; native " +
                  "gameplay and sampling comparison semantics require separate checks" };
File.WriteAllText(Path.Combine(args[0], "shaders/texture-declaration-tests/verification.json"),
                  JsonSerializer.Serialize(report,
                                           new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
