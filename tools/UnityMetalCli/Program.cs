using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using H3VR.Converter;

namespace UnityMetalCli;

internal static class Program
{
    private static readonly Command[] Commands =
    [
        new("build", "Build the ARM64 Metal client and write a local Unity integration manifest.",
            [PathOption("unity-editor"), PathOption("openxr-headers"), PathOption("platform"), PathOption("output"), new("unity-version", "VERSION")]),
        new("combine", "Put three signed renderer builds into a single local NativeH3VR app.",
            [PathOption("base"), PathOption("builtin"), PathOption("urp"), PathOption("output"), new("share-assets")]),
        new("package-urp", "Package only the URP player in a launchable NativeH3VR app.",
            [PathOption("app"), PathOption("output")]),
        new("refresh-launcher", "Replace and verify the launcher in a staged NativeH3VR app.",
            [PathOption("app")]),
        new("run", "Launch a private Apple Silicon player against a verified MacVR package.",
            [
                PathOption("app"), PathOption("runtime"), PathOption("state-dir"), PathOption("log-file"),
                new("synthetic"), new("headset"), PathOption("trace-file", false),
                PathOption("unity-profile-file", false), PathOption("capture-file", false), PathOption("mods-dir", false),
                new("timeout", "SECONDS"), new("renderer", "base|builtin|urp"), PathOption("run-checkout", false)
            ], AllowsPlayerArguments: true),
        new("profile", "Summarize one comparable interval from Unity and MacVR frame traces.",
            [
                PathOption("unity"), PathOption("openxr"), new("warmup-seconds", "SECONDS"),
                new("window-seconds", "SECONDS"), new("unity-frames", "START END", Arity: 2)
            ]),
        new("smoke", "Build and run the source-only Unity Metal/OpenXR fixture with two captured eyes.",
            [PathOption("unity-editor"), PathOption("openxr-headers"), PathOption("runtime"), PathOption("work"), new("frames", "COUNT")])
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("Usage: UnityMetalCli <build|combine|package-urp|refresh-launcher|run|profile|smoke> [options]");
            foreach (var item in Commands)
                Console.WriteLine($"  {item.Name,-8} {item.Description}");
            Console.WriteLine("Run a subcommand with --help for its options.");
            return 0;
        }

