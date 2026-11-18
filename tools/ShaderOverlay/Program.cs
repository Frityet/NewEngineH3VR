using System.ComponentModel;
using System.Text.Json;
using H3VR.Converter;
using Spectre.Console.Cli;

namespace NativeH3VR.ShaderOverlay;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        var app = new CommandApp();
        app.Configure(configuration =>
        {
            configuration.SetApplicationName("shader-overlay");
            configuration.SetExceptionHandler((error, _) =>
            {
                Console.Error.WriteLine(error is InvalidDataException or IOException or ArgumentException ? error.Message : error);
                return 1;
            });

            configuration.AddCommand<LinkCommand>("link").WithDescription("Match raw exports to the local Steam extraction.");
            configuration.AddCommand<ComputeCommand>("compute").WithDescription("Translate serialized Direct3D compute shaders.");
        });
        return await app.RunAsync(args, cancellation.Token);
    }
}

public sealed class LinkOptions : CommandSettings
{
    [CommandOption("--workspace <PATH>", isRequired: true)] public string Workspace { get; init; } = "";
    [CommandOption("--recovery <PATH>", isRequired: true)] public string Recovery { get; init; } = "";
    [CommandOption("--steam-install <PATH>", isRequired: true)] public string SteamInstall { get; init; } = "";
    [CommandOption("--output <PATH>", isRequired: true)] public string Output { get; init; } = "";
    [CommandOption("--overlay-candidate <PATH>")] public string? OverlayCandidate { get; init; }
    [CommandOption("--repair-exclusive-keywords")] public bool RepairExclusiveKeywords { get; init; }
    [CommandOption("--class-database <PATH>")]
    [Description("Unity type tree package for stripped shader objects. Defaults to cache/legacy-unity-classdata.tpk.")]
    public string? ClassDatabase { get; init; }
}

public sealed class LinkCommand : Command<LinkOptions>
{
    public override int Execute(CommandContext context, LinkOptions settings, CancellationToken cancellationToken) =>
        ShaderLinks.Run(settings, cancellationToken);
}

public sealed class ComputeOptions : CommandSettings
{
    [CommandOption("--workspace <PATH>", isRequired: true)] public string Workspace { get; init; } = "";
    [CommandOption("--overlay <PATH>", isRequired: true)] public string Overlay { get; init; } = "";
    [CommandOption("--dxbc-compiler <PATH>", isRequired: true)]
    [Description("Built dxil-spirv executable.")] public string Compiler { get; init; } = "";
    [CommandOption("--spirv-cross <PATH>", isRequired: true)] public string Cross { get; init; } = "";
}

public sealed class ComputeCommand : AsyncCommand<ComputeOptions>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ComputeOptions settings, CancellationToken cancellationToken)
    {
        await ComputeRecovery.Run(settings, cancellationToken);
        return 0;
    }
}

internal static class Receipt
{
    private static readonly JsonSerializerOptions Json = new(ConversionConfig.Json)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Missing receipt data: " + path);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json) + "\n";

    public static string Child(string root, string name)
    {
        var path = SafeFiles.Inside(root, name.Replace('/', Path.DirectorySeparatorChar));
        if ((!File.Exists(path) && !Directory.Exists(path)) || Path.GetFullPath(path) != SafeFiles.Canonical(path))
            throw new InvalidDataException("Unsafe or missing receipt path: " + name);
        return path;
    }

    public static void Verify(string path, string expected)
    {
        if (SafeFiles.Hash(path) != expected) throw new InvalidDataException("Receipt hash differs: " + path);
    }

    public static StepReceipt Extraction(string workspace)
    {
        var receipt = Read<StepReceipt>(Child(workspace, ".conversion/extract.json"));
        if (receipt.Step != "extract") throw new InvalidDataException("A complete cold extraction is required.");
        return receipt;
    }

    public static void WriteNew<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        writer.Write(Serialize(value));
    }
}
