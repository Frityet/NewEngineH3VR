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
        foreach (var entry in entries)
        {
            var parts = entry.Split('\t'); var metadata = parts[0].Split(' '); var path = parts[1];
            if (metadata[0] is not ("100644" or "100755") || metadata[2] != "0") failures.Add(path + ": symlink/submodule/unmerged entry");
            if (!Allowed(path)) failures.Add(path + ": not an authored converter source/configuration/documentation path");
            var bytes = Git(root, "cat-file", "blob", metadata[1]);
            try { new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { failures.Add(path + ": binary content"); }
            if (bytes.Contains((byte)0) || bytes.Length > 1024 * 1024) failures.Add(path + ": binary or oversized content");
            count++;
        }
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine($"Publication: {count} staged files, {failures.Count} violations. New source requires provenance review.");
        return failures.Count == 0 ? 0 : 1;
    }
    public static bool Allowed(string path)
    {
        if (path.Split('/').Any(p => p is "bin" or "obj" or "build" or "Library" or "recovered")) return false;
        if (path == ".github/workflows/ci.yml") return true;
        if (path == "src/H3VR.Converter/AssetRipper.ShaderExporter.patch") return true;
        if (new[] { "LICENSE", "README.md", ".gitignore", ".gitattributes", ".editorconfig", "Directory.Build.props", "global.json", "H3VR.Converter.sln" }.Contains(path)) return true;
        if (path.StartsWith("docs/", StringComparison.Ordinal)) return Path.GetExtension(path) == ".md";
        if (new[] { "src/", "tools/", "runtime/", "editor/", "tests/" }.Any(p => path.StartsWith(p, StringComparison.Ordinal)))
            return Path.GetExtension(path) is ".cs" or ".csproj";
        return false;
    }
}
