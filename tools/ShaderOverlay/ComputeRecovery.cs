using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using H3VR.Converter;
using YamlDotNet.RepresentationModel;

namespace NativeH3VR.ShaderOverlay;

internal static partial class ComputeRecovery
{
    private const string Folder = "ComputeShader";
    private const string ReceiptFilename = "compute-provenance.json";
    private sealed record ToolHashes(string DxbcCompiler, string SpirvCross);
    private sealed record Kernel(string Name, int[] Threads, string DxbcSha256, string SpirvSha256);
    private sealed record Shader(string Source, string SourceSha256, string SourceMetaSha256,
        string Compute, string ComputeSha256, string ComputeMetaSha256, Kernel[] Kernels);
    private sealed record ComputeReceipt(int SchemaVersion, string ExtractionFingerprint, ToolHashes ToolSha256, Shader[] Files);
    private sealed record KernelSource(Kernel Evidence, string Hlsl);

    [GeneratedRegex(@"(?m)^guid: ([0-9a-f]{32})$")] private static partial Regex GuidLine();
    [GeneratedRegex(@"(?m)^  (?:userData|assetBundleName|assetBundleVariant):[^\r\n]*$")] private static partial Regex ImportSettings();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex Identifier();
    [GeneratedRegex(@"(?m)^uniform float4 (\w+)\[(\d+)\];\n")] private static partial Regex Uniform();
    [GeneratedRegex(@"(?m)^[\w<>]+ (\w+) : register\(([stu])(\d+)\);$")] private static partial Regex Resource();
    [GeneratedRegex(@"\b\w+\b")] private static partial Regex Words();

