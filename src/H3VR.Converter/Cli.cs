using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace H3VR.Converter;

public enum ConverterCommand
{
    Convert,
    Doctor,
    Status,
    Init,
    CheckPublication,
    RecoverShaders
}

public sealed record CommandRequest(ConverterCommand Command, ConversionOptions Options)
{
    public string Through => Options.Through ?? "validate";
}

public sealed class ConversionOptions : CommandSettings
{
    [CommandArgument(0, "[config]")]
    [Description("Optional legacy JSON configuration path.")]
    public string? ConfigArgument { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("Optional legacy JSON configuration file.")]
    public string? ConfigOption { get; init; }

    [CommandOption("--game <PATH>")]
    [Description("Installed H3VR folder.")]
    public string? Game { get; init; }

    [CommandOption("--unity <PATH>")]
    [Description("Unity 2021.3.45f2 editor executable or installation.")]
    public string? Unity { get; init; }

    [CommandOption("--workspace <PATH>")]
    [Description("Writable conversion workspace outside the game and converter checkout.")]
    public string? Workspace { get; init; }

    [CommandOption("--shaders <PATH>")]
    [Description("Optional local Assets-shaped shader source overlay.")]
    public string? Shaders { get; init; }

    [CommandOption("--recovered <PATH>")]
    [Description("Existing recovered Unity project (skips AssetRipper export).")]
    public string? Recovered { get; init; }

    [CommandOption("--target <TARGET>")]
    [Description("Build target: windows, macos, or linux.")]
    public string? Target { get; init; }

    [CommandOption("--integration <PATH>")]
    [Description("Hash-pinned native integration manifest for non-Windows targets.")]
    public string? Integration { get; init; }

    [CommandOption("--cache <PATH>")]
    [Description("Tool download and build cache directory.")]
    public string? Cache { get; init; }

    [CommandOption("--offline")]
    [Description("Use cached AssetRipper tools without downloading or building them.")]
    public bool? Offline { get; init; }

    [CommandOption("--tui")]
    [Description("Review or edit configuration in the interactive terminal wizard.")]
    public bool Tui { get; init; }

    [CommandOption("--through <STEP>")]
    [Description("Stop after extract, managed, project, validate, or build. Defaults to validate.")]
    public string? Through { get; init; }

    [CommandOption("--verbose")]
    [Description("Show exception details if a command fails.")]
    public bool Verbose { get; init; }

    [CommandOption("--no-color")]
    [Description("Disable ANSI colors in console output.")]
    public bool NoColor { get; init; }

    [CommandOption("--output <PATH>")]
    [Description("Optional output directory for recover-shaders source export.")]
    public string? Output { get; init; }

    public string? ConfigFile => ConfigOption ?? ConfigArgument;
}

public static class Cli
{
    private static readonly AsyncLocal<Invocation?> Current = new();

    public static async Task<int> RunAsync(
        string[] args,
        Func<CommandRequest, CancellationToken, Task<int>> handler,
        IAnsiConsole? console = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(handler);

        var activeConsole = console ?? AnsiConsole.Console;
        if (args.Contains("--no-color", StringComparer.Ordinal) ||
            Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 })
        {
            DisableColor(activeConsole);
        }

        Current.Value = new Invocation(handler, activeConsole);
        try
        {
            var app = new CommandApp<ConvertCommand>();
            app.Configure(configuration =>
            {
                configuration.SetApplicationName("h3vr-convert");
                configuration.Settings.Console = activeConsole;
                configuration.AddCommand<ExplicitConvertCommand>("convert").WithDescription("Run the conversion (also the default command).");
                configuration.AddCommand<DoctorCommand>("doctor").WithDescription("Check local paths and tool readiness.");
                configuration.AddCommand<StatusCommand>("status").WithDescription("List recorded conversion stages.");
                configuration.AddCommand<InitCommand>("init").WithDescription("Create a starter configuration file.");
                configuration.AddCommand<CheckPublicationCommand>("check-publication")
                    .WithDescription("Check source publication and licensing constraints.");
                configuration.AddCommand<RecoverShadersCommand>("recover-shaders")
                    .WithDescription("Recover shader source data from the local H3VR installation.");
            });
            string[] parsedArgs = args.Length == 0
                ? new[] { "--help" }
                : args[0] == "help"
                    ? args.Length == 1 ? new[] { "--help" } : args.Skip(1).Append("--help").ToArray()
                    : args;
            return await app.RunAsync(parsedArgs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Current.Value = null;
        }
    }

