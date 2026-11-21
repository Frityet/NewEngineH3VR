namespace H3VR.Converter;

public sealed partial class Pipeline
{
    private static void AddExtractionSources(string repository, Action<string> add)
    {
        add(AssetRipperIntegration.Fingerprint(repository));
        foreach (var relative in new[]
        {
            "src/H3VR.Converter/Extraction.cs", "src/H3VR.Converter/AssetRipperIntegration.cs",
            "src/H3VR.Converter/SafeFiles.cs",
            "src/H3VR.Converter/H3VR.Converter.csproj",
            ".gitmodules"
        })
        {
            var file = Path.Combine(repository, relative);
            add(relative);
            add(File.Exists(file) ? SafeFiles.Hash(file) : "missing");
        }
    }

    private async Task<List<string>> Extract(CancellationToken cancellation)
    {
        if (config.RecoveredProject is null)
        {
            var recovery = Path.Combine(config.Workspace, "recovery");
            if (Directory.Exists(recovery))
                throw new IOException("An incomplete recovery exists. Inspect it and choose a fresh workspace before rerunning extraction.");
            var candidate = Path.Combine(config.Workspace, "recovery-candidate-" + Guid.NewGuid().ToString("N"));
            try
            {
                await AssetRipperIntegration.ExportAsync(repository, config, candidate,
                    Path.Combine(Receipts, "assetripper.log"), cancellation);
                if (!File.Exists(Path.Combine(candidate, "ExportedProject/ProjectSettings/ProjectVersion.txt")) ||
                    !Directory.Exists(Path.Combine(candidate, "ExportedProject/Assets")))
                    throw new InvalidDataException("AssetRipper did not produce an ExportedProject. Inspect .conversion/assetripper.log.");
                Directory.Move(candidate, recovery);
            }
            finally
            {
                if (Directory.Exists(candidate)) Directory.Delete(candidate, true);
            }
        }

        var version = Path.Combine(config.Export, "ProjectSettings/ProjectVersion.txt");
        if (!File.Exists(version) || !Directory.Exists(Path.Combine(config.Export, "Assets")))
            throw new InvalidDataException("Recovery must contain ExportedProject/Assets and ProjectSettings/ProjectVersion.txt.");
        // External recovery bytes are already included in the input fingerprint.
        if (config.RecoveredProject is not null) return [version];
        return ColdRecoveryOutputFiles(Path.Combine(config.Workspace, "recovery"));
    }

    internal static List<string> ColdRecoveryOutputFiles(string recovery)
    {
        var identityMap = Path.Combine(recovery, "AuxiliaryFiles", "shader-identity-v2.json");
        var pathMap = Path.Combine(recovery, "AuxiliaryFiles", "path_id_map.json");
        if (!File.Exists(identityMap) || !File.Exists(pathMap))
            throw new InvalidDataException("Cold extraction did not produce its required provenance maps.");
        return SafeFiles.Files(Path.Combine(recovery, "ExportedProject")).Concat([identityMap, pathMap]).ToList();
    }
}
