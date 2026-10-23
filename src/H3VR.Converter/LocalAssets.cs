using System.Buffers.Binary;
using System.Text;

namespace H3VR.Converter;

/// <summary>Copies dependencies from the user's installation into their private candidate.</summary>
public static class LocalAssets
{
    public static void Stage(ConversionConfig config, string project)
    {
        var assets = Path.Combine(project, "Assets");
        var streaming = Path.Combine(config.GameData, "StreamingAssets");
        if (!Directory.Exists(streaming)) throw new DirectoryNotFoundException("Original StreamingAssets are required.");
        foreach (var source in SafeFiles.Files(streaming))
        {
            // Unity 5 bundles are rebuilt by BundleConversion with their original addresses.
            if (IsBundle(source) || source.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) continue;
            CopyIdenticalOrNew(source, SafeFiles.Inside(assets, "StreamingAssets/" + Path.GetRelativePath(streaming, source).Replace('\\', '/')));
        }
        if (config.BuildTarget != "StandaloneWindows64") return;
        var plugins = Path.Combine(config.GameData, "Plugins");
        var native = Directory.Exists(plugins) ? SafeFiles.Files(plugins).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) : [];
        var steam = Path.Combine(config.GameDirectory, "steam_api64.dll");
        if (File.Exists(steam)) native = native.Append(steam);
        Span<byte> header = stackalloc byte[64];
        Span<byte> pe = stackalloc byte[6];
        foreach (var source in native)
        {
            if (Path.GetFileName(source).Equals("openvr_api.dll", StringComparison.OrdinalIgnoreCase)) continue;
            using (var file = File.OpenRead(source))
            {
                // These are native Windows plugins, never editor scripts or game assemblies.
                file.ReadExactly(header);
                if (header[0] != 'M' || header[1] != 'Z') throw new InvalidDataException("Not a Windows plugin: " + source);
                file.Position = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
                file.ReadExactly(pe);
                if (!pe[..4].SequenceEqual("PE\0\0"u8) || BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) != 0x8664)
                    throw new InvalidDataException("Windows64 conversion requires x64 native plugins: " + source);
            }
            var destination = Path.Combine(assets, "Plugins", "x86_64", Path.GetFileName(source));
            CopyIdenticalOrNew(source, destination);
            var guid = SafeFiles.HashText("H3VR.LocalNative:" + Path.GetFileName(source))[..32];
            File.WriteAllText(destination + ".meta", $$"""
                fileFormatVersion: 2
                guid: {{guid}}
                PluginImporter:
                  externalObjects: {}
                  serializedVersion: 2
                  iconMap: {}
                  executionOrder: {}
                  defineConstraints: []
                  isPreloaded: 0
                  isOverridable: 0
                  isExplicitlyReferenced: 0
                  validateReferences: 1
                  platformData:
                  - first:
                      Any:
                    second:
                      enabled: 0
                      settings: {}
                  - first:
                      Editor: Editor
                    second:
                      enabled: 0
                      settings:
                        DefaultValueInitialized: true
                  - first:
                      Standalone: Win64
                    second:
                      enabled: 1
                      settings:
                        CPU: x86_64
                        OS: Windows
                  userData: Copied locally from the installed game
                  assetBundleName:
                  assetBundleVariant:
                """ + "\n", new UTF8Encoding(false));
        }
    }

    public static bool IsBundle(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        var count = stream.Read(header);
        return count >= 7 && (header[..7].SequenceEqual("UnityFS"u8) || header[..7].SequenceEqual("UnityRa"u8) || header[..7].SequenceEqual("UnityWe"u8));
    }

    private static void CopyIdenticalOrNew(string source, string destination)
    {
        if (File.Exists(destination))
        {
            if (SafeFiles.Hash(source) != SafeFiles.Hash(destination)) throw new IOException("Recovered dependency differs from the installation: " + destination);
            return;
        }
        SafeFiles.Copy(source, destination);
    }
}
