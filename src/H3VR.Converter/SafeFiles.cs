using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace H3VR.Converter;

public static partial class SafeFiles
{
    [LibraryImport("libc", EntryPoint = "clonefile", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int CloneFile(string source, string destination, int flags);
    public static string Canonical(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path)!;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            root = Path.Combine(root, part);
            FileSystemInfo item = Directory.Exists(root) ? new DirectoryInfo(root) : new FileInfo(root);
            if (item.LinkTarget is not null) root = Canonical(item.ResolveLinkTarget(true)!.FullName);
        }
        return Path.TrimEndingDirectorySeparator(root);
    }
    public static bool Contains(string parent, string child)
    {
        parent = Canonical(parent); child = Canonical(child);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return child.Equals(parent, comparison) || child.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }
    public static void RequireSeparate(string source, string destination)
    {
        if (Contains(source, destination) || Contains(destination, source))
            throw new InvalidDataException($"Input/output paths overlap: {source} and {destination}");
    }
    public static string Inside(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
            throw new InvalidDataException("An integration path must be relative and cannot traverse parents: " + relative);
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!Contains(root, result) || result == Canonical(root)) throw new InvalidDataException("Path escapes destination: " + relative);
        return result;
    }
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public static string HashText(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static IEnumerable<string> Files(string root)
    {
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null) throw new InvalidDataException("Symlink input is not supported: " + entry.FullName);
            if (entry is DirectoryInfo) { foreach (var f in Files(entry.FullName)) yield return f; }
            else yield return entry.FullName;
        }
    }
    public static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) throw new IOException("Refusing to overwrite: " + destination);
        if (!OperatingSystem.IsMacOS() || CloneFile(source, destination, 0) != 0) File.Copy(source, destination);
    }
    public static void CopyTree(string source, string destination)
    {
        RequireSeparate(source, destination);
        if (Directory.Exists(destination)) throw new IOException("Destination already exists: " + destination);
        Directory.CreateDirectory(destination);
        foreach (var file in Files(source)) Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
    public static void AtomicJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(value, ConversionConfig.Json) + "\n");
        File.Move(temporary, path, true);
    }
}