        Command? command = Commands.FirstOrDefault(candidate => candidate.Name == args[0]);
        if (command is null)
        {
            Console.Error.WriteLine($"Unknown command: {args[0]}. See --help.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            Arguments options = Arguments.Parse(command, args[1..]);
            if (options.Help)
            {
                PrintHelp(command);
                return 0;
            }

            switch (command.Name)
            {
                case "build":
                    Console.WriteLine(await IntegrationBuild.BuildAsync(
                        options["unity-editor"], options["openxr-headers"], options["platform"], options["output"],
                        options.Get("unity-version") ?? ConversionConfig.TargetVersion, cancellation.Token));
                    return 0;
                case "combine":
                    await RendererPackage.CombineAsync(options["base"], options["builtin"], options["urp"],
                        options["output"], options.Has("share-assets"), cancellation.Token);
                    return 0;
                case "package-urp":
                    await RendererPackage.PackageUrpAsync(options["app"], options["output"], cancellation.Token);
                    return 0;
                case "refresh-launcher":
                    await RendererPackage.RefreshLauncherAsync(options["app"], cancellation.Token);
                    return 0;
                case "run":
                    bool synthetic = options.Has("synthetic");
                    if (synthetic == options.Has("headset"))
                        throw new UsageException("Choose exactly one of --synthetic and --headset.");
                    return await PlayerRun.RunAsync(new Launch(
                        options["app"], options["runtime"], options["state-dir"], options["log-file"],
                        synthetic, options.PlayerArguments, options.Get("trace-file"), options.Get("unity-profile-file"),
                        options.Get("capture-file"), Timeout(options), options.Get("mods-dir"),
                        options.Get("renderer"), options.Get("run-checkout")), cancellation.Token);
                case "profile":
                    double warmup = options.GetSeconds("warmup-seconds", allowZero: true) ?? 2;
                    double? window = options.GetSeconds("window-seconds");
                    (int First, int Last)? frames = options.Values.TryGetValue("unity-frames", out var bounds)
                        ? (Integer(bounds[0], "unity-frames"), Integer(bounds[1], "unity-frames"))
                        : null;
                    Console.WriteLine(JsonSerializer.Serialize(
                        TraceProfile.Analyze(options["unity"], options["openxr"], warmup, window, frames),
                        new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                case "smoke":
                    int framesRequested = options.Get("frames") is { } text ? Integer(text, "frames") : 240;
                    if (framesRequested < 60)
                        throw new UsageException("Use at least 60 stereo frames.");
                    await MetalSmoke.RunAsync(options["unity-editor"], options["openxr-headers"],
                        options["runtime"], options["work"], framesRequested, cancellation.Token);
                    return 0;
                default:
                    throw new InvalidOperationException("Unregistered command: " + command.Name);
            }
        }
        catch (UsageException error)
        {
            Console.Error.WriteLine($"{error.Message} See {command.Name} --help.");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"Unity Metal {command.Name} canceled.");
            return 130;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                                             InvalidOperationException or KeyNotFoundException or JsonException or
                                             FormatException or OverflowException or Win32Exception or TimeoutException or
                                             PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"Unity Metal {command.Name} failed: {error.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static Option PathOption(string name, bool required = true) => new(name, "PATH", required);

    private static TimeSpan? Timeout(Arguments options)
    {
        if (options.GetSeconds("timeout") is not { } seconds)
            return null;
        return TimeSpan.FromSeconds(seconds);
    }

    private static int Integer(string value, string option) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : throw new UsageException($"--{option} requires an integer.");

    private static void PrintHelp(Command command)
    {
        string remainder = command.AllowsPlayerArguments ? " [-- UNITY_ARGUMENTS...]" : "";
        Console.WriteLine($"Usage: UnityMetalCli {command.Name} [options]{remainder}");
        Console.WriteLine(command.Description);
        foreach (var option in command.Options)
        {
            string value = option.Value is null ? "" : " " + option.Value;
            Console.WriteLine($"  --{option.Name}{value}{(option.Required ? " (required)" : "")}");
        }
        if (command.AllowsPlayerArguments)
            Console.WriteLine("Choose exactly one of --synthetic and --headset.");
        if (command.Name is "build" or "run" or "smoke")
            Console.WriteLine("Choose output paths under the checkout's .local/ directory.");
    }

    private sealed record Option(string Name, string? Value = null, bool Required = false, int Arity = 1);
    private sealed record Command(string Name, string Description, Option[] Options, bool AllowsPlayerArguments = false);

    private sealed class UsageException(string message) : Exception(message);

    private sealed class Arguments
    {
        public Dictionary<string, string[]> Values { get; } = new(StringComparer.Ordinal);
        public string[] PlayerArguments { get; private set; } = [];
        public bool Help { get; private set; }

        public bool Has(string name) => Values.ContainsKey(name);
        public string? Get(string name) => Values.TryGetValue(name, out var values) ? values.Single() : null;
        public string this[string name] => Get(name)!;

        public double? GetSeconds(string name, bool allowZero = false)
        {
            if (Get(name) is not { } text)
                return null;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                !double.IsFinite(value) || value < 0 || (!allowZero && value == 0))
                throw new UsageException($"--{name} requires {(allowZero ? "nonnegative" : "positive")} seconds.");
            return value;
        }

        public static Arguments Parse(Command command, string[] args)
        {
            var result = new Arguments();
            var options = command.Options.ToDictionary(option => "--" + option.Name, StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index++)
            {
                string token = args[index];
                if (token is "-h" or "--help")
                {
                    result.Help = true;
                    return result;
                }
                if (command.AllowsPlayerArguments && (token == "--" || !token.StartsWith('-')))
                {
                    result.PlayerArguments = args[(index + (token == "--" ? 1 : 0))..];
                    break;
                }

                string[] assignment = token.Split('=', 2);
                if (!options.TryGetValue(assignment[0], out Option? option))
                    throw new UsageException($"Unknown option or argument: {token}");
                if (option.Value is null)
                {
                    if (assignment.Length != 1)
                        throw new UsageException($"{assignment[0]} does not take a value.");
                    result.Values[option.Name] = [];
                    continue;
                }

                var values = new List<string>();
                if (assignment.Length == 2)
                    values.Add(assignment[1]);
                while (values.Count < option.Arity && index + 1 < args.Length &&
                       args[index + 1] is not ("--" or "-h") && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    values.Add(args[++index]);
                if (values.Count != option.Arity || values.Any(string.IsNullOrWhiteSpace))
                    throw new UsageException($"{assignment[0]} requires {option.Value}.");
                result.Values[option.Name] = values.ToArray();
            }

            var missing = command.Options.Where(option => option.Required && !result.Has(option.Name))
                .Select(option => "--" + option.Name).ToArray();
            if (missing.Length != 0)
                throw new UsageException("Missing required options: " + string.Join(", ", missing) + ".");
            return result;
        }
    }
}