    public static async Task Run(ComputeOptions options, CancellationToken cancellationToken)
    {
        var workspace = SafeFiles.Canonical(options.Workspace);
        var overlay = SafeFiles.Canonical(options.Overlay);
        var compiler = SafeFiles.Canonical(options.Compiler);
        var cross = SafeFiles.Canonical(options.Cross);
        SafeFiles.RequireSeparate(overlay, workspace);
        var extract = Receipt.Extraction(workspace);
        var inputRoot = Receipt.Child(workspace, "recovery/ExportedProject/Assets/" + Folder);
        var target = Path.Combine(Receipt.Child(overlay, "Assets"), Folder);
        if (Directory.Exists(target) || File.Exists(target) || new DirectoryInfo(target).LinkTarget is not null)
            throw new IOException("Compute overlay output already exists: " + target);
        var inputs = Directory.GetFiles(inputRoot, "*.asset").Order(StringComparer.Ordinal).ToArray();
        if (inputs.Length == 0) throw new InvalidDataException("The extraction has no serialized compute shaders.");
        var hashes = new ToolHashes(SafeFiles.Hash(compiler), SafeFiles.Hash(cross));
        var scratch = Path.Combine(overlay, ".compute-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var candidate = Path.Combine(scratch, Folder);
            Directory.CreateDirectory(candidate);
            var translated = new List<Shader>();
            foreach (var path in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                translated.Add(await ConvertAsset(path, extract.Files, candidate, compiler, cross, scratch, cancellationToken));
            }
            if (hashes != new ToolHashes(SafeFiles.Hash(compiler), SafeFiles.Hash(cross)))
                throw new InvalidDataException("The translation tools changed during recovery.");
            Receipt.WriteNew(Path.Combine(candidate, ReceiptFilename), new ComputeReceipt(1, extract.Fingerprint, hashes, translated.ToArray()));
            Directory.Move(candidate, target);
            Console.WriteLine($"{translated.Count} private compute shaders, {translated.Sum(shader => shader.Kernels.Length)} kernels");
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private static async Task<Shader> ConvertAsset(string path, Dictionary<string, string> extraction, string destination,
        string compiler, string cross, string scratch, CancellationToken cancellationToken)
    {
        var source = LoadVerified(path, extraction[path]);
        var metadata = LoadVerified(path + ".meta", extraction[path + ".meta"]);
        const string rootMarker = "\nComputeShader:\n";
        var root = source.IndexOf(rootMarker, StringComparison.Ordinal);
        if (root < 0 || !source[..root].Contains("--- !u!72 &7200000", StringComparison.Ordinal))
            throw new InvalidDataException("Expected an exported Unity ComputeShader: " + path);
        var yaml = new YamlStream();
        yaml.Load(new StringReader(source[(root + 1)..]));
        var variant = Sequence(Field(Field(yaml.Documents.Single().RootNode, "ComputeShader"), "variants")).Single();
        if (Value(variant, "targetRenderer") != "2")
            throw new InvalidDataException("Expected Direct3D as the single source platform: " + path);
        var buffers = Sequence(Field(variant, "constantBuffers")).ToDictionary(node => Value(node, "name"), StringComparer.Ordinal);
        var kernels = new List<KernelSource>();
        foreach (var kernel in Sequence(Field(variant, "kernels")))
            kernels.Add(await ConvertKernel(kernel, buffers, compiler, cross, scratch, cancellationToken));
        if (kernels.Count == 0 || kernels.Select(kernel => kernel.Evidence.Name).Distinct(StringComparer.Ordinal).Count() != kernels.Count)
            throw new InvalidDataException("Invalid or duplicate kernels: " + path);

        var compute = Path.Combine(destination, Path.GetFileNameWithoutExtension(path) + ".compute");
        using (var writer = File.CreateText(compute))
        {
            for (int index = 0; index < kernels.Count; index++)
                writer.Write($"#pragma kernel {kernels[index].Evidence.Name} NATIVEH3VR_COMPUTE_{index}\n");
            for (int index = 0; index < kernels.Count; index++)
                writer.Write($"\n#if defined(NATIVEH3VR_COMPUTE_{index})\n{kernels[index].Hlsl}#endif\n");
        }

        var guid = GuidLine().Match(metadata).Groups[1].Value;
        if (guid.Length == 0) throw new InvalidDataException("Compute metadata lacks a GUID: " + path);
        var metaPath = compute + ".meta";
        var importer = new[] { "fileFormatVersion: 2", "guid: " + guid, "ComputeShaderImporter:", "  externalObjects: {}" };
        File.WriteAllText(metaPath, string.Join('\n', importer.Concat(ImportSettings().Matches(metadata).Select(match => match.Value))) + "\n");
        return new(Path.GetFileName(path), extraction[path], extraction[path + ".meta"], Path.GetFileName(compute),
            SafeFiles.Hash(compute), SafeFiles.Hash(metaPath), kernels.Select(kernel => kernel.Evidence).ToArray());
    }

    private static string LoadVerified(string path, string expected)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException("Symlinked compute input: " + path);
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected)
            throw new InvalidDataException("Compute input differs from the extraction receipt: " + path);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static async Task<KernelSource> ConvertKernel(YamlNode kernel, Dictionary<string, YamlNode> buffers,
        string compiler, string cross, string scratch, CancellationToken cancellationToken)
    {
        var name = Symbol(Value(kernel, "name"));
        var dxbc = Convert.FromHexString(Value(kernel, "code"));
        if (!dxbc.AsSpan().StartsWith("DXBC"u8)) throw new InvalidDataException("Expected Direct3D bytecode for " + name);
        var threadGroup = Convert.FromHexString(Value(kernel, "threadGroupSize"));
        if (threadGroup.Length != 12) throw new InvalidDataException("Invalid original workgroup size for " + name);
        int[] threads = [BinaryPrimitives.ReadInt32LittleEndian(threadGroup), BinaryPrimitives.ReadInt32LittleEndian(threadGroup.AsSpan(4)),
            BinaryPrimitives.ReadInt32LittleEndian(threadGroup.AsSpan(8))];
        var bytecode = Path.Combine(scratch, "kernel.dxbc");
        var spirv = Path.Combine(scratch, "kernel.spv");
        File.WriteAllBytes(bytecode, dxbc);
        File.Delete(spirv);
        await RunTool(compiler, [bytecode, "--output", spirv, "--validate", "--ssbo-uav", "--ssbo-srv"], cancellationToken);
        using var reflection = JsonDocument.Parse(await RunTool(cross, [spirv, "--reflect"], cancellationToken));
        var entry = reflection.RootElement.GetProperty("entryPoints").EnumerateArray().Single();
        if (entry.GetProperty("mode").GetString() != "comp" ||
            !entry.GetProperty("workgroup_size").EnumerateArray().Select(item => item.GetInt32()).SequenceEqual(threads))
            throw new InvalidDataException("SPIR-V workgroup differs from the installed Unity program: " + name);
        var hlsl = await RunTool(cross, [spirv, "--hlsl", "--shader-model", "50", "--flatten-ubo", "--rename-entry-point",
            entry.GetProperty("name").GetString()!, name, "comp"], cancellationToken);
        hlsl = RestoreConstants(hlsl, reflection.RootElement, buffers, kernel);
        hlsl = RestoreResources(hlsl, kernel);
        return new(new(name, threads, SafeFiles.Hash(bytecode), SafeFiles.Hash(spirv)), hlsl);
    }

    private static string RestoreConstants(string source, JsonElement reflection, Dictionary<string, YamlNode> buffers, YamlNode kernel)
    {
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        var vectors = new Dictionary<int, Dictionary<int, string>>();
        foreach (var binding in Sequence(Field(kernel, "cbs")))
        {
            var registers = new Dictionary<int, string>();
            vectors.Add(Number(binding, "bindPoint"), registers);
            foreach (var parameter in Sequence(Field(buffers[Value(binding, "name")], "params")))
            {
                var name = Symbol(Value(parameter, "name"));
                var width = Number(parameter, "colCount");
                var offset = Number(parameter, "offset");
                var array = Number(parameter, "arraySize");
                if (Value(parameter, "type") != "0" || Value(parameter, "rowCount") != "1" || width is < 1 or > 4 ||
                    offset < 0 || offset % 16 != 0 || array < 0)
                    throw new InvalidDataException("Unsupported original constant layout: " + name);
                declarations.Add(name, $"float{(width > 1 ? width : "")} {name}{(array > 0 ? $"[{array}]" : "")};");
                for (int index = 0; index < Math.Max(1, array); index++)
                {
                    var value = array > 0 ? $"{name}[{index}]" : name;
                    if (width != 4) value = $"float4({value}, {string.Join(", ", Enumerable.Repeat("0.0", 4 - width))})";
                    registers.Add(offset / 16 + index, value);
                }
            }
        }
        var locations = reflection.TryGetProperty("ubos", out var ubos)
            ? ubos.EnumerateArray().ToDictionary(entry => entry.GetProperty("type").GetString()!, entry => entry.GetProperty("binding").GetInt32())
            : [];
        var uniforms = Uniform().Matches(source).Select(match => match.Groups[1].Value).ToArray();
        if (!uniforms.ToHashSet(StringComparer.Ordinal).SetEquals(locations.Keys))
            throw new InvalidDataException("Unexpected SPIRV-Cross constant buffer layout.");
        source = Uniform().Replace(source, "");
        foreach (var symbol in uniforms)
        {
            var registers = vectors[locations[symbol]];
            source = Regex.Replace(source, $@"\b{symbol}\[(\d+)\]", match => registers[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)]);
            if (Regex.IsMatch(source, $@"\b{symbol}\b"))
                throw new InvalidDataException("Cannot bind the computed constant offset to original Unity properties: " + symbol);
        }
        return string.Join('\n', declarations.Values) + "\n\n" + source;
    }

