using H3VR.Converter;
using Spectre.Console;
using Spectre.Console.Testing;

namespace H3VR.Converter.Tests;

public static class CliTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("CLI bare flags dispatch the default convert command", DefaultConvert);
        yield return ("CLI explicit commands dispatch their command kinds", ExplicitCommands);
        yield return ("CLI config option takes precedence over legacy positional config", ConfigPrecedence);
        yield return ("CLI rejects unknown options without dispatching", UnknownOption);
        yield return ("CLI no-args and help show usage without dispatching", NoArgsHelp);
        yield return ("CLI generated command help includes option descriptions", CommandHelp);
        yield return ("CLI malformed flags report through the injected console", MalformedFlag);
        yield return ("CLI propagates cancellation to the handler", Cancellation);
        yield return ("TUI reviews literal paths and supports cancel", TuiCancel);
        yield return ("TUI rejects redirected input with a helpful message", NonInteractiveTui);
        yield return ("TUI requires ANSI cursor support before prompting", TuiRequiresAnsi);
        yield return ("no-color preserves ANSI cursor support for TUI", NoColorTui);
    }

    private static void DefaultConvert()
    {
        var console = new TestConsole();
        var requests = new List<CommandRequest>();
        int code = Cli.RunAsync(new[] { "--game", "/tmp/Game With Spaces", "--unity", "/tmp/Unity",
                "--workspace", "/tmp/workspace", "--shaders", "/tmp/shaders", "--recovered", "/tmp/recovered",
                "--target", "macos", "--integration", "/tmp/integration.json", "--cache", "/tmp/cache",
                "--offline", "--through", "project", "--verbose", "--no-color", "--output", "/tmp/output" },
            (request, _) => { requests.Add(request); return Task.FromResult(23); }, console).GetAwaiter().GetResult();

        var request = requests.Single();
        Check(code == 23 && request.Command == ConverterCommand.Convert, "bare flags did not dispatch Convert or preserve its exit code");
        Check(request.Options.Game == "/tmp/Game With Spaces" && request.Options.Unity == "/tmp/Unity" &&
              request.Options.Workspace == "/tmp/workspace" && request.Options.Shaders == "/tmp/shaders" &&
              request.Options.Recovered == "/tmp/recovered" && request.Options.Target == "macos" &&
              request.Options.Integration == "/tmp/integration.json" && request.Options.Cache == "/tmp/cache" &&
              request.Options.Offline == true && request.Options.Through == "project" &&
              request.Options.Verbose && request.Options.NoColor && request.Options.Output == "/tmp/output",
              "one or more conversion flags were not bound to ConversionOptions");
        Check(request.Through == "project", "request did not expose its selected pipeline stage");
    }

    private static void ExplicitCommands()
    {
        var console = new TestConsole();
        var actual = new List<(ConverterCommand Command, string? Config, string? Output)>();
        foreach (var (arguments, expected) in new[]
        {
            (new[] { "convert", "--game", "/tmp/game" }, ConverterCommand.Convert),
            (new[] { "doctor", "--unity", "/tmp/Unity" }, ConverterCommand.Doctor),
            (new[] { "status" }, ConverterCommand.Status),
            (new[] { "init", "/tmp/new-config.json" }, ConverterCommand.Init),
            (new[] { "check-publication" }, ConverterCommand.CheckPublication),
            (new[] { "recover-shaders", "--output", "/tmp/shader-output" }, ConverterCommand.RecoverShaders)
        })
        {
            int result = Cli.RunAsync(arguments, (request, _) =>
            {
                actual.Add((request.Command, request.Options.ConfigFile, request.Options.Output));
                return Task.FromResult(0);
            }, console).GetAwaiter().GetResult();
            Check(result == 0 && actual[^1].Command == expected, "explicit command did not dispatch: " + arguments[0]);
        }
        Check(actual.Single(x => x.Command == ConverterCommand.Init).Config == "/tmp/new-config.json",
            "init positional path did not bind as the optional config argument");
        Check(actual.Single(x => x.Command == ConverterCommand.RecoverShaders).Output == "/tmp/shader-output",
            "recover-shaders output option was not bound");
    }

    private static void ConfigPrecedence()
    {
        var console = new TestConsole();
        CommandRequest? request = null;
        int code = Cli.RunAsync(new[] { "legacy.json", "--config", "flag.json" },
            (value, _) => { request = value; return Task.FromResult(0); }, console).GetAwaiter().GetResult();
        Check(code == 0 && request?.Options.ConfigArgument == "legacy.json" &&
              request.Options.ConfigOption == "flag.json" && request.Options.ConfigFile == "flag.json",
              "flag config did not take precedence over the legacy positional config");
    }

    private static void UnknownOption()
    {
        var console = new TestConsole();
        bool dispatched = false;
        int code = Cli.RunAsync(new[] { "--unknown-option" },
            (_, _) => { dispatched = true; return Task.FromResult(0); }, console).GetAwaiter().GetResult();
        Check(code == 2 && !dispatched && console.Output.Contains("Unknown option", StringComparison.Ordinal),
            "unknown option was silently accepted or did not explain the error");
    }

    private static void NoArgsHelp()
    {
        foreach (var arguments in new[] { Array.Empty<string>(), new[] { "help" }, new[] { "--help" } })
        {
            var console = new TestConsole();
            bool dispatched = false;
            int code = Cli.RunAsync(arguments, (_, _) => { dispatched = true; return Task.FromResult(0); }, console)
                .GetAwaiter().GetResult();
            Check(code == 0 && !dispatched && console.Output.Contains("doctor", StringComparison.OrdinalIgnoreCase),
                "empty/help invocation did not show command help");
        }
    }

    private static void CommandHelp()
    {
        foreach (var arguments in new[] { new[] { "help", "recover-shaders" }, new[] { "recover-shaders", "--help" } })
        {
            var console = new TestConsole();
            bool dispatched = false;
            int code = Cli.RunAsync(arguments, (_, _) => { dispatched = true; return Task.FromResult(0); }, console)
                .GetAwaiter().GetResult();
            Check(code == 0 && !dispatched &&
                  console.Output.Contains("--output", StringComparison.Ordinal) &&
                  console.Output.Contains("Optional output directory", StringComparison.Ordinal),
                "generated recover-shaders help omitted its options or descriptions");
        }
    }

    private static void MalformedFlag()
    {
        var console = new TestConsole();
        bool dispatched = false;
        int code = Cli.RunAsync(new[] { "--game" },
            (_, _) => { dispatched = true; return Task.FromResult(0); }, console).GetAwaiter().GetResult();
        Check(code != 0 && !dispatched &&
              (console.Output.Contains("expects", StringComparison.OrdinalIgnoreCase) ||
               console.Output.Contains("value", StringComparison.OrdinalIgnoreCase)),
            "a missing --game value did not produce an actionable parser error in the injected console");
    }

    private static void Cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool observedCancellation = false;
        var run = Cli.RunAsync(new[] { "--game", "/tmp/game" }, async (_, token) =>
        {
            Check(token.CanBeCanceled, "handler received no cancellation token");
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { observedCancellation = true; return 130; }
            return 0;
        }, new TestConsole(), cancellation.Token);
        entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        cancellation.Cancel();
        int code = run.GetAwaiter().GetResult();
        Check(code == 130 && observedCancellation, "CLI did not deliver cancellation to the active handler");
    }

    private static void TuiCancel()
    {
        using var temp = new TempDirectory();
        string game = Path.Combine(temp.Root, "game [red]");
        Write(Path.Combine(game, "h3vr_Data", "Managed", "Assembly-CSharp.dll"), "synthetic game marker");
        string editor = Path.Combine(temp.Root, "Unity");
        Write(editor, "synthetic editor marker");
        string workspace = Path.Combine(temp.Root, "workspace");

        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        foreach (string value in new[] { game, editor, workspace, "" }) console.Input.PushTextWithEnter(value);
        for (int i = 0; i < 3; i++) console.Input.PushKey(ConsoleKey.Enter); // target, stage, and advanced toggle
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        Throws<OperationCanceledException>(() => TerminalWizard.EditAsync(new ConversionConfig(), "validate", console)
            .GetAwaiter().GetResult());
        Check(console.Output.Contains(game, StringComparison.Ordinal), "wizard review did not show the entered path literally");
    }

    private static void NonInteractiveTui()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = false;
        bool dispatched = false;
        int code = Cli.RunAsync(new[] { "--tui" }, (_, _) => { dispatched = true; return Task.FromResult(0); }, console)
            .GetAwaiter().GetResult();
        Check(code == 2 && !dispatched && console.Output.Contains("interactive terminal", StringComparison.OrdinalIgnoreCase),
            "redirected TUI request was not rejected with a useful message");
    }

    private static void TuiRequiresAnsi()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = false;
        bool dispatched = false;
        int code = Cli.RunAsync(new[] { "--tui" }, (_, _) => { dispatched = true; return Task.FromResult(0); }, console)
            .GetAwaiter().GetResult();
        Check(code == 2 && !dispatched &&
              console.Output.Contains("ANSI terminal cursor support", StringComparison.OrdinalIgnoreCase),
            "TUI did not fail early with an actionable ANSI capability message");
    }

    private static void NoColorTui()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        bool dispatched = false;
        int code = Cli.RunAsync(new[] { "--tui", "--no-color" }, (_, _) =>
        {
            dispatched = true;
            return Task.FromResult(0);
        }, console).GetAwaiter().GetResult();
        Check(code == 0 && dispatched && console.Profile.Capabilities.Ansi &&
              console.Profile.Capabilities.ColorSystem == ColorSystem.NoColors,
            "--no-color disabled cursor controls or prevented TUI dispatch");

        using var temp = new TempDirectory();
        string game = Path.Combine(temp.Root, "game");
        Write(Path.Combine(game, "h3vr_Data", "Managed", "Assembly-CSharp.dll"), "synthetic game marker");
        string editor = Path.Combine(temp.Root, "Unity");
        Write(editor, "synthetic editor marker");
        var promptConsole = new TestConsole();
        promptConsole.Profile.Capabilities.Interactive = true;
        promptConsole.Profile.Capabilities.Ansi = true;
        promptConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        foreach (string value in new[] { game, editor, Path.Combine(temp.Root, "workspace"), "" })
            promptConsole.Input.PushTextWithEnter(value);
        for (int i = 0; i < 4; i++) promptConsole.Input.PushKey(ConsoleKey.Enter);
        var edited = TerminalWizard.EditAsync(new ConversionConfig(), "validate", promptConsole)
            .GetAwaiter().GetResult();
        Check(edited.Config.GameDirectory == Path.GetFullPath(game) &&
              promptConsole.Profile.Capabilities.Ansi &&
              promptConsole.Profile.Capabilities.ColorSystem == ColorSystem.NoColors,
            "interactive wizard failed with color disabled but ANSI cursor support enabled");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "h3vr-cli-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Root);
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
    }
}
