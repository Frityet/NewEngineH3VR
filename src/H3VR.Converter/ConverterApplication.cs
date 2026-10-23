using Spectre.Console;

namespace H3VR.Converter;

public sealed class ConverterApplication(string repository, IAnsiConsole console)
{
    public async Task<int> RunAsync(CommandRequest request, CancellationToken cancellation)
    {
        if (request.Command == ConverterCommand.CheckPublication) return Publication.Check(repository);
        var options = request.Options;
        if (options.Tui && request.Command != ConverterCommand.Convert)
            throw new ArgumentException("Use --tui with convert (the default command). Other commands use flags directly.");
        if (options.Output is not null && request.Command != ConverterCommand.RecoverShaders)
            throw new ArgumentException("--output is for recover-shaders. Conversion writes into --workspace.");
        var loaded = request.Command != ConverterCommand.Init && options.ConfigFile is not null
            ? ConversionConfig.Load(Path.GetFullPath(options.ConfigFile)) : new ConversionConfig();
        var config = ApplyOptions(loaded, options, Environment.CurrentDirectory);
        var through = request.Through.ToLowerInvariant();
        if (!Pipeline.Steps.Contains(through)) throw new ArgumentException("--through must be extract, managed, project, validate, or build.");
        // Discovery is bounded to known Steam/Unity locations and never launches an editor.
        var needsGame = request.Command is not ConverterCommand.Status;
        var needsEditor = request.Command == ConverterCommand.Init ||
            (request.Command is ConverterCommand.Convert or ConverterCommand.Doctor && (options.Tui || through != "extract"));
        var games = needsGame && config.GameDirectory.Length == 0 ? InstallationDiscovery.DiscoverGameDirectories() : [];
        var editors = needsEditor && config.UnityEditor.Length == 0 ? InstallationDiscovery.DiscoverUnityEditors() : [];
        config = WithDefaults(config, games, editors);

        if (options.Tui)
        {
            config = config with
            {
                GameDirectory = await ChooseDiscovered("H3VR installation", config.GameDirectory, games, cancellation),
                UnityEditor = await ChooseDiscovered("Unity editor", config.UnityEditor, editors, cancellation)
            };
            (config, through) = await TerminalWizard.EditAsync(config, through, console, cancellation);
        }

        switch (request.Command)
        {
            case ConverterCommand.Init:
                var file = options.ConfigFile ?? Path.Combine(Environment.CurrentDirectory, "h3vr-conversion.json");
                if (File.Exists(file)) throw new IOException("Configuration already exists; refusing to overwrite it: " + file);
                SafeFiles.AtomicJson(Path.GetFullPath(file), config);
                console.WriteLine("Saved optional configuration: " + Path.GetFullPath(file));
                console.WriteLine("You can also run directly with --game, --unity and --workspace, or --tui.");
                return 0;
            case ConverterCommand.Status:
                ShowStatus(config);
                return 0;
            case ConverterCommand.Doctor:
                return Doctor(config, through, games, editors);
            case ConverterCommand.RecoverShaders:
                return await RecoverShaders(config, options, cancellation);
            case ConverterCommand.Convert:
                config.Validate(repository, through);
                if (through != "extract") config.VerifyEditorVersion();
                ShowConfiguration(config, through);
                if (config.ShaderSources is null && through is not ("extract" or "managed"))
                    console.WriteLine("Shader overlay is unset. Project preparation will stop if the recovery contains dummy shaders. See docs/shader-recovery.md.");
                await new Pipeline(repository, config).Run(through, cancellation);
                console.Write(new Text("Completed through " + through + ". Workspace: " + config.Workspace + "\n", new Style(Color.Green)));
                return 0;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    internal static ConversionConfig ApplyOptions(ConversionConfig config, ConversionOptions options, string directory)
    {
        // Loaded configuration paths are absolute; flag paths resolve against the CWD.
        var result = (config with
        {
            GameDirectory = options.Game ?? config.GameDirectory,
            UnityEditor = options.Unity ?? config.UnityEditor,
            Workspace = options.Workspace ?? config.Workspace,
            ShaderSources = options.Shaders ?? config.ShaderSources,
            RecoveredProject = options.Recovered ?? config.RecoveredProject,
            IntegrationManifest = options.Integration ?? config.IntegrationManifest,
            ToolCacheDirectory = options.Cache ?? config.ToolCacheDirectory,
            Offline = options.Offline ?? config.Offline,
            BuildTarget = options.Target is null ? config.BuildTarget : options.Target.ToLowerInvariant() switch
            {
                "windows" or "standalonewindows64" => "StandaloneWindows64",
                "macos" or "osx" or "standaloneosx" => "StandaloneOSX",
                "linux" or "standalonelinux64" => "StandaloneLinux64",
                _ => throw new ArgumentException("--target must be windows, macos, or linux.")
            }
        }).ResolvePaths(directory);
        if (Directory.Exists(result.UnityEditor)) result = result with { UnityEditor = InstallationDiscovery.NormalizeEditorPath(result.UnityEditor) };
        return result;
    }

    internal static ConversionConfig WithDefaults(ConversionConfig config, IReadOnlyList<string> games, IReadOnlyList<string> editors) => config with
    {
        GameDirectory = config.GameDirectory.Length == 0 && games.Count == 1 ? games[0] : config.GameDirectory,
        UnityEditor = config.UnityEditor.Length == 0 && editors.Count == 1 ? editors[0] : config.UnityEditor,
        Workspace = config.Workspace.Length == 0 ? InstallationDiscovery.DefaultWorkspace() : config.Workspace,
        ToolCacheDirectory = config.ToolCacheDirectory ?? InstallationDiscovery.DefaultToolCache()
    };

    private async Task<string> ChooseDiscovered(string label, string current, IReadOnlyList<string> candidates, CancellationToken cancellation)
    {
        if (current.Length != 0 || candidates.Count < 2) return current;
        return await new SelectionPrompt<string>().Title(label + ":").UseConverter(Markup.Escape)
            .AddChoices(candidates).ShowAsync(console, cancellation);
    }

    private int Doctor(ConversionConfig config, string through, IReadOnlyList<string> games, IReadOnlyList<string> editors)
    {
        ShowConfiguration(config, through);
        var problems = new List<string>();
        if (games.Count > 1 && config.GameDirectory.Length == 0) problems.Add("Multiple H3VR installations were found; select --game or use --tui.");
        if (editors.Count > 1 && config.UnityEditor.Length == 0) problems.Add("Multiple Unity editors were found; select --unity or use --tui.");
        try { config.Validate(repository, through); if (through != "extract") config.VerifyEditorVersion(); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { problems.Add(error.Message); }
        if (config.RecoveredProject is null)
        {
            var available = AssetRipperIntegration.CheckAvailability(config, repository, out var detail);
            console.WriteLine("AssetRipper: " + detail);
            if (!available) problems.Add("AssetRipper is not ready for extraction; resolve the tool issue above.");
        }
        console.WriteLine("Doctor is read-only; it does not download tools, import Unity projects, or verify gameplay.");
        if (config.ShaderSources is null) console.WriteLine("Shader overlay: unset. Dummy exports require locally validated sources before project preparation.");
        foreach (var problem in problems) console.Write(new Text(problem + "\n", new Style(Color.Red)));
        if (problems.Count == 0) console.WriteLine("Input paths and boundaries passed. Unity also verifies its version during project validation.");
        return problems.Count == 0 ? 0 : 1;
    }

    private async Task<int> RecoverShaders(ConversionConfig config, ConversionOptions options, CancellationToken cancellation)
    {
        if (!File.Exists(Path.Combine(config.ManagedInput, "Assembly-CSharp.dll")))
            throw new InvalidDataException("Set --game to your installed H3VR directory.");
        var output = new ConversionConfig { Workspace = options.Output ?? config.Workspace + "-shaders" }.ResolvePaths(Environment.CurrentDirectory).Workspace;
        foreach (var input in new[] { repository, config.GameDirectory, config.ToolCacheDirectory!, config.Workspace })
            SafeFiles.RequireSeparate(input, output);
        foreach (var input in new[] { repository, config.GameDirectory, config.Workspace })
            SafeFiles.RequireSeparate(input, config.ToolCacheDirectory!);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Shader output must be empty: " + output);
        console.WriteLine("Preparing the pinned shader exporter in " + config.ToolCacheDirectory);
        var exporter = await ShaderToolCache.EnsureAsync(config.ToolCacheDirectory!, config.Offline, cancellation);
        var result = await ShaderRecovery.RecoverAsync(new(config.GameData, output, exporter), cancellation);
        console.WriteLine($"Recovered {result.ExportedShaders} shader files to {result.OutputDirectory}.");
        console.WriteLine("These are raw, unvalidated sources. Validate and assemble an identity-matched overlay before using --shaders; see docs/shader-recovery.md.");
        return 0;
    }

    private void ShowConfiguration(ConversionConfig config, string through)
    {
        console.Write(new Rule("H3VR → Unity " + ConversionConfig.TargetVersion).LeftJustified());
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Setting").AddColumn("Value");
        Add("Game", config.GameDirectory.Length == 0 ? "Not found; use --game" : config.GameDirectory);
        Add("Unity", config.UnityEditor.Length == 0 ? "Not found; use --unity" : config.UnityEditor);
        Add("Workspace", config.Workspace);
        Add("Target", config.BuildTarget);
        Add("Recovery", config.RecoveredProject ?? "AssetRipper 2.0.0 (automatic)");
        Add("Shaders", config.ShaderSources ?? "Not set");
        Add("Cache", config.ToolCacheDirectory!);
        Add("Tool provisioning", config.Offline ? "Cached tools only" : "Download and build when needed");
        Add("Through", through);
        console.Write(table);
        void Add(string label, string value) => table.AddRow(new Text(label), new Text(value));
    }

    private void ShowStatus(ConversionConfig config)
    {
        console.WriteLine("Workspace: " + config.Workspace);
        var table = new Table().AddColumn("Stage").AddColumn("Receipt");
        foreach (var step in Pipeline.Steps)
            table.AddRow(new Text(step), new Text(File.Exists(Path.Combine(config.Workspace, ".conversion", step + ".json")) ? "Present; checked before resume" : "Not completed"));
        console.Write(table);
    }
}
