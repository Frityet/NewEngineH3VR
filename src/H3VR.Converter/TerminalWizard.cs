using Spectre.Console;

namespace H3VR.Converter;

public static class TerminalWizard
{
    private static readonly string[] Stages = { "extract", "managed", "project", "validate", "build" };
    private static readonly string[] Targets = { "windows", "macos", "linux" };

    public static (ConversionConfig Config, string Through) Edit(
        ConversionConfig config,
        string through,
        IAnsiConsole? console = null,
        CancellationToken cancellationToken = default) =>
        EditAsync(config, through, console, cancellationToken).GetAwaiter().GetResult();

    public static async Task<(ConversionConfig Config, string Through)> EditAsync(
        ConversionConfig config,
        string through,
        IAnsiConsole? console = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var ui = console ?? AnsiConsole.Console;
        if (!ui.Profile.Capabilities.Interactive)
            throw new InvalidOperationException("The configuration wizard needs an interactive terminal. Use command-line flags instead.");
        if (!ui.Profile.Capabilities.Ansi)
            throw new InvalidOperationException("The configuration wizard needs ANSI terminal cursor support. Use a terminal with ANSI support or command-line flags.");

        try
        {
            while (true)
            {
                var draft = config with
                {
                    GameDirectory = await RequiredPath(ui, "Installed H3VR folder", config.GameDirectory,
                        IsGameDirectory, "Choose the folder containing h3vr_Data/Managed/Assembly-CSharp.dll.", cancellationToken),
                    UnityEditor = await RequiredUnityPath(ui, config.UnityEditor, cancellationToken),
                    Workspace = await RequiredPath(ui, "Writable conversion workspace", config.Workspace,
                        IsValidPath, "Enter a valid workspace directory path.", cancellationToken),
                    ShaderSources = await OptionalPath(ui, "Optional recovered shader overlay", config.ShaderSources,
                        Directory.Exists, "That shader overlay directory does not exist.", cancellationToken)
                };

                string target = await Select(ui, "Build target", Targets,
                    TargetName(draft.BuildTarget), cancellationToken);
                draft = draft with { BuildTarget = BuildTargetName(target) };
                string selectedStage = await Select(ui, "Stop after", Stages,
                    Stages.Contains(through, StringComparer.OrdinalIgnoreCase) ? through.ToLowerInvariant() : "validate",
                    cancellationToken);

                bool advanced = await Select(ui, "Edit advanced recovery and runtime settings?",
                    new[] { "No", "Yes" }, "No", cancellationToken) == "Yes";
                if (advanced)
                {
                    draft = draft with
                    {
                        RecoveredProject = await OptionalPath(ui, "Optional recovered Unity project", draft.RecoveredProject,
                            path => Directory.Exists(Path.Combine(path, "Assets")),
                            "Choose a project folder containing Assets, or leave blank.", cancellationToken),
                        IntegrationManifest = await OptionalPath(ui, "Optional native integration manifest", draft.IntegrationManifest,
                            File.Exists, "Choose an existing integration manifest, or leave blank.", cancellationToken),
                        ToolCacheDirectory = await OptionalPath(ui, "Optional tool cache directory", draft.ToolCacheDirectory,
                            IsValidPath, "Enter a valid cache directory path, or leave blank.", cancellationToken)
                    };
                    bool offline = await Select(ui, "Offline mode", new[] { "Off", "On" },
                        draft.Offline ? "On" : "Off", cancellationToken) == "On";
                    draft = draft with { Offline = offline };
                }

                ShowReview(ui, draft, selectedStage);
                string action = await Select(ui, "Continue", new[] { "Run", "Edit", "Cancel" },
                    "Run", cancellationToken);
                if (action == "Run") return (draft, selectedStage);
                if (action == "Cancel") throw new OperationCanceledException("Configuration wizard cancelled.", cancellationToken);
                config = draft;
                through = selectedStage;
            }
        }
        catch (EndOfStreamException error)
        {
            throw new OperationCanceledException("Terminal input ended before configuration was confirmed.", error, cancellationToken);
        }
        catch (IOException error) when (!ui.Profile.Capabilities.Interactive)
        {
            throw new OperationCanceledException("Terminal input is unavailable; configuration wizard cancelled.", error, cancellationToken);
        }
    }

    private static async Task<string> RequiredPath(IAnsiConsole console, string title, string? current,
        Func<string, bool> validate, string failure, CancellationToken cancellationToken)
    {
        var prompt = new TextPrompt<string>(title + ":")
            .Validate(value => SafeValidate(validate, value) ? ValidationResult.Success() : ValidationResult.Error(failure));
        if (!string.IsNullOrWhiteSpace(current)) prompt.DefaultValue(current).ShowDefaultValue().EditableDefaultValue(true);
        string value = await prompt.ShowAsync(console, cancellationToken).ConfigureAwait(false);
        return FullPath(value);
    }

