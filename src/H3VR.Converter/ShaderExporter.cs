using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace H3VR.Converter;

/// <summary>
/// Builds the pinned AssetRipper fork shader exporter from public source.
/// </summary>
public static class ShaderExporter
{
    public const string UpstreamCommit = "d142a4b46586acdf931aaefc34d3e9eed4b7158e";
    public const string ForkRepository = "https://github.com/Frityet/AssetRipper.git";
    public const string ForkCommit = "68c73ecf0e2bc59c443246c8ca64af831736567e";
    private const string ProjectPath = "Source/AssetRipper.GUI.Free/AssetRipper.GUI.Free.csproj";
    private static string RuntimeIdentifier
    {
        get
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                Architecture.X86 when OperatingSystem.IsWindows() => "x86",
                _ => throw new PlatformNotSupportedException($"Unsupported AssetRipper process architecture: {RuntimeInformation.ProcessArchitecture}.")
            };
            var platform = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux"
                : throw new PlatformNotSupportedException("AssetRipper provisioning supports macOS, Windows, and Linux.");
            return $"{platform}-{architecture}";
        }
    }

    /// <summary>
    /// Provisions a self-contained AssetRipper executable. The destination must not already exist.
    /// The build checkout is temporary and contains only public source and generic exporter fixes.
    /// </summary>
    public static async Task<string> ProvisionAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var destination = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"Output already exists: {destination}");

        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Output needs a parent directory.", nameof(outputDirectory));
        Directory.CreateDirectory(parent);
        var workspace = Path.Combine(Path.GetTempPath(), "h3vr-assetripper-" + Guid.NewGuid().ToString("N"));
        var checkout = Path.Combine(workspace, "source");
        var buildOutput = Path.Combine(workspace, "publish");
        var staging = Path.Combine(parent, "." + Path.GetFileName(destination) + ".provision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            await Run("git", ["clone", "--filter=blob:none", "--no-checkout", "--quiet", ForkRepository, checkout], workspace, cancellationToken);
            await Run("git", ["checkout", "--detach", ForkCommit], checkout, cancellationToken);
            var head = (await Capture("git", ["rev-parse", "HEAD"], checkout, cancellationToken)).Trim();
            if (!string.Equals(head, ForkCommit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"AssetRipper checkout mismatch: expected {ForkCommit}, found {head}.");
            await Run("git", ["merge-base", "--is-ancestor", UpstreamCommit, ForkCommit], checkout, cancellationToken);

            var project = Path.Combine(checkout, ProjectPath.Replace('/', Path.DirectorySeparatorChar));
            await Run("dotnet", ["publish", project, "--configuration", "Release", "--runtime", RuntimeIdentifier,
                "--output", buildOutput, "--self-contained", "true", "-p:PublishAot=false", "-p:PublishTrimmed=false"], checkout, cancellationToken);

            var executableName = OperatingSystem.IsWindows() ? "AssetRipper.GUI.Free.exe" : "AssetRipper.GUI.Free";
            var executable = Path.Combine(buildOutput, executableName);
            if (!File.Exists(executable))
                throw new FileNotFoundException("AssetRipper publish completed without its application host.", executable);
            foreach (var dependency in new[] { "System.CommandLine.dll", "AssetRipper.Text.Html.dll", "AssetRipper.IO.Endian.dll", "AssetRipper.SourceGenerated.dll", "ICSharpCode.Decompiler.dll", "K4os.Compression.LZ4.dll", "Cpp2IL.Core.dll" })
                if (!File.Exists(Path.Combine(buildOutput, dependency)))
                    throw new FileNotFoundException($"AssetRipper publish omitted required runtime dependency {dependency}.", Path.Combine(buildOutput, dependency));

            Directory.CreateDirectory(staging);
            CopyTree(buildOutput, staging);
            File.Copy(Path.Combine(checkout, "LICENSE.md"), Path.Combine(staging, "AssetRipper-LICENSE.md"));
            await File.WriteAllTextAsync(Path.Combine(staging, "AssetRipper-SOURCE.md"), SourceNotice, new UTF8Encoding(false), cancellationToken);
            Directory.Move(staging, destination);
            return Path.Combine(destination, executableName);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }

    private static async Task Run(string file, IReadOnlyList<string> arguments, string cwd, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Console.WriteLine($"> {file} {string.Join(' ', arguments)}");
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {file}.");
        var stdout = Pump(process.StandardOutput, Console.Out, cancellationToken);
        var stderr = Pump(process.StandardError, Console.Error, cancellationToken);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken));
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} exited with code {process.ExitCode}.");
    }

    private static async Task<string> Capture(string file, IReadOnlyList<string> arguments, string cwd, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {file}.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{file} exited with code {process.ExitCode}: {error}");
        return output;
    }

    private static async Task Pump(StreamReader reader, TextWriter writer, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            await writer.WriteLineAsync(line);
    }

    private static readonly string SourceNotice = $"""
        AssetRipper GUI Free 1.0.15
        Upstream repository: https://github.com/AssetRipper/AssetRipper
        Upstream commit: {UpstreamCommit}
        Fork repository: {ForkRepository}
        Corresponding source: {ForkRepository[..^4]}/tree/{ForkCommit}
        Fork commit: {ForkCommit}
        License: GPL-3.0-only; see AssetRipper-LICENSE.md.
        """;
}
