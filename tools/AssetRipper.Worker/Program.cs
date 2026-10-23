using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Configuration;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing.Configuration;

if (args.Length != 2 || args[0] is "--help" or "-h")
{
    Console.Error.WriteLine("Usage: AssetRipper.Worker <game-directory> <output-directory>");
    return args.Length == 1 && args[0] is "--help" or "-h" ? 0 : 2;
}

var gameDirectory = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
if (!Directory.Exists(gameDirectory)) throw new DirectoryNotFoundException("Game directory does not exist: " + gameDirectory);
if (SafeIsWithin(outputDirectory, gameDirectory) || SafeIsWithin(gameDirectory, outputDirectory))
    throw new IOException("AssetRipper output must be outside the installed game directory.");
if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
    throw new IOException("Output directory must be empty; refusing to overwrite existing files: " + outputDirectory);
Directory.CreateDirectory(outputDirectory);

var configuration = new FullConfiguration();
configuration.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level2;
configuration.ImportSettings.StreamingAssetsMode = StreamingAssetsMode.Extract;
configuration.ExportSettings.ScriptExportMode = ScriptExportMode.Hybrid;
configuration.ExportSettings.ShaderExportMode = ShaderExportMode.Dummy;
configuration.ExportSettings.ImageExportFormat = ImageExportFormat.Png;
configuration.ExportSettings.AudioExportFormat = AudioExportFormat.Default;
configuration.ExportSettings.ExportUnreadableAssets = true;
configuration.ExportSettings.PreferOriginalTextureExtension = true;
configuration.ProcessingSettings.BundledAssetsExportMode = BundledAssetsExportMode.DirectExport;

var exporter = new ExportHandler(configuration);
Logger.Add(new ConsoleLogger());
exporter.LoadProcessAndExport([gameDirectory], outputDirectory, LocalFileSystem.Instance);
if (!Directory.Exists(Path.Combine(outputDirectory, "ExportedProject", "Assets")))
    throw new InvalidDataException("AssetRipper finished without producing ExportedProject/Assets.");
if (!File.Exists(Path.Combine(outputDirectory, "AuxiliaryFiles", "path_id_map.json")))
    throw new InvalidDataException("AssetRipper finished without producing AuxiliaryFiles/path_id_map.json; bundle address resolution cannot continue.");
return 0;

static bool SafeIsWithin(string candidate, string parent)
{
    var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
    return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
}
