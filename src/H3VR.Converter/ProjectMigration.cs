using Mono.Cecil;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace H3VR.Converter;

public sealed record ProjectMigrationReport(string Project, string UnityVersion, Dictionary<string, string> Assemblies,
    int ScriptBindings, int RewrittenReferences, int ExecutionOrderEntries, string RecoverableSourceDirectory,
    string ShaderOverlay, string ProjectVersion, string OpenXrPackage, string AssetBundleMembership);

/// <summary>Rewrites only a copied candidate project. RecoveredProject is treated as immutable input.</summary>
public sealed class ProjectMigration(ConversionConfig config, string candidate)
{
    private static readonly Regex GuidPattern = new(@"(?m)^guid: ([0-9a-f]{32})$", RegexOptions.Compiled);
    private static readonly Regex ScriptPattern = new(@"m_Script:\s*\{fileID:\s*(-?\d+),\s*guid:\s*([0-9a-f]{32}),\s*type:\s*\d+\}", RegexOptions.Compiled);
    private static readonly Regex NamespacePattern = new(@"\bnamespace\s+([\w.]+)", RegexOptions.Compiled);
    private static readonly Regex TypePattern = new(@"\b(?:class|struct|enum|interface)\s+(\w+)", RegexOptions.Compiled);
    private static readonly Dictionary<string, string> AssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Assembly-CSharp"] = "NativeH3VR.Game",
        ["Assembly-CSharp-firstpass"] = "NativeH3VR.FirstPass",
        ["Assembly-UnityScript"] = "NativeH3VR.UnityScript"
    };

    private sealed record BindingTarget(string Assembly, string Namespace, string Class, string Guid, int FileId);
    private sealed record RecoveredType(string Namespace, string Class, string Guid, string Relative, string SourceAssembly, int ExecutionOrder);

    public ProjectMigrationReport Run()
    {
        var root = Path.GetFullPath(candidate); var assets = Path.Combine(root, "Assets");
        if (!Directory.Exists(assets)) throw new InvalidDataException("Candidate project has no Assets directory.");
        SafeFiles.RequireSeparate(config.Export, root);
        RejectLinks(root);
        var archive = Path.Combine(root, "NativeH3VR-RecoveredSources");
        var managedArchive = Path.Combine(archive, "ManagedPlugins");
        var managedFiles = Directory.Exists(config.ManagedOutput) ? SafeFiles.Files(config.ManagedOutput).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Order().ToArray() : [];
        if (managedFiles.Length == 0) throw new InvalidDataException("Retargeted managed output is missing or contains no DLLs.");

        var dlls = new Dictionary<string, (string Path, string Guid)>(StringComparer.OrdinalIgnoreCase);
        var pluginDir = Path.Combine(assets, "Plugins"); Directory.CreateDirectory(pluginDir);
        foreach (var source in managedFiles)
        {
            var name = Path.GetFileName(source); var destination = Path.Combine(pluginDir, name); var meta = destination + ".meta";
            EnsureCandidatePath(root, destination);
            var guid = (File.Exists(meta) ? ReadGuid(File.ReadAllText(meta)) : null) ?? DeterministicGuid("NativeH3VR.Managed:" + name);
            if (File.Exists(destination))
            {
                Directory.CreateDirectory(managedArchive);
                var saved = Path.Combine(managedArchive, name);
                if (File.Exists(saved)) throw new IOException("Recoverable original managed assembly already exists: " + saved);
                File.Move(destination, saved);
            }
            File.Copy(source, destination); WritePluginMeta(meta, guid, ReadExecutionOrders(source));
            dlls[name] = (destination, guid);
        }

        var recovered = CollectRecoveredTypes(assets);
        var assemblyTypes = ReadTypeMetadata(config.ManagedOutput);
        var originalTypes = Directory.Exists(config.ManagedInput) ? ReadTypeMetadata(config.ManagedInput) : new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var bindings = new Dictionary<(int, string), BindingTarget>(); var bindingRows = new List<object>();
        var orders = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceType in recovered)
        {
            if (string.IsNullOrEmpty(sourceType.Guid)) continue;
            var mappedAssembly = AssemblyNames.TryGetValue(sourceType.SourceAssembly, out var rename) ? rename : sourceType.SourceAssembly;
            var dllName = mappedAssembly + ".dll";
            if (!dlls.TryGetValue(dllName, out var targetAssembly)) continue;
            var fullName = (sourceType.Namespace.Length == 0 ? "" : sourceType.Namespace + ".") + sourceType.Class;
            var originalName = sourceType.SourceAssembly + ".dll";
            if (!originalTypes.TryGetValue(originalName, out var sourceTypes) || !sourceTypes.Contains(fullName)) continue;
            if (!assemblyTypes.TryGetValue(dllName, out var types) || !types.Contains(fullName))
                throw new InvalidDataException($"Recovered type {fullName} exists in {originalName} but is missing from retargeted {dllName}.");
            var target = new BindingTarget(dllName, sourceType.Namespace, sourceType.Class, targetAssembly.Guid, Md4.UnityFileId(sourceType.Namespace, sourceType.Class));
            var key = (11500000, sourceType.Guid);
            if (bindings.TryGetValue(key, out var previous) && previous != target)
                throw new InvalidDataException($"Recovered script GUID {sourceType.Guid} maps to multiple managed types.");
            bindings[key] = target;
            bindingRows.Add(new { source_guid = sourceType.Guid, source_path = sourceType.Relative, assembly = target.Assembly, @namespace = target.Namespace, class_name = target.Class, guid = target.Guid, fileID = target.FileId });
            if (sourceType.ExecutionOrder != 0)
            {
                if (!orders.TryGetValue(dllName, out var perAssembly)) orders[dllName] = perAssembly = new();
                perAssembly[fullName] = sourceType.ExecutionOrder;
            }
        }
        AddBuiltInUguiBindings(bindings, bindingRows, dlls, assemblyTypes, originalTypes);
        var recoveredGuids = recovered.Select(r => r.Guid).Where(g => !string.IsNullOrEmpty(g)).ToHashSet(StringComparer.Ordinal);
        var referenceCounts = new Dictionary<string, int>(); var unresolved = new HashSet<string>();
        foreach (var path in SafeFiles.Files(assets).Where(IsUnitySerializedText))
        {
            var text = File.ReadAllText(path, Encoding.UTF8); if (!text.Contains("m_Script:", StringComparison.Ordinal)) continue;
            var count = 0;
            var changed = ScriptPattern.Replace(text, match =>
            {
                var key = (int.Parse(match.Groups[1].Value), match.Groups[2].Value);
                if (!bindings.TryGetValue(key, out var target)) { if (key.Item1 == 11500000) unresolved.Add(key.Item2); return match.Value; }
                count++; return $"m_Script: {{fileID: {target.FileId}, guid: {target.Guid}, type: 3}}";
            });
            if (count > 0) { File.WriteAllText(path, changed, new UTF8Encoding(false)); referenceCounts[Path.GetRelativePath(root, path)] = count; }
        }
        var unresolvedRecovered = unresolved.Intersect(recoveredGuids, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (unresolvedRecovered.Length > 0)
            throw new InvalidDataException($"{unresolvedRecovered.Length} serialized script GUID(s) still point at recovered source scripts whose managed type could not be rebound; first GUID: {unresolvedRecovered[0]}.");

        Directory.CreateDirectory(archive);
        foreach (var relative in new[] { "Assets/Scripts/Assembly-CSharp", "Assets/Plugins/Assembly-CSharp-firstpass", "Assets/Scripts/Assembly-UnityScript" })
            MoveCandidatePath(root, relative, archive);
        foreach (var name in new[] { "System.Runtime.CompilerServices.Unsafe.dll", "System.Runtime.InteropServices.dll" })
            MoveCandidatePath(root, "Assets/Plugins/" + name, archive);
        RemoveUguiDuplicate(root);
        var shaderOverlay = ApplyShaderOverlay(root, assets);
        RejectDummyShaders(assets);
        CopySteamWindowsPlugins(root, assets);
        CopySteamVrActions(root, assets);
        ConfigureOpenXr(root);
        PinProjectVersion(root);
        var bundleMembership = CaptureBundleMembership(root, assets, archive);
        return new ProjectMigrationReport(root, ConversionConfig.TargetVersion,
            dlls.ToDictionary(p => p.Key, p => Path.GetRelativePath(root, p.Value.Path)), bindingRows.Count,
            referenceCounts.Values.Sum(), orders.Values.Sum(x => x.Count), Path.GetRelativePath(root, archive), shaderOverlay,
            Path.Combine("ProjectSettings", "ProjectVersion.txt"), "com.unity.xr.openxr@1.13.0", bundleMembership);
    }

    private void CopySteamWindowsPlugins(string root, string assets)
    {
        var sourceRoot = Path.Combine(config.GameData, "Plugins"); if (!Directory.Exists(sourceRoot)) return;
        RejectLinks(sourceRoot);
        foreach (var source in SafeFiles.Files(sourceRoot).Where(p => Path.GetFileName(p).StartsWith("steam_api", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var sourceHash = SafeFiles.Hash(source);
            var destination = Path.Combine(assets, "Plugins", "x86_64", Path.GetFileName(source));
            EnsureCandidatePath(root, destination);
            if (File.Exists(destination))
            {
                if (!SafeFiles.Hash(destination).Equals(sourceHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Candidate contains a different Steam native plugin: " + destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, false);
            File.WriteAllText(destination + ".meta", "fileFormatVersion: 2\nguid: " + DeterministicGuid("NativeH3VR.SteamWindows:" + Path.GetFileName(source)) + "\nPluginImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  iconMap: {}\n  executionOrder: {}\n  defineConstraints: []\n  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n  validateReferences: 1\n  platformData:\n  - first:\n      Any:\n    second:\n      enabled: 0\n      settings: {}\n  - first:\n      Editor: Editor\n    second:\n      enabled: 0\n      settings:\n        DefaultValueInitialized: true\n  - first:\n      Standalone: Win64\n    second:\n      enabled: 1\n      settings:\n        CPU: x86_64\n        OS: Windows\n  userData: local Windows Steam runtime plugin\n  assetBundleName:\n  assetBundleVariant:\n");
        }
    }

    private void CopySteamVrActions(string root, string assets)
    {
        var sourceRoot = Path.GetFullPath(config.GameDirectory);
        var actions = Path.Combine(sourceRoot, "actions.json");
        if (!File.Exists(actions)) throw new FileNotFoundException("H3VR root action manifest is required for the OpenXR input adapter.", actions);
        RejectActionSourceLinks(sourceRoot, actions);
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(actions)); }
        catch (JsonException e) { throw new InvalidDataException("H3VR actions.json is not valid JSON.", e); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("default_bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array || bindings.GetArrayLength() == 0)
                throw new InvalidDataException("H3VR actions.json must list at least one default controller binding.");
            var sources = new List<string> { actions };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in bindings.EnumerateArray())
            {
                if (binding.ValueKind != JsonValueKind.Object || !binding.TryGetProperty("binding_url", out var bindingUrl) || bindingUrl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(bindingUrl.GetString()))
                    throw new InvalidDataException("H3VR actions.json contains a default binding without a binding_url.");
                var relative = bindingUrl.GetString()!.Replace('\\', '/');
                var source = SafeFiles.Inside(sourceRoot, relative);
                if (!File.Exists(source)) throw new FileNotFoundException("A binding referenced by H3VR actions.json is missing.", source);
                RejectActionSourceLinks(sourceRoot, source);
                if (!seen.Add(relative)) throw new InvalidDataException("H3VR actions.json lists a duplicate default binding: " + relative);
                try { using var bindingDocument = JsonDocument.Parse(File.ReadAllText(source)); }
                catch (JsonException e) { throw new InvalidDataException("Referenced H3VR controller binding is not valid JSON: " + relative, e); }
                sources.Add(source);
            }
            foreach (var source in sources)
            {
                var relative = Path.GetRelativePath(sourceRoot, source).Replace(Path.DirectorySeparatorChar, '/');
                var destination = SafeFiles.Inside(assets, "StreamingAssets/SteamVR/" + relative);
                EnsureCandidatePath(root, destination);
                var sourceHash = SafeFiles.Hash(source);
                if (File.Exists(destination))
                {
                    if (!SafeFiles.Hash(destination).Equals(sourceHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Candidate contains different SteamVR action data: " + relative);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, false);
                }
                var metaSource = source + ".meta";
                if (File.Exists(metaSource))
                {
                    var metaDestination = destination + ".meta";
                    if (File.Exists(metaDestination) && !SafeFiles.Hash(metaDestination).Equals(SafeFiles.Hash(metaSource), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Candidate contains different SteamVR action metadata: " + relative);
                    if (!File.Exists(metaDestination)) File.Copy(metaSource, metaDestination, false);
                }
            }
        }
    }

    private static void RejectActionSourceLinks(string root, string path)
    {
        root = Path.GetFullPath(root); path = Path.GetFullPath(path);
        if (!SafeFiles.Contains(root, path)) throw new InvalidDataException("SteamVR action input escapes the installed game directory: " + path);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo item = File.Exists(current) ? new FileInfo(current) : new DirectoryInfo(current);
            if (item.LinkTarget is not null) throw new InvalidDataException("Symlinked SteamVR action input is not supported: " + current);
            if (current.Equals(root, StringComparison.Ordinal)) break;
        }
    }

    private void AddBuiltInUguiBindings(Dictionary<(int, string), BindingTarget> bindings, List<object> rows,
        Dictionary<string, (string Path, string Guid)> dlls, Dictionary<string, HashSet<string>> migrated,
        Dictionary<string, HashSet<string>> originals)
    {
        const string assemblyName = "UnityEngine.UI.dll";
        if (!dlls.TryGetValue(assemblyName, out var assembly)) return;
        var originalName = assemblyName;
        if (!originals.TryGetValue(originalName, out var originalTypes) || !migrated.TryGetValue(assemblyName, out var targetTypes)) return;
        var package = Path.Combine(config.UnityData, "Resources", "PackageManager", "BuiltInPackages", "com.unity.ugui");
        if (!Directory.Exists(package)) return;
        foreach (var source in SafeFiles.Files(package).Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var meta = source + ".meta"; if (!File.Exists(meta)) continue;
            var content = File.ReadAllText(source); var nsMatch = NamespacePattern.Match(content); var ns = nsMatch.Success ? nsMatch.Groups[1].Value : "";
            if (ns is not ("UnityEngine.UI" or "UnityEngine.EventSystems")) continue;
            var className = Path.GetFileNameWithoutExtension(source); var full = ns + "." + className;
            if (!originalTypes.Contains(full) || !targetTypes.Contains(full)) continue;
            var oldGuid = ReadGuid(File.ReadAllText(meta)); if (oldGuid is null) continue;
            var target = new BindingTarget(assemblyName, ns, className, assembly.Guid, Md4.UnityFileId(ns, className));
            var key = (11500000, oldGuid);
            if (bindings.TryGetValue(key, out var previous) && previous != target)
                throw new InvalidDataException("Built-in UGUI script GUID conflicts with another recovered script mapping: " + oldGuid);
            bindings[key] = target; rows.Add(new { source_guid = oldGuid, source_path = source, assembly = target.Assembly, @namespace = ns, class_name = className, guid = target.Guid, fileID = target.FileId });
            if (ns == "UnityEngine.UI") bindings[(target.FileId, "f5f67c52d1564df4a8936ccd202a3bd8")] = target;
        }
    }

    private List<RecoveredType> CollectRecoveredTypes(string assets)
    {
        var result = new List<RecoveredType>();
        foreach (var source in SafeFiles.Files(assets).Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = Path.GetRelativePath(assets, source).Replace('\\', '/'); var sourceAssembly = relative.StartsWith("Scripts/Assembly-CSharp/", StringComparison.OrdinalIgnoreCase) ? "Assembly-CSharp" : relative.StartsWith("Plugins/Assembly-CSharp-firstpass/", StringComparison.OrdinalIgnoreCase) ? "Assembly-CSharp-firstpass" : relative.StartsWith("Scripts/Assembly-UnityScript/", StringComparison.OrdinalIgnoreCase) ? "Assembly-UnityScript" : "";
            if (sourceAssembly.Length == 0) continue;
            var content = File.ReadAllText(source, Encoding.UTF8); var ns = NamespacePattern.Match(content); var namespaceName = ns.Success ? ns.Groups[1].Value : "";
            var meta = source + ".meta"; var guid = File.Exists(meta) ? ReadGuid(File.ReadAllText(meta)) : null;
            var orderMatch = File.Exists(meta) ? Regex.Match(File.ReadAllText(meta), @"(?m)^\s*executionOrder:\s*(-?\d+)") : Match.Empty;
            var execution = orderMatch.Success && int.TryParse(orderMatch.Groups[1].Value, out var n) ? n : 0;
            foreach (Match type in TypePattern.Matches(content)) result.Add(new(namespaceName, type.Groups[1].Value, guid ?? "", relative, sourceAssembly, execution));
        }
        // The serialized source file's MonoScript refers to its single principal class. Avoid treating helper classes in the same file as identities.
        return result.GroupBy(x => (x.Relative, x.Namespace, x.Guid)).Select(g =>
        {
            var stem = Path.GetFileNameWithoutExtension(g.First().Relative);
            return g.FirstOrDefault(x => x.Class == stem) ?? g.OrderBy(x => x.Class, StringComparer.Ordinal).First();
        }).ToList();
    }

    private static Dictionary<string, HashSet<string>> ReadTypeMetadata(string directory)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SafeFiles.Files(directory).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            using var assembly = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { InMemory = true });
            var types = new HashSet<string>(StringComparer.Ordinal);
            void Add(TypeDefinition type) { types.Add(type.FullName.Replace('/', '+')); foreach (var nested in type.NestedTypes) Add(nested); }
            foreach (var type in assembly.MainModule.Types) Add(type);
            result[Path.GetFileName(file)] = types;
        }
        return result;
    }

    private Dictionary<string, int> ReadExecutionOrders(string assemblyFile)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var assembly = AssemblyDefinition.ReadAssembly(assemblyFile, new ReaderParameters { InMemory = true }))
            foreach (var type in assembly.MainModule.Types.SelectMany(Flatten)) names.Add(type.FullName.Replace('/', '+'));
        var result = new Dictionary<string, int>();
        foreach (var recovered in CollectRecoveredTypes(Path.Combine(candidate, "Assets")))
        {
            var full = (recovered.Namespace.Length == 0 ? "" : recovered.Namespace + ".") + recovered.Class;
            if (recovered.ExecutionOrder != 0 && names.Contains(full)) result[full] = recovered.ExecutionOrder;
        }
        return result;
    }
    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) { yield return type; foreach (var n in type.NestedTypes) foreach (var x in Flatten(n)) yield return x; }

    private string ApplyShaderOverlay(string root, string assets)
    {
        if (config.ShaderSources is null) return "not configured";
        var overlayRoot = Path.GetFullPath(config.ShaderSources); if (!Directory.Exists(overlayRoot)) throw new DirectoryNotFoundException("ShaderSources directory does not exist.");
        SafeFiles.RequireSeparate(overlayRoot, root); RejectLinks(overlayRoot);
        var sourceAssets = Directory.Exists(Path.Combine(overlayRoot, "Assets")) ? Path.Combine(overlayRoot, "Assets") : overlayRoot;
        var shaders = SafeFiles.Files(sourceAssets).Where(p => p.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (shaders.Length == 0) throw new InvalidDataException("ShaderSources must contain a local Assets-shaped overlay with shader files.");
        foreach (var source in shaders)
        {
            var bytes = File.ReadAllBytes(source); var content = Encoding.UTF8.GetString(bytes);
            if (content.Contains("DummyShaderTextExporter", StringComparison.OrdinalIgnoreCase) || content.Contains("AssetRipperDummyShader", StringComparison.OrdinalIgnoreCase) || content.Contains("H3VR_CONVERTER_DUMMY", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Dummy shader source is not accepted: " + Path.GetRelativePath(sourceAssets, source));
            var relative = Path.GetRelativePath(sourceAssets, source); var destination = SafeFiles.Inside(assets, relative.Replace(Path.DirectorySeparatorChar, '/'));
            var sourceMeta = source + ".meta"; if (!File.Exists(sourceMeta)) throw new InvalidDataException("Shader overlay is missing its original .meta GUID: " + relative);
            var guid = ReadGuid(File.ReadAllText(sourceMeta)) ?? throw new InvalidDataException("Shader metadata has no GUID: " + relative);
            var targetMeta = destination + ".meta";
            if (File.Exists(targetMeta))
            {
                var targetGuid = ReadGuid(File.ReadAllText(targetMeta));
                if (targetGuid is not null && targetGuid != guid) throw new InvalidDataException("Shader overlay GUID differs from recovered asset GUID: " + relative);
            }
            var sourceHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (File.Exists(destination)) File.Delete(destination);
            SafeFiles.Copy(source, destination);
            if (!SafeFiles.Hash(destination).Equals(sourceHash, StringComparison.Ordinal)) throw new IOException("Shader overlay changed while copying: " + relative);
            // Existing recovered metadata also carries the source AssetBundle group; keep it intact when its GUID matches.
            if (!File.Exists(targetMeta)) File.Copy(sourceMeta, targetMeta);
        }
        var includes = SafeFiles.Files(sourceAssets).Where(p => new[] { ".cginc", ".hlsl", ".compute", ".glslinc" }.Contains(Path.GetExtension(p).ToLowerInvariant())).ToArray();
        foreach (var source in includes)
        {
            var relative = Path.GetRelativePath(sourceAssets, source).Replace('\\', '/');
            var destination = SafeFiles.Inside(assets, relative);
            if (File.Exists(destination)) File.Delete(destination);
            SafeFiles.Copy(source, destination);
            if (!File.Exists(destination + ".meta") && File.Exists(source + ".meta")) File.Copy(source + ".meta", destination + ".meta");
        }
        foreach (var source in shaders.Concat(includes))
        {
            var relative = Path.GetRelativePath(sourceAssets, source);
            var destination = SafeFiles.Inside(assets, relative.Replace('\\', '/'));
            foreach (Match include in Regex.Matches(File.ReadAllText(destination), "(?m)^[ \t]*#include(?:_with_pragmas)?\\s*[<\"](Assets/[^>\"]+)[>\"]"))
                if (!File.Exists(SafeFiles.Inside(root, include.Groups[1].Value))) throw new InvalidDataException("Shader overlay is missing an include: " + include.Groups[1].Value);
        }
        return $"{shaders.Length} source shaders copied with matching metadata and {includes.Length} include/compute files";
    }

    private static void RejectDummyShaders(string assets)
    {
        foreach (var shader in SafeFiles.Files(assets).Where(p => p.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(shader);
            if (text.Contains("DummyShaderTextExporter", StringComparison.OrdinalIgnoreCase) || text.Contains("AssetRipperDummyShader", StringComparison.OrdinalIgnoreCase) || text.Contains("H3VR_CONVERTER_DUMMY", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Project still contains a dummy shader; preparation cannot be recorded as successful: " + Path.GetRelativePath(assets, shader));
        }
    }

    private static string CaptureBundleMembership(string root, string assets, string archive)
    {
        var groups = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var field = new Regex(@"(?m)^  assetBundleName: (\S.*)$", RegexOptions.Compiled);
        foreach (var meta in SafeFiles.Files(assets).Where(p => p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
        {
            var match = field.Match(File.ReadAllText(meta)); if (!match.Success) continue;
            var asset = Path.GetRelativePath(root, meta[..^5]).Replace(Path.DirectorySeparatorChar, '/');
            if (!File.Exists(Path.Combine(root, asset.Replace('/', Path.DirectorySeparatorChar)))) continue;
            if (!groups.TryGetValue(match.Groups[1].Value.Trim(), out var paths)) groups[match.Groups[1].Value.Trim()] = paths = [];
            paths.Add(asset);
        }
        var relative = "NativeH3VR-RecoveredSources/asset-bundle-membership.json";
        var output = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { schemaVersion = 1, source = "preserved Unity .meta assetBundleName fields", addresses = (string?)null, bundles = groups.ToDictionary(p => p.Key, p => p.Value.Order(StringComparer.Ordinal).ToArray()) }, ConversionConfig.Json) + "\n");
        return relative;
    }

    private static void ConfigureOpenXr(string root)
    {
        var path = Path.Combine(root, "Packages/manifest.json"); JsonObject manifest;
        if (File.Exists(path)) manifest = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("Packages/manifest.json is not a JSON object.");
        else manifest = new JsonObject();
        var dependencies = manifest["dependencies"] as JsonObject ?? new JsonObject(); manifest["dependencies"] = dependencies;
        dependencies["com.unity.inputsystem"] = "1.6.3";
        dependencies["com.unity.xr.openxr"] = "1.13.0";
        dependencies["com.unity.xr.management"] = "4.4.0";
        dependencies["com.unity.xr.core-utils"] = "2.1.1";
        dependencies["com.unity.xr.legacyinputhelpers"] = "2.1.7";
        // OpenXR is selected through the project settings; do not enable a specific headset/provider.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        var playerSettings = Path.Combine(root, "ProjectSettings/ProjectSettings.asset");
        if (!File.Exists(playerSettings)) throw new FileNotFoundException("Recovered PlayerSettings are required to configure both Unity input backends.", playerSettings);
        var playerText = File.ReadAllText(playerSettings);
        var activeInput = Regex.Match(playerText, @"(?m)^  activeInputHandler:\s*\d+\s*$");
        playerText = activeInput.Success
            ? Regex.Replace(playerText, @"(?m)^  activeInputHandler:\s*\d+\s*$", "  activeInputHandler: 2", RegexOptions.None)
            : Regex.Replace(playerText, @"(?m)^(PlayerSettings:\s*\r?\n)", "$1  activeInputHandler: 2\n", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!playerText.Contains("  activeInputHandler: 2", StringComparison.Ordinal)) throw new InvalidDataException("Could not configure the Unity input system selection.");
        File.WriteAllText(playerSettings, playerText);
        var settings = Path.Combine(root, "ProjectSettings/XRPackageSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{\n  \"settings\": {\n    \"com.unity.xr.management\": {\n      \"activeLoaders\": [],\n      \"initializeOnStart\": true\n    },\n    \"com.unity.xr.openxr\": {\n      \"enabled\": true\n    }\n  }\n}\n");
    }
    private static void PinProjectVersion(string root)
    {
        var file = Path.Combine(root, "ProjectSettings/ProjectVersion.txt"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "m_EditorVersion: " + ConversionConfig.TargetVersion + "\nm_EditorVersionWithRevision: " + ConversionConfig.TargetVersion + " (local migration target)\n");
    }
    private static void RemoveUguiDuplicate(string root)
    {
        var file = Path.Combine(root, "Packages/manifest.json"); if (!File.Exists(file)) return;
        var node = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; if (node?["dependencies"] is JsonObject dependencies) dependencies.Remove("com.unity.ugui");
        File.WriteAllText(file, (node ?? new JsonObject()).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
    private static void WritePluginMeta(string path, string guid, Dictionary<string, int> orders)
    {
        var orderText = orders.Count == 0 ? "  executionOrder: {}\n" : "  executionOrder:\n" + string.Join("", orders.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"    {p.Key}: {p.Value}\n"));
        File.WriteAllText(path, $"fileFormatVersion: 2\nguid: {guid}\nPluginImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  iconMap: {{}}\n{orderText}  defineConstraints: []\n  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n  validateReferences: 1\n  platformData:\n  - first:\n      Any:\n    second:\n      enabled: 1\n      settings: {{}}\n  userData: NativeH3VR local managed migration\n  assetBundleName:\n  assetBundleVariant:\n");
    }
    private static string? ReadGuid(string text) => GuidPattern.Match(text) is { Success: true } m ? m.Groups[1].Value : null;
    private static string DeterministicGuid(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];
    private static bool IsUnitySerializedText(string p) => Path.GetExtension(p).ToLowerInvariant() is ".unity" or ".prefab" or ".asset" or ".controller" or ".overridecontroller";
    private static void RejectLinks(string path) { if (File.Exists(path) || Directory.Exists(path)) _ = SafeFiles.Files(path); }
    private static void EnsureCandidatePath(string root, string path) { if (!SafeFiles.Contains(root, path)) throw new InvalidDataException("Migration target escapes candidate: " + path); }
    private static void MoveCandidatePath(string root, string relative, string archive)
    {
        var source = SafeFiles.Inside(root, relative); if (!Directory.Exists(source) && !File.Exists(source)) return;
        var destination = Path.Combine(archive, Path.GetFileName(source)); if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Recoverable source archive destination already exists: " + destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
        var meta = source + ".meta"; if (File.Exists(meta)) File.Move(meta, destination + ".meta");
    }
}