    private static string RestoreResources(string source, YamlNode kernel)
    {
        var inputs = new Dictionary<int, string>();
        var samplers = new Dictionary<int, string>();
        var outputs = new Dictionary<int, string>();
        foreach (var texture in Sequence(Field(kernel, "textures")))
        {
            var name = Symbol(Value(texture, "name"));
            var register = Number(texture, "bindPoint");
            inputs.Add(register & 0xffff, name);
            if (register >= 0) samplers.Add(register >> 16, "sampler" + name);
        }
        foreach (var sampler in Sequence(Field(kernel, "builtinSamplers")))
        {
            if (Value(sampler, "sampler") != "1") throw new InvalidDataException("Unrecognized Unity inline sampler mode.");
            samplers.Add(Number(sampler, "bindPoint"), "sampler_LinearClamp");
        }
        foreach (var input in Sequence(Field(kernel, "inBuffers"))) inputs.Add(Number(input, "bindPoint"), Symbol(Value(input, "name")));
        foreach (var output in Sequence(Field(kernel, "outBuffers"))) outputs.Add(Number(output, "bindPoint"), Symbol(Value(output, "name")));
        var registers = new Dictionary<string, Dictionary<int, string>>() { ["t"] = inputs, ["s"] = samplers, ["u"] = outputs };
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Resource().Matches(source))
        {
            var ordinal = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (!registers[match.Groups[2].Value].Remove(ordinal, out var identifier))
                throw new InvalidDataException("Missing original Unity resource binding: " + match.Value);
            replacements.Add(match.Groups[1].Value, identifier);
        }
        if (registers.Values.Any(binding => binding.Count > 0))
            throw new InvalidDataException("Original Unity compute bindings are missing in translated HLSL.");
        return Words().Replace(source, word => replacements.GetValueOrDefault(word.Value, word.Value));
    }

    private static async Task<string> RunTool(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Unable to launch " + executable);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}\n{await errors}\n{await output}");
        await errors;
        return await output;
    }

    private static YamlNode Field(YamlNode node, string name) => ((YamlMappingNode)node)[new YamlScalarNode(name)];
    private static IEnumerable<YamlNode> Sequence(YamlNode node) => ((YamlSequenceNode)node).Children;
    private static string Value(YamlNode node, string name) =>
        ((YamlScalarNode)Field(node, name)).Value ?? throw new InvalidDataException("Missing shader value: " + name);
    private static int Number(YamlNode node, string name) => int.Parse(Value(node, name), CultureInfo.InvariantCulture);
    private static string Symbol(string symbol) =>
        Identifier().IsMatch(symbol) ? symbol : throw new InvalidDataException("Invalid compute symbol: " + symbol);
}
