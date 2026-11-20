using Mono.Cecil;

namespace H3VR.Converter.Tests;

internal static class StockMaterialColorTests
{
    const string ImportedName = "NativeH3VR.Game";
    static readonly Guid StockMvid = new("6d320100-4b8c-4da8-8a19-b53d3475e3c7");

    internal static IEnumerable<(string, Action)> Cases()
    {
        yield return ("stock HDR setters require the exact Steam source and a Linear import", () =>
        {
            using var wrongRevision = Assembly(ImportedName, Guid.NewGuid());
            using var other = Assembly("External.Mod", StockMvid);
            using var stock = Assembly(ImportedName, StockMvid);
            Require(LegacyStockMaterialColors.Apply(wrongRevision.MainModule, true) == 0);
            Require(LegacyStockMaterialColors.Apply(other.MainModule, true) == 0);
            Require(LegacyStockMaterialColors.Apply(stock.MainModule, false) == 0);
        }
        );

        yield return ("the stock HDR setter importer rejects a missing original call before changing the assembly", () =>
        {
            using var stock = Assembly(ImportedName, StockMvid);
            var reticle = new TypeDefinition("FistVR", "TAH_Reticle", TypeAttributes.Class,
                stock.MainModule.TypeSystem.Object);
            stock.MainModule.Types.Add(reticle);
            try
            {
                LegacyStockMaterialColors.Apply(stock.MainModule, true);
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("original stock material setter"))
            {
                Require(!reticle.HasMethods);
                return;
            }
            throw new InvalidOperationException("An unexpected stock setter was accepted.");
        }
        );
    }

    static AssemblyDefinition Assembly(string name, Guid mvid)
    {
        var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)),
            name + ".dll", ModuleKind.Dll);
        assembly.MainModule.Mvid = mvid;
        return assembly;
    }

    static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Unexpected stock HDR setter provenance or patch state.");
    }
}
