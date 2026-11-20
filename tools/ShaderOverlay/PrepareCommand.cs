using System.ComponentModel;
using H3VR.Converter;
using Spectre.Console.Cli;

namespace NativeH3VR.ShaderOverlay;

public sealed class PrepareOptions : CommandSettings
{
    [CommandOption("--workspace <PATH>", isRequired: true)] public string Workspace { get; init; } = "";
    [CommandOption("--recovery <PATH>", isRequired: true)] public string Recovery { get; init; } = "";
    [CommandOption("--steam-install <PATH>", isRequired: true)] public string SteamInstall { get; init; } = "";
    [CommandOption("--output <PATH>", isRequired: true)] public string Output { get; init; } = "";
    [CommandOption("--dxbc-compiler <PATH>", isRequired: true)]
    [Description("Built dxil-spirv executable.")] public string Compiler { get; init; } = "";
    [CommandOption("--spirv-cross <PATH>", isRequired: true)] public string Cross { get; init; } = "";
    [CommandOption("--class-database <PATH>")]
    [Description("Unity type tree package. Defaults to cache/legacy-unity-classdata.tpk.")] public string? ClassDatabase { get; init; }
}

public sealed class PrepareCommand : AsyncCommand<PrepareOptions>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PrepareOptions settings, CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(settings.Output);
        SafeFiles.RequirePrivateOutput(Repository.Find(), output);
        foreach (var input in new[] { settings.Workspace, settings.Recovery, settings.SteamInstall })
            SafeFiles.RequireSeparate(input, output);
        if (Directory.Exists(output) || File.Exists(output) || new FileInfo(output).LinkTarget is not null)
            throw new IOException("Choose a new overlay output: " + output);

        var compiler = SafeFiles.Canonical(settings.Compiler);
        var cross = SafeFiles.Canonical(settings.Cross);
        foreach (var executable in new[] { compiler, cross })
            if (!File.Exists(executable))
                throw new FileNotFoundException("Missing compute translation executable " + executable, executable);

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string staging = output + ".prepare-" + Guid.NewGuid().ToString("N");
        string candidate = Path.Combine(staging, "overlay");
        string report = Path.Combine(staging, "shader-correspondence.json");
        Directory.CreateDirectory(staging);
        bool published = false;
        try
        {
            var linkResult = ShaderLinks.Run(new LinkOptions
            {
                Workspace = settings.Workspace,
                Recovery = settings.Recovery,
                SteamInstall = settings.SteamInstall,
                Output = report,
                OverlayCandidate = candidate,
                RepairExclusiveKeywords = true,
                ClassDatabase = settings.ClassDatabase
            }, cancellationToken);
            if (linkResult != 0) return linkResult;

            await ComputeRecovery.Run(new ComputeOptions
            {
                Workspace = settings.Workspace,
                Overlay = candidate,
                Compiler = compiler,
                Cross = cross
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(candidate, output);
            published = true;
            Console.WriteLine("Prepared shader overlay: " + output);
            return 0;
        }
        finally
        {
            if (published || !File.Exists(report))
                Directory.Delete(staging, true);
            else
            {
                if (Directory.Exists(candidate)) Directory.Delete(candidate, true);
                Console.Error.WriteLine("Shader correspondence from failed preparation: " + report);
            }
        }
    }
}
