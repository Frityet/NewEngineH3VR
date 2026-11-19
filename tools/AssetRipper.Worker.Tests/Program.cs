using AssetsTools.NET;
using AssetsTools.NET.Extra;
using H3VR.Converter;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

var Strings = new[] { "TextAsset", "Base", "string", "m_Name", "Array", "int", "size", "char", "data", "m_Script" };
var root = Path.Combine(Path.GetTempPath(), "assetripper-worker-fixture-" + Guid.NewGuid().ToString("N"));
var input = Path.Combine(root, "input");
Directory.CreateDirectory(input);
var firstExport = Path.Combine(root, "export-online");
var secondExport = Path.Combine(root, "export-offline");
var config = new ConversionConfig { GameDirectory = input, ToolCacheDirectory = Path.Combine(root, "cache") };
try
{
    CreateSyntheticTextAsset(Path.Combine(input, "synthetic.assets"), "2021.3.45f1", "SyntheticHello", "ASSET-RIPPER-FIXTURE-TEST");

    var repository = Repository.Find();
    await AssetRipperIntegration.ExportAsync(repository, config, firstExport, Path.Combine(root, "online.log"), CancellationToken.None);
    ValidateExport(firstExport);
    var git = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in new[] { "config", "--local", "--get", "submodule." + AssetRipperIntegration.SubmodulePath + ".url" }) git.ArgumentList.Add(argument);
    using (var process = Process.Start(git)!)
    {
        var url = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || url.Trim() != AssetRipperIntegration.UpstreamUrl)
            throw new InvalidDataException("First use did not register the pinned submodule in the checkout.");
    }

    var offline = config with { Offline = true };
    if (!AssetRipperIntegration.CheckAvailability(offline, repository, out var detail))
        throw new InvalidDataException("Verified worker cache was not reported available: " + detail);
    await AssetRipperIntegration.ExportAsync(repository, offline, secondExport, Path.Combine(root, "offline.log"), CancellationToken.None);
    ValidateExport(secondExport);
    Console.WriteLine("PASS synthetic TextAsset exported through online build and verified offline cache.");

    var worker = Directory.EnumerateFiles(AssetRipperIntegration.DefaultCacheDirectory(config, repository), "AssetRipper.Worker.dll", SearchOption.AllDirectories).Single();
    ValidateSyntheticShaderIdentity(worker);
    File.AppendAllText(worker, "tampered fixture");
    if (AssetRipperIntegration.CheckAvailability(offline, repository, out _))
        throw new InvalidDataException("A modified worker was accepted by the integrity check.");
    try
    {
        await AssetRipperIntegration.ExportAsync(repository, offline, Path.Combine(root, "export-rejected"), Path.Combine(root, "rejected.log"), CancellationToken.None);
        throw new InvalidDataException("A modified cached worker was executed.");
    }
    catch (InvalidOperationException error) when (error.Message.Contains("Offline mode", StringComparison.Ordinal)) { }
    if (Directory.Exists(Path.Combine(root, "export-rejected")) || !File.Exists(worker))
        throw new InvalidDataException("Refusing a modified cache must leave it inspectable without creating an export.");
    Console.WriteLine("PASS tampered worker cache is refused without executing or replacing it.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

void ValidateSyntheticShaderIdentity(string workerPath)
{
    // The converter provisions the worker during the cold export above.
    var worker = Assembly.Load(File.ReadAllBytes(workerPath));
    var identityType = worker.GetType("ShaderIdentity", throwOnError: true)!;
    var recorderType = worker.GetType("ShaderIdentityRecorder", throwOnError: true)!;
    var add = recorderType.GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var write = recorderType.GetMethod("Write")!;
    var output = Path.Combine(root, "identity-fixture");
    const string shaderPath = "Assets/Synthetic.shader";
    var shader = Path.Combine(output, "ExportedProject", shaderPath);
    Directory.CreateDirectory(Path.GetDirectoryName(shader)!);
    Directory.CreateDirectory(Path.Combine(output, "AuxiliaryFiles"));
    File.WriteAllText(shader, "synthetic shader");
    var guid = new string('a', 32);
    File.WriteAllText(shader + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");

    object Identity(string id, string exportPath) =>
        Activator.CreateInstance(identityType, ["source", "synthetic", new[] { "fixture" }, -7L, id, exportPath, "DummyShaderTextExporter"])!;

    Action<string> CreateWriter(params object[] records)
    {
        var recorder = Activator.CreateInstance(recorderType)!;
        foreach (var record in records)
            add.Invoke(recorder, [record]);
        return write.CreateDelegate<Action<string>>(recorder);
    }

    void Reject(Action<string> writeMap, string message)
    {
        try { writeMap(output); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    var identity = Identity(guid, shaderPath);
    var writeIdentity = CreateWriter(identity);
    writeIdentity(output);
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "AuxiliaryFiles", "shader-identity-v2.json")));
    if (json.RootElement.GetProperty("shaders")[0].GetProperty("PathId").GetInt64() != -7) throw new InvalidDataException("Synthetic identity lost its signed path ID.");
    Reject(CreateWriter(identity, identity), "Duplicate shader identity was accepted.");
    Reject(CreateWriter(identity, Identity(new string('b', 32), "Assets/Other.shader")), "Ambiguous original shader identity was accepted.");
    File.WriteAllText(shader + ".meta", "guid: " + new string('b', 32) + "\n");
    Reject(writeIdentity, "Mismatched shader GUID was accepted.");
    Console.WriteLine("PASS synthetic shader identities preserve path IDs and reject duplicates or metadata mismatches.");
}

void ValidateExport(string export)
{
    var project = Path.Combine(export, "ExportedProject");
    var projectVersion = Path.Combine(project, "ProjectSettings", "ProjectVersion.txt");
    var pathMapPath = Path.Combine(export, "AuxiliaryFiles", "path_id_map.json");
    var shaderMap = Path.Combine(export, "AuxiliaryFiles", "shader-identity-v2.json");
    var textAssetPath = Path.Combine(project, "Assets", "TextAsset", "SyntheticHello.txt");
    if (!File.Exists(projectVersion) || !File.Exists(pathMapPath) || !File.Exists(textAssetPath))
        throw new InvalidDataException("AssetRipper output missed ProjectVersion.txt, path_id_map.json, or the synthetic TextAsset.");
    using (var identities = JsonDocument.Parse(File.ReadAllText(shaderMap)))
        if (identities.RootElement.GetProperty("shaders").GetArrayLength() != 0)
            throw new InvalidDataException("Text-only fixture should not produce shader identities.");
    if (!File.ReadAllText(projectVersion).Contains("2021.3.45f1", StringComparison.Ordinal))
        throw new InvalidDataException("Exported ProjectVersion.txt did not preserve the fixture engine version.");
    if (File.ReadAllText(textAssetPath) != "ASSET-RIPPER-FIXTURE-TEST")
        throw new InvalidDataException("Exported TextAsset bytes did not match the synthetic fixture.");
    using var json = JsonDocument.Parse(File.ReadAllText(pathMapPath));
    var files = json.RootElement.GetProperty("Files");
    var asset = files.EnumerateArray().SelectMany(file => file.GetProperty("Assets").EnumerateArray())
        .SingleOrDefault(value => value.GetProperty("PathID").GetInt64() == 123);
    if (asset.ValueKind == JsonValueKind.Undefined || asset.GetProperty("Name").GetString() != "SyntheticHello" ||
        asset.GetProperty("Type").GetString() != "TextAsset")
        throw new InvalidDataException("path_id_map.json omitted the synthetic TextAsset identity.");
}

void CreateSyntheticTextAsset(string path, string engineVersion, string name, string content)
{
    var nodes = new List<TypeTreeNode>();
    uint Offset(string value)
    {
        uint offset = 0;
        foreach (var item in Strings)
        {
            if (item == value) return offset;
            offset += (uint)Encoding.UTF8.GetByteCount(item) + 1;
        }
        throw new InvalidOperationException("Missing type-tree string: " + value);
    }
    TypeTreeNode Node(string type, string field, byte level, int size, uint meta = 0) => new()
    {
        Version = 1,
        Level = level,
        TypeFlags = type == "Array" ? TypeTreeNodeFlags.Array : 0,
        TypeStrOffset = Offset(type),
        NameStrOffset = Offset(field),
        ByteSize = size,
        Index = (uint)nodes.Count,
        MetaFlags = meta
    };
    nodes.Add(Node("TextAsset", "Base", 0, -1));
    nodes.Add(Node("string", "m_Name", 1, -1, 0x4000));
    nodes.Add(Node("Array", "Array", 2, -1));
    nodes.Add(Node("int", "size", 3, 4));
    nodes.Add(Node("char", "data", 3, 1));
    nodes.Add(Node("string", "m_Script", 1, -1, 0x4000));
    nodes.Add(Node("Array", "Array", 2, -1));
    nodes.Add(Node("int", "size", 3, 4));
    nodes.Add(Node("char", "data", 3, 1));
    var type = new TypeTreeType
    {
        TypeId = 49,
        IsStrippedType = false,
        ScriptTypeIndex = ushort.MaxValue,
        ScriptIdHash = Hash128.NewBlankHash(),
        TypeHash = Hash128.NewBlankHash(),
        ExtTypeHash = Hash128.NewBlankHash(),
        TypeBlobIsDefinition = true,
        IsRefType = false,
        TypeDependencies = [],
        TypeBlob = new TypeTreeBlob { Nodes = nodes, StringBufferBytes = Encoding.UTF8.GetBytes(string.Join('\0', Strings) + "\0") }
    };

    using var stream = new MemoryStream();
    using var writer = new AssetsFileWriter(stream);
    var header = new AssetsFileHeader { MetadataSize = 0, FileSize = -1, Version = 22, DataOffset = -1, Endianness = false };
    var metadata = new AssetsFileMetadata
    {
        UnityVersion = engineVersion,
        TargetPlatform = 19,
        TypeTreeEnabled = true,
        TypeTreeTypes = [type],
        AssetInfos = new List<AssetFileInfo>(),
        ScriptTypes = new(),
        Externals = new(),
        RefTypes = new(),
        UserInformation = ""
    };
    header.Write(writer);
    metadata.Write(writer, header.Version);
    writer.Write((uint)0);
    writer.Align();
    writer.Write((uint)0);
    writer.Write((uint)0);
    writer.Write(0);
    uint metadataSize = (uint)(writer.Position - 0x13) - 0x1c;
    while (writer.Position < 0x1000) writer.Write((byte)0);
    var end = writer.Position;
    header.FileSize = end;
    header.DataOffset = end;
    header.MetadataSize = metadataSize;
    writer.Position = 0;
    header.Write(writer);
    writer.Position = end;
    File.WriteAllBytes(path, stream.ToArray());

    var manager = new AssetsManager();
    var instance = manager.LoadAssetsFile(path, false);
    var value = manager.CreateValueBaseField(instance, 49, ushort.MaxValue);
    value["m_Name"].AsString = name;
    value["m_Script"].AsString = content;
    var info = new AssetFileInfo
    {
        PathId = 123,
        TypeIdOrIndex = 0,
        TypeId = 49,
        ScriptTypeIndex = ushort.MaxValue,
        Stripped = 0
    };
    info.SetNewData(value);
    instance.file.Metadata.AddAssetInfo(info);
    var finalPath = path + ".final";
    using (var output = new AssetsFileWriter(finalPath)) instance.file.Write(output);
    manager.UnloadAllAssetsFiles();
    File.Move(finalPath, path, overwrite: true);
}