    private static async Task<string> RequiredUnityPath(IAnsiConsole console, string? current,
        CancellationToken cancellationToken)
    {
        var prompt = new TextPrompt<string>("Unity 2021.3.45f2 editor executable or installation folder:")
            .Validate(value => TryNormalizeUnity(value, out _)
                ? ValidationResult.Success()
                : ValidationResult.Error("Choose a Unity editor folder or Unity/Unity.exe executable."));
        if (!string.IsNullOrWhiteSpace(current)) prompt.DefaultValue(current).ShowDefaultValue().EditableDefaultValue(true);
        string value = await prompt.ShowAsync(console, cancellationToken).ConfigureAwait(false);
        return InstallationDiscovery.NormalizeEditorPath(ExpandPath(value));
    }

    private static async Task<string?> OptionalPath(IAnsiConsole console, string title, string? current,
        Func<string, bool> validate, string failure, CancellationToken cancellationToken)
    {
        var prompt = new TextPrompt<string>(title + " (blank to skip):")
            .AllowEmpty()
            .Validate(value => value.Length == 0 || SafeValidate(validate, value)
                ? ValidationResult.Success() : ValidationResult.Error(failure));
        prompt.DefaultValue(current ?? string.Empty).ShowDefaultValue().EditableDefaultValue(true);
        string value = (await prompt.ShowAsync(console, cancellationToken).ConfigureAwait(false)).Trim();
        return value.Length == 0 ? null : FullPath(value);
    }

    private static async Task<string> Select(IAnsiConsole console, string title,
        IReadOnlyList<string> choices, string current, CancellationToken cancellationToken)
    {
        var prompt = new SelectionPrompt<string>()
            .Title(title + ":")
            .PageSize(Math.Max(3, Math.Min(choices.Count, 8)))
            .AddChoices(choices);
        prompt.DefaultValue(choices.Contains(current, StringComparer.Ordinal) ? current : choices[0]);
        return await prompt.ShowAsync(console, cancellationToken).ConfigureAwait(false);
    }

    private static void ShowReview(IAnsiConsole console, ConversionConfig config, string through)
    {
        var table = new Table().AddColumn("Setting").AddColumn("Value");
        Add("Game", config.GameDirectory);
        Add("Unity", config.UnityEditor);
        Add("Workspace", config.Workspace);
        Add("Shader sources", config.ShaderSources ?? "(not set)");
        Add("Recovered project", config.RecoveredProject ?? "(not set)");
        Add("Target", TargetName(config.BuildTarget));
        Add("Integration", config.IntegrationManifest ?? "(not set)");
        Add("AssetRipper", "Managed automatically");
        Add("Tool cache", config.ToolCacheDirectory ?? "(default)");
        Add("Offline", config.Offline ? "on" : "off");
        Add("Through", through);
        console.Write(table);

        void Add(string setting, string value) => table.AddRow(new Text(setting), new Text(value));
    }

    private static bool IsGameDirectory(string path) => IsValidPath(path) &&
        File.Exists(Path.Combine(ExpandPath(path), "h3vr_Data", "Managed", "Assembly-CSharp.dll"));

    private static bool TryNormalizeUnity(string value, out string executable)
    {
        try { executable = InstallationDiscovery.NormalizeEditorPath(ExpandPath(value)); return true; }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or NotSupportedException)
        { executable = string.Empty; return false; }
    }

    private static bool IsValidPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { _ = FullPath(value); return true; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private static string TargetName(string buildTarget) => buildTarget switch
    {
        "StandaloneOSX" => "macos",
        "StandaloneLinux64" => "linux",
        "StandaloneWindows64" => "windows",
        _ => throw new InvalidDataException("Unsupported configured build target '" + buildTarget + "'. Choose Windows, macOS or Linux.")
    };

    private static string BuildTargetName(string target) => target switch
    {
        "macos" => "StandaloneOSX",
        "linux" => "StandaloneLinux64",
        _ => "StandaloneWindows64"
    };

    private static bool SafeValidate(Func<string, bool> validate, string value)
    {
        try { return validate(ExpandPath(value)); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        { return false; }
    }

    private static string FullPath(string value) => Path.GetFullPath(ExpandPath(value));

    private static string ExpandPath(string value)
    {
        value = Environment.ExpandEnvironmentVariables(value.Trim());
        if (value == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]);
        return value;
    }
}
