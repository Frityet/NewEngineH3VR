using System.Diagnostics;
using System.Text;

namespace H3VR.Converter;

public static class Publication
{
    private static byte[] Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var output = new MemoryStream(); process.StandardOutput.BaseStream.CopyTo(output);
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(error);
        return output.ToArray();
    }
    public static int Check(string root)
    {
        var failures = new List<string>(); var count = 0;
        var entries = Encoding.UTF8.GetString(Git(root, "ls-files", "--stage", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var hasAssetRipper = false; var hasModules = false;
        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('\t');
            var metadata = entry[..separator].Split(' '); var path = entry[(separator + 1)..];
            count++;
            if (metadata[2] != "0") { failures.Add(path + ": unmerged entry"); continue; }
            if (metadata[0] == "160000")
            {
                if (path == AssetRipperIntegration.SubmodulePath && metadata[1] == AssetRipperIntegration.UpstreamCommit) hasAssetRipper = true;
                else failures.Add(path + ": unapproved submodule or revision");
                continue;
            }
            if (metadata[0] is not ("100644" or "100755")) { failures.Add(path + ": symlink or unsupported entry"); continue; }
            if (!Allowed(path)) failures.Add(path + ": not an authored converter source/configuration/documentation path");
            var bytes = Git(root, "cat-file", "blob", metadata[1]);
            try
            {
                var content = new UTF8Encoding(false, true).GetString(bytes);
                if (path == ".gitmodules")
                {
                    hasModules = true;
                    if (!ApprovedModules(content)) failures.Add(".gitmodules: only the pinned public AssetRipper submodule is allowed");
                }
            }
            catch (DecoderFallbackException) { failures.Add(path + ": binary content"); }
            if (bytes.Contains((byte)0) || bytes.Length > 1024 * 1024) failures.Add(path + ": binary or oversized content");
        }
        if (hasModules != hasAssetRipper) failures.Add("AssetRipper gitlink and .gitmodules must be staged together at the approved pin");
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine($"Publication: {count} staged files, {failures.Count} violations. New source requires provenance review.");
        return failures.Count == 0 ? 0 : 1;
    }
    internal static bool ApprovedModules(string content)
    {
        var lines = content.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToArray();
        return lines.SequenceEqual(new[]
        {
            $"[submodule \"{AssetRipperIntegration.SubmodulePath}\"]",
            "path = " + AssetRipperIntegration.SubmodulePath,
            "url = " + AssetRipperIntegration.UpstreamUrl
        }, StringComparer.Ordinal);
    }
    public static bool Allowed(string path)
    {
        if (path.Any(char.IsControl)) return false;
        if (path.Split('/').Any(p => p is "bin" or "obj" or "build" or "Library" or "recovered")) return false;
        if (path == ".github/workflows/ci.yml") return true;
        if (path == "src/H3VR.Converter/AssetRipper.ShaderExporter.patch") return true;
        if (new[] { "LICENSE", "README.md", ".gitignore", ".gitmodules", ".gitattributes", ".editorconfig", "Directory.Build.props", "global.json", "H3VR.Converter.sln" }.Contains(path)) return true;
        if (path.StartsWith("docs/", StringComparison.Ordinal)) return Path.GetExtension(path) == ".md";
        if (new[] { "src/", "tools/", "runtime/", "editor/", "tests/" }.Any(p => path.StartsWith(p, StringComparison.Ordinal)))
            return Path.GetExtension(path) is ".cs" or ".csproj";
        return false;
    }
}
