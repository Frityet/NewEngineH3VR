// Worker-local instrumentation around AssetRipper 2.0.0's original shader exporters.
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Shaders;
using AssetRipper.IO.Files;
using AssetRipper.SourceGenerated.Classes.ClassID_48;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AssetRipper.Worker.Tests")]

internal sealed record ShaderIdentity(string CollectionPath, string CollectionName, string[] BundleChain,
    long PathId, string Guid, string ExportPath, string Exporter);

internal sealed class ShaderIdentityRecorder
{
    private readonly List<ShaderIdentity> rows = [];
    public void Record(IShader asset, string path, string root, string guid, string exporter)
    {
        if (asset.Collection is not SerializedAssetCollection) return;
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidDataException("Shader exporter wrote outside its project root.");
        var chain = new List<string>();
        for (Bundle? bundle = asset.Collection.Bundle; bundle is not null; bundle = bundle.Parent) chain.Add(bundle.Name);
        Add(new(asset.Collection.FilePath, asset.Collection.Name, chain.ToArray(), asset.PathID, guid, relative, exporter));
    }

    internal void Add(ShaderIdentity identity) => rows.Add(identity);

    public void Write(string output)
    {
        var project = Path.Combine(output, "ExportedProject");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var originals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!paths.Add(row.ExportPath) || !guids.Add(row.Guid)) throw new InvalidDataException("Duplicate shader export path or GUID.");
            if (!originals.Add(JsonSerializer.Serialize(new { row.CollectionPath, row.CollectionName, row.BundleChain, row.PathId })))
                throw new InvalidDataException("Ambiguous serialized shader identity; cannot safely record an object-to-export mapping.");
            var file = Path.Combine(project, row.ExportPath);
            if (!File.Exists(file) || !File.Exists(file + ".meta")) throw new InvalidDataException("Shader export or metadata is missing: " + row.ExportPath);
            var match = Regex.Match(File.ReadAllText(file + ".meta"), @"(?m)^guid:\s*([0-9a-fA-F]{32})\s*$");
            if (!match.Success || !match.Groups[1].Value.Equals(row.Guid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Shader export metadata does not match its captured GUID: " + row.ExportPath);
        }
        var path = Path.Combine(output, "AuxiliaryFiles", "shader-identity-v2.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 1, exporter = "AssetRipper 2.0.0", shaders = rows }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

internal sealed class IdentityExportHandler(FullConfiguration config, ShaderIdentityRecorder identities) : ExportHandler(config)
{
    protected override void BeforeExport(ProjectExporter projectExporter) => projectExporter.OverrideExporter<IShader>(new IdentityShaderExporter(identities));
}

internal sealed class IdentityShaderExporter(ShaderIdentityRecorder identities) : ShaderExporterBase
{
    private readonly SimpleShaderExporter simple = new();
    public override bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out IExportCollection? collection)
    {
        if (asset is not IShader shader) { collection = null; return false; }
        // Preserve SimpleShaderExporter's selection rule from pinned AssetRipper.
        var text = shader.Has_Script() ? shader.Script.String : null;
        var useSimple = !string.IsNullOrEmpty(text) && !text.Contains("Program") && !text.Contains("SubProgram");
        collection = new IdentityShaderCollection(this, shader, identities, useSimple);
        return true;
    }
    public bool ExportSelected(IExportContainer container, IShader shader, string path, FileSystem fs, bool useSimple) =>
        useSimple ? simple.Export(container, shader, path, fs) : DummyShaderTextExporter.ExportShader(shader, path, fs);
}

internal sealed class IdentityShaderCollection(IdentityShaderExporter exporter, IShader asset,
    ShaderIdentityRecorder identities, bool simple) : ShaderExportCollection(exporter, asset)
{
    protected override bool ExportInner(IExportContainer container, string filePath, string dirPath, FileSystem fs)
    {
        var success = exporter.ExportSelected(container, Asset, filePath, fs, simple);
        if (success) identities.Record(Asset, filePath, dirPath, GUID.ToString(), simple ? "SimpleShaderExporter" : "DummyShaderTextExporter");
        return success;
    }
}