    internal static Task<int> DispatchAsync(CommandContext context, ConverterCommand command, ConversionOptions options,
                                            CancellationToken cancellationToken)
    {
        var invocation = Current.Value
            ?? throw new InvalidOperationException("CLI command ran outside Cli.RunAsync.");
        if (options.Tui && !invocation.Console.Profile.Capabilities.Interactive)
        {
            invocation.Console.WriteLine("--tui requires an interactive terminal. Run without --tui or attach a terminal.");
            return Task.FromResult(2);
        }
        if (options.Tui && !invocation.Console.Profile.Capabilities.Ansi)
        {
            invocation.Console.WriteLine("--tui requires ANSI terminal cursor support. Use a terminal with ANSI support or run without --tui.");
            return Task.FromResult(2);
        }
        var unknown = context.Remaining.Parsed.Select(group => group.Key).ToArray();
        if (unknown.Length != 0 || context.Remaining.Raw.Count != 0)
        {
            string details = string.Join(", ", unknown.Select(Markup.Escape)
                .Concat(context.Remaining.Raw.Select(Markup.Escape)));
            invocation.Console.Write(new Text("Unknown option or argument: " + details));
            invocation.Console.WriteLine();
            return Task.FromResult(2);
        }
        return invocation.Handler(new CommandRequest(command, options), cancellationToken);
    }

    private sealed record Invocation(
        Func<CommandRequest, CancellationToken, Task<int>> Handler,
        IAnsiConsole Console);

    private static void DisableColor(IAnsiConsole console)
    {
        console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
    }

}

public sealed class ConvertCommand : AsyncCommand<ConversionOptions>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConversionOptions settings,
                                           CancellationToken cancellationToken) =>
        Cli.DispatchAsync(context, ConverterCommand.Convert, settings, cancellationToken);
}

public sealed class ExplicitConvertCommand : AsyncCommand<ConversionOptions>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConversionOptions settings,
                                           CancellationToken cancellationToken) =>
        Cli.DispatchAsync(context, ConverterCommand.Convert, settings, cancellationToken);
}

public sealed class DoctorCommand : AsyncCommand<ConversionOptions>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConversionOptions settings,
                                           CancellationToken cancellationToken) =>
        Cli.DispatchAsync(context, ConverterCommand.Doctor, settings, cancellationToken);
}

public sealed class StatusCommand : AsyncCommand<ConversionOptions>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConversionOptions settings,
                                           CancellationToken cancellationToken) =>
        Cli.DispatchAsync(context, ConverterCommand.Status, settings, cancellationToken);
}

public sealed class InitCommand : AsyncCommand<ConversionOptions>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConversionOptions settings,
                                           CancellationToken cancellationToken) =>
        Cli.DispatchAsync(context, ConverterCommand.Init, settings, cancellationToken);
}

public sealed class CheckPublicationCommand : AsyncCommand<ConversionOptions>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConversionOptions settings,
                                           CancellationToken cancellationToken) =>
        Cli.DispatchAsync(context, ConverterCommand.CheckPublication, settings, cancellationToken);
}

public sealed class RecoverShadersCommand : AsyncCommand<ConversionOptions>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConversionOptions settings,
                                           CancellationToken cancellationToken) =>
        Cli.DispatchAsync(context, ConverterCommand.RecoverShaders, settings, cancellationToken);
}
