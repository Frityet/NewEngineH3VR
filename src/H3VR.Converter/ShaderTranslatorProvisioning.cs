using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace H3VR.Converter;

internal static class ShaderTranslatorProvisioning
{
    private const int CacheSchema = 1;
    private const string ManifestName = "translator.json";
    private sealed record Tool(string Name, string Repository, string Revision, string License, bool SharedLibrary);
    private sealed record Manifest(int Schema, string Repository, string Revision, string Runtime,
        SortedDictionary<string, string> Files);

    private static readonly Tool Dxbc = new("dxil-spirv", "https://github.com/HansKristian-Work/dxil-spirv.git",
        "a781dce9b5f2dd076f419d7f98dcc2dcf5f011d5", "LICENSE.MIT", true);
    private static readonly Tool Cross = new("spirv-cross", "https://github.com/KhronosGroup/SPIRV-Cross.git",
        "6c09849fe88c48eaed08413aa022aaa136a3a057", "LICENSE", false);

    internal static async Task<ConversionConfig> PrepareAsync(
        string repository, ConversionConfig config, CancellationToken cancellationToken)
    {
        if (config.ShaderSources is not null) return config;
        var cmake = new Lazy<Task<string>>(() => LocalCMakeProvisioning.ResolveAsync(repository, config, cancellationToken));
        string dxbc = config.DxbcCompiler ?? await EnsureAsync(repository, config, Dxbc, cmake, cancellationToken);
        string cross = config.SpirvCross ?? await EnsureAsync(repository, config, Cross, cmake, cancellationToken);
        return config with { DxbcCompiler = dxbc, SpirvCross = cross };
    }

