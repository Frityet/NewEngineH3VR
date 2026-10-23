using H3VR.Converter;
using System.Text.Json;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("""
    H3VR → Unity 2021.3.45f2

    dotnet run --project src/H3VR.Converter -- init <config.json>
    dotnet run --project src/H3VR.Converter -- doctor <config.json>
    dotnet run --project src/H3VR.Converter -- convert <config.json> [--through extract|managed|project|validate|build]
    dotnet run --project src/H3VR.Converter -- status <config.json>
    dotnet run --project src/H3VR.Converter -- check-publication

    Conversion reads a locally installed, licensed copy of H3VR. It never modifies
    the Steam installation, publishes game assets, or starts a headset runtime.
    Re-run convert to resume verified completed steps. Default endpoint: validate.
    """);
    return 0;
}
using var interrupt = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; interrupt.Cancel(); };
try
{
    var repository = Repository.Find();
    if (args[0] == "check-publication") return Publication.Check(repository);
    if (args.Length < 2) throw new ArgumentException("Specify the configuration filename. Use --help for commands.");
    if (args[0] == "init")
    {
        if (File.Exists(args[1])) throw new IOException("Configuration already exists; refusing to overwrite it.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var game = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library/Application Support/Steam/steamapps/common/H3VR")
            : OperatingSystem.IsWindows() ? @"C:\Program Files (x86)\Steam\steamapps\common\H3VR" : Path.Combine(home, ".steam/steam/steamapps/common/H3VR");
        var unity = OperatingSystem.IsMacOS() ? "/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity"
            : OperatingSystem.IsWindows() ? @"C:\Program Files\Unity\Hub\Editor\2021.3.45f2\Editor\Unity.exe" : Path.Combine(home, "Unity/Hub/Editor/2021.3.45f2/Editor/Unity");
        SafeFiles.AtomicJson(Path.GetFullPath(args[1]), new ConversionConfig { GameDirectory = game, UnityEditor = unity, Workspace = Path.Combine(home, "H3VR-Unity2021"), AssetRipperUrl = "http://127.0.0.1:58947/" });
        Console.WriteLine("Created configuration. Set the local game/editor paths and either RecoveredProject or AssetRipperUrl.");
        return 0;
    }
    var config = ConversionConfig.Load(args[1]);
    if (args[0] == "status") { Pipeline.Status(config); return 0; }
    config.Validate(repository); config.VerifyEditorVersion();
    if (args[0] == "doctor")
    {
        Console.WriteLine($"Game: {config.GameDirectory}\nUnity: {config.UnityEditor}\nPinned target: {ConversionConfig.TargetVersion}\nWorkspace: {config.Workspace}\nBuild target: {config.BuildTarget}");
        Console.WriteLine("Inputs and path boundaries passed. Unity's version is also asserted by the editor validation step.");
        if (config.ShaderSources is null) Console.WriteLine("ShaderSources is unset. Dummy shader exports will block project preparation until locally recovered sources are supplied.");
        return 0;
    }
    if (args[0] != "convert") throw new ArgumentException("Unknown command: " + args[0]);
    var through = args.Length == 4 && args[2] == "--through" ? args[3] : args.Length == 2 ? "validate" : throw new ArgumentException("Expected --through <step>.");
    await new Pipeline(repository, config).Run(through, interrupt.Token);
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled. Completed steps remain available for resume."); return 130; }
catch (Exception error) { Console.Error.WriteLine("Conversion stopped: " + error.Message); return 1; }
