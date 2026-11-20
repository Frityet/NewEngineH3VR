using Mono.Cecil;

namespace H3VR.Converter.Tests;

internal static class StockShaderWarmupTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("stock shader warmup preserves other preloads and requires a retained game reference", PreserveOtherPreloads);
    }

    private static void PreserveOtherPreloads()
    {
        const string stockGuid = "2948aa4c29af30e40bed49f54993d507";
        const string stock = "{fileID: 20000000, guid: " + stockGuid + ", type: 2}";
        const string other = "{fileID: 20000000, guid: 11111111111111111111111111111111, type: 2}";
        var root = Directory.CreateTempSubdirectory("h3vr-stock-warmup-");
        try
        {
            const string heading = "GraphicsSettings:\r\n  m_PreloadedShaders:\r\n";
            const string setting = "  m_CustomRenderPipeline: {fileID: 0}\r\n";
            string original = heading + "  - " + other + "\r\n  - " + stock + "\r\n" + setting;
            string retained = heading + "  - " + other + "\r\n" + setting;
            const string asset = "Assets/ShaderVariantCollection/H3_ShaderAsset3.shadervariants";
            const string manager = "Assets/Resources/prefabs/_managers/_GameManager.prefab";
            const string graphics = "ProjectSettings/GraphicsSettings.asset";
            Write(asset, "--- !u!200 &20000000\r\nShaderVariantCollection:\r\n  m_Name: H3_ShaderAsset3\r\n");
            Write(asset + ".meta", "guid: " + stockGuid + "\r\n");
            Write(manager, "MonoBehaviour:\r\n  ShaderVariants: " + stock + "\r\n");
            Write(graphics, original);
            if (!StockShaderWarmupProject.TryPrepare(root.FullName, out string update) || update != retained)
                throw new InvalidOperationException("Removing the known pointer changed unrelated preloaded collections or settings.");

            string dll = Path.Combine(root.FullName, "NativeH3VR.Game.dll");
            using (var unpatched = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("NativeH3VR.Game", new Version(1, 0)), "NativeH3VR.Game", ModuleKind.Dll))
                unpatched.Write(dll);
            if (StockShaderWarmupProject.Apply(root.FullName, dll) || File.ReadAllText(Path.Combine(root.FullName, graphics)) != original)
                throw new InvalidOperationException("A project without the verified managed queue lost its preload.");

            const string alternateGuid = "fefefefefefefefefefefefefefefefe";
            Write(asset + ".meta", "guid: " + alternateGuid + "\r\n");
            Write(manager, "MonoBehaviour:\r\n  ShaderVariants: " + stock.Replace(stockGuid, alternateGuid) + "\r\n");
            Write(graphics, original.Replace(stockGuid, alternateGuid));
            if (!StockShaderWarmupProject.TryPrepare(root.FullName, out update) || update != retained)
                throw new InvalidOperationException("An equivalent export with a new GUID did not qualify.");

            Write(asset, "--- !u!200 &20000000\r\nShaderVariantCollection:\r\n  m_Name: H3_ShaderAsset30\r\n");
            if (StockShaderWarmupProject.TryPrepare(root.FullName, out _))
                throw new InvalidOperationException("A similarly named collection qualified for preload removal.");
            Write(asset, "--- !u!200 &20000000\r\nShaderVariantCollection:\r\n  m_Name: H3_ShaderAsset3\r\n");

            Write(manager, "MonoBehaviour:\r\n  ShaderVariants: " + other + "\r\n");
            if (StockShaderWarmupProject.TryPrepare(root.FullName, out _))
                throw new InvalidOperationException("An unretained stock collection qualified for preload removal.");

            void Write(string name, string data)
            {
                string file = Path.Combine(root.FullName, name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, data);
            }
        }
        finally { root.Delete(recursive: true); }
    }
}