    private static async Task<string> EnsureAsync(
        string repository, ConversionConfig config, Tool tool, Lazy<Task<string>> cmake, CancellationToken cancellationToken)
    {
        string runtime = ShaderToolCache.CurrentRuntimeIdentifier();
        string root = Path.Combine(config.CacheDirectory(repository), "shader-translators");
        string key = $"{tool.Name}-{tool.Revision}-{runtime}";
        string destination = Path.Combine(root, key);
        SafeFiles.RequireToolCachePath(repository, destination);
        if (Directory.Exists(destination)) return Verify(destination, tool, runtime);
        if (config.Offline)
            throw new InvalidOperationException($"Offline mode requires a verified {tool.Name} cache at {destination} or an explicit translator path.");

        string lockPath = destination + ".lock";
        SafeFiles.RequireToolCachePath(repository, lockPath);
        Directory.CreateDirectory(root);
        await using var lease = await AcquireLockAsync(lockPath, cancellationToken);
        if (Directory.Exists(destination)) return Verify(destination, tool, runtime);
        if (File.Exists(destination))
            throw new IOException("Translator cache path is occupied by a file " + destination);

        string staging = Path.Combine(root, "." + key + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string source = Path.Combine(staging, "source");
            string artifacts = Path.Combine(staging, "artifacts");
            string package = Path.Combine(staging, "package");
            Directory.CreateDirectory(source);
            string cmakeExecutable = await cmake.Value;
            await Processes.Run("git", ["init", "--quiet"], source, cancellation: cancellationToken);
            await Processes.Run("git", ["remote", "add", "origin", tool.Repository], source, cancellation: cancellationToken);
            await Processes.Run("git", ["fetch", "--depth=1", "origin", tool.Revision], source, cancellation: cancellationToken);
            await Processes.Run("git", ["checkout", "--detach", tool.Revision], source, cancellation: cancellationToken);
            if (tool.SharedLibrary)
                await Processes.Run("git", ["submodule", "update", "--init", "--recursive", "--depth=1"],
                    source, cancellation: cancellationToken);

            await BuildAsync(cmakeExecutable, tool, staging, source, artifacts, cancellationToken);
            Directory.CreateDirectory(package);
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in RuntimeFiles(tool, artifacts).Append("LICENSE"))
            {
                string input = name == "LICENSE" ? Path.Combine(source, tool.License) : Path.Combine(artifacts, name);
                string output = Path.Combine(package, name);
                SafeFiles.Copy(SafeFiles.Canonical(input), output);
                hashes.Add(name, SafeFiles.Hash(output));
            }
            SafeFiles.AtomicJson(Path.Combine(package, ManifestName),
                new Manifest(CacheSchema, tool.Repository, tool.Revision, runtime, hashes));
            string executable = Verify(package, tool, runtime);
            await VerifyInvocationAsync(executable, package, tool, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(package, destination);
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException("Translator provisioning needs Git, CMake, Python 3 and a native C++ build environment.",
                error);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        return Verify(destination, tool, runtime);
    }

    private static async Task BuildAsync(string cmake, Tool tool, string staging, string source, string artifacts,
        CancellationToken cancellationToken)
    {
        string build = Path.Combine(staging, "build");
        var arguments = new List<string>
        {
            "-S", source, "-B", build, "-DCMAKE_BUILD_TYPE=Release",
            "-DCMAKE_RUNTIME_OUTPUT_DIRECTORY=" + artifacts, "-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE=" + artifacts,
            "-DCMAKE_LIBRARY_OUTPUT_DIRECTORY=" + artifacts, "-DCMAKE_LIBRARY_OUTPUT_DIRECTORY_RELEASE=" + artifacts
        };
        if (OperatingSystem.IsMacOS())
            arguments.Add("-DCMAKE_OSX_ARCHITECTURES=" +
                (ShaderToolCache.CurrentRuntimeIdentifier() == "osx-arm64" ? "arm64" : "x86_64"));
        if (tool.SharedLibrary)
        {
            arguments.Add("-DSPIRV_SKIP_TESTS=ON");
            if (!OperatingSystem.IsWindows())
            {
                arguments.Add("-DCMAKE_BUILD_WITH_INSTALL_RPATH=ON");
                arguments.Add("-DCMAKE_INSTALL_RPATH=" + (OperatingSystem.IsMacOS() ? "@loader_path" : "$ORIGIN"));
            }
        }
        else
        {
            arguments.Add("-DSPIRV_CROSS_ENABLE_TESTS=OFF");
        }
        await Processes.Run(cmake, arguments, staging, cancellation: cancellationToken);
        string jobs = Math.Clamp(Environment.ProcessorCount, 1, 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Processes.Run(cmake, ["--build", build, "--config", "Release", "--parallel", jobs, "--target", tool.Name],
            staging, cancellation: cancellationToken);
    }

    private static string Verify(string directory, Tool tool, string runtime)
    {
        var expected = RuntimeFiles(tool, directory).Append("LICENSE").ToHashSet(StringComparer.Ordinal);
        var manifestPath = Path.Combine(directory, ManifestName);
        if (new DirectoryInfo(directory).LinkTarget is not null || new FileInfo(manifestPath).LinkTarget is not null)
            throw new InvalidDataException("Translator cache may not contain links " + directory);
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), ConversionConfig.Json);
        var actual = SafeFiles.Files(directory).Select(file => Path.GetRelativePath(directory, file))
            .Where(relative => relative != ManifestName);
        if (manifest is null || manifest.Schema != CacheSchema || manifest.Repository != tool.Repository ||
            manifest.Revision != tool.Revision || manifest.Runtime != runtime || manifest.Files is null ||
            !expected.SetEquals(manifest.Files.Keys) || !expected.SetEquals(actual))
            throw new InvalidDataException("Translator cache provenance differs from the pinned inputs " + directory);
        foreach (var (relative, hash) in manifest.Files)
            if (SafeFiles.Hash(SafeFiles.Inside(directory, relative)) != hash)
                throw new InvalidDataException("Cached translator file hash differs " + relative);
        string executable = Path.Combine(directory, ExecutableName(tool));
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
            throw new InvalidDataException("Cached translator is not executable " + executable);
        return executable;
    }

    private static async Task VerifyInvocationAsync(string executable, string directory, Tool tool,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("--help");
        using var process = Process.Start(info) ?? throw new IOException("Could not start translator " + executable);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await Task.WhenAll(output, errors, process.WaitForExitAsync(cancellationToken)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        string usage = (await output) + (await errors);
        string[] options = tool.SharedLibrary ? ["--validate", "--ssbo-uav", "--ssbo-srv"] :
            ["--reflect", "--hlsl", "--rename-entry-point"];
        if (process.ExitCode is not (0 or 1) || options.Any(option => !usage.Contains(option, StringComparison.Ordinal)))
            throw new InvalidDataException("Built translator failed its CLI check " + executable);
    }

    private static string ExecutableName(Tool tool) => tool.Name + (OperatingSystem.IsWindows() ? ".exe" : "");

    private static string[] RuntimeFiles(Tool tool, string directory)
    {
        if (!tool.SharedLibrary) return [ExecutableName(tool)];
        string library = OperatingSystem.IsMacOS() ? "libdxil-spirv-c-shared.2.dylib" :
            OperatingSystem.IsWindows() ? "dxil-spirv-c-shared.dll" : "libdxil-spirv-c-shared.so.2";
        if (OperatingSystem.IsWindows() && !File.Exists(Path.Combine(directory, library)))
            library = "lib" + library;
        return [ExecutableName(tool), library];
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(300, cancellationToken); }
        }
    }
}
