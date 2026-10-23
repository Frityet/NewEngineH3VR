using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace H3VR.Converter;

/// <summary>
/// Builds the pinned, locally patched AssetRipper shader exporter from public source.
/// </summary>
public static class ShaderExporter
{
    public const string UpstreamCommit = "d142a4b46586acdf931aaefc34d3e9eed4b7158e";
    public const string UpstreamRepository = "https://github.com/AssetRipper/AssetRipper.git";
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
            await Run("git", ["clone", "--filter=blob:none", "--no-checkout", "--quiet", UpstreamRepository, checkout], workspace, cancellationToken);
            await Run("git", ["checkout", "--detach", UpstreamCommit], checkout, cancellationToken);
            var head = (await Capture("git", ["rev-parse", "HEAD"], checkout, cancellationToken)).Trim();
            if (!string.Equals(head, UpstreamCommit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"AssetRipper checkout mismatch: expected {UpstreamCommit}, found {head}.");

            var patchPath = Path.Combine(workspace, "assetripper-shader-exporter.patch");
            var patch = ReadSourcePatch();
            await File.WriteAllTextAsync(patchPath, patch, new UTF8Encoding(false), cancellationToken);
            var actualPatchHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(patchPath, cancellationToken))).ToLowerInvariant();
            if (actualPatchHash != ExpectedPatchSha256)
                throw new InvalidDataException($"Embedded AssetRipper patch failed its integrity check ({actualPatchHash}).");
            await Run("git", ["apply", "--check", patchPath], checkout, cancellationToken);
            await Run("git", ["apply", patchPath], checkout, cancellationToken);
            await Run("git", ["diff", "--check"], checkout, cancellationToken);
            var buildFixPatch = await EnsureCommandLineDependency(checkout, cancellationToken);

            var project = Path.Combine(checkout, ProjectPath.Replace('/', Path.DirectorySeparatorChar));
            await Run("dotnet", ["publish", project, "--configuration", "Release", "--runtime", RuntimeIdentifier,
                "--output", buildOutput, "--self-contained", "true", "-p:PublishAot=false", "-p:PublishTrimmed=false"], checkout, cancellationToken);

            var executableName = OperatingSystem.IsWindows() ? "AssetRipper.GUI.Free.exe" : "AssetRipper.GUI.Free";
            var executable = Path.Combine(buildOutput, executableName);
            if (!File.Exists(executable))
                throw new FileNotFoundException("AssetRipper publish completed without its application host.", executable);
            foreach (var dependency in new[] { "System.CommandLine.dll", "AssetRipper.Text.Html.dll", "Cpp2IL.Core.dll" })
                if (!File.Exists(Path.Combine(buildOutput, dependency)))
                    throw new FileNotFoundException($"AssetRipper publish omitted required runtime dependency {dependency}.", Path.Combine(buildOutput, dependency));

            Directory.CreateDirectory(staging);
            CopyTree(buildOutput, staging);
            File.Copy(Path.Combine(checkout, "LICENSE.md"), Path.Combine(staging, "AssetRipper-LICENSE.md"));
            await File.WriteAllTextAsync(Path.Combine(staging, "AssetRipper-SOURCE.md"), SourceNotice, new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, "AssetRipper-ShaderExporter.patch"), patch + "\n" + buildFixPatch, new UTF8Encoding(false), cancellationToken);
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

    // Provenance: this embedded patch modifies AssetRipper source from
    // https://github.com/AssetRipper/AssetRipper at commit
    // d142a4b46586acdf931aaefc34d3e9eed4b7158e. AssetRipper is copyright its
    // contributors and licensed under GNU GPL version 3 only (see its LICENSE.md
    // and README.md). These are generic exporter changes; the corresponding
    // modified-source patch and upstream license are also emitted with the
    // provisioned executable.
    public static string ReadSourcePatch()
    {
        using var stream = typeof(ShaderExporter).Assembly.GetManifestResourceStream("H3VR.Converter.AssetRipper.ShaderExporter.patch")
            ?? throw new InvalidDataException("The pinned AssetRipper source patch is missing from the converter build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var patch = reader.ReadToEnd().TrimEnd('\n');
        (string Line, int Count)[] crlfLines =
        [
            ("-\uFEFFusing AssetRipper.Export.Modules.Shaders.ShaderBlob;", 2),
            ("-using AssetRipper.Export.Modules.Shaders.ShaderBlob.Parameters;", 1),
            ("-\uFEFFusing AssetRipper.Export.Modules.Shaders.UltraShaderConverter.UShader.DirectX;", 1),
            ("-using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.UShader.DirectX;", 1),
            ("-\uFEFFusing AssetRipper.Export.Modules.Shaders.Extensions;", 1),
            ("-\uFEFFusing AssetRipper.SourceGenerated.Extensions.Enums.Shader.SerializedShader;", 1),
        ];
        foreach (var (line, count) in crlfLines)
        {
            for (var i = 0; i < count; i++)
            {
                var offset = patch.IndexOf(line + "\n", StringComparison.Ordinal);
                if (offset < 0) throw new InvalidDataException("Patch line-ending repair did not match its expected source line.");
                patch = patch[..offset] + line + "\r\n" + patch[(offset + line.Length + 1)..];
            }
        }
        patch += "\n";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patch))).ToLowerInvariant();
        if (hash != ExpectedPatchSha256)
            throw new InvalidDataException($"Embedded AssetRipper patch failed its integrity check ({hash}).");
        return patch;
    }

    private static async Task<string> EnsureCommandLineDependency(string checkout, CancellationToken cancellationToken)
    {
        const string projectPath = "Source/AssetRipper.GUI.Free/AssetRipper.GUI.Free.csproj";
        var path = Path.Combine(checkout, projectPath.Replace('/', Path.DirectorySeparatorChar));
        var project = File.ReadAllText(path);
        if (project.Contains("<PackageReference Include=\"System.CommandLine\"", StringComparison.Ordinal) &&
            project.Contains("<PackageReference Include=\"Samboy063.Cpp2IL.Core\"", StringComparison.Ordinal))
            return string.Empty;
        const string marker = "<ProjectReference Include=\"..\\AssetRipper.GUI.Web\\AssetRipper.GUI.Web.csproj\" />";
        if (project.Split(marker, StringSplitOptions.None).Length != 2)
            throw new InvalidDataException("AssetRipper GUI project no longer matches the reviewed command-line dependency patch.");
        var newline = project.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var replacement = string.Join(newline,
            marker,
            "\t\t<PackageReference Include=\"System.CommandLine\" Version=\"2.0.0-beta4.22272.1\" />",
            "\t\t<PackageReference Include=\"AssetRipper.Text.Html\" Version=\"1.0.0\" />",
            "\t\t<PackageReference Include=\"NativeFileDialogs.Net\" Version=\"1.1.0\" />",
            "\t\t<PackageReference Include=\"AsmResolver.DotNet\" Version=\"5.5.1\" />",
            "\t\t<PackageReference Include=\"AssetRipper.Gee.External.Capstone\" Version=\"2.3.2\" />",
            "\t\t<PackageReference Include=\"AssetRipper.Mining.PredefinedAssets\" Version=\"1.2.1\" />",
            "\t\t<PackageReference Include=\"AssetRipper.Primitives\" Version=\"3.1.1\" />",
            "\t\t<PackageReference Include=\"AssetRipper.Tpk\" Version=\"1.0.0\" />",
            "\t\t<PackageReference Include=\"Samboy063.Cpp2IL.Core\" Version=\"2022.1.0-pre-release.14\" />");
        File.WriteAllText(path, project.Replace(marker, replacement, StringComparison.Ordinal), new UTF8Encoding(false));
        return await Capture("git", ["diff", "--", projectPath], checkout, cancellationToken);
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

    private const string ExpectedPatchSha256 = "280611c3ce97a9d518525ff3543fe069e4c5addb6da73fee4b5990d84287038d";
    private const string SourceNotice = """
        AssetRipper GUI Free 1.0.15
        Upstream repository: https://github.com/AssetRipper/AssetRipper
        Upstream commit: d142a4b46586acdf931aaefc34d3e9eed4b7158e
        Local changes: generic serialized shader and DirectX-to-HLSL repairs, plus explicit
        command-line and dynamically loaded UI/import package references in the app host project.
        License: GPL-3.0-only; see AssetRipper-LICENSE.md.
        Corresponding source changes are provided in AssetRipper-ShaderExporter.patch.
        """;
}
