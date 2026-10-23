using System.Diagnostics;

namespace H3VR.Converter;

public static class Processes
{
    public static async Task Run(string file, IEnumerable<string> arguments, string cwd, string? log = null, CancellationToken cancellation = default)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        Console.WriteLine($"> {file} {string.Join(' ', info.ArgumentList.Select(a => a.Contains(' ') ? '"' + a + '"' : a))}");
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + file);
        using var writer = log is null ? null : new StreamWriter(log);
        var sync = new SemaphoreSlim(1, 1);
        async Task Pump(StreamReader stream)
        {
            while (await stream.ReadLineAsync(cancellation) is { } line)
            {
                await sync.WaitAsync(cancellation);
                try { Console.WriteLine(line); if (writer is not null) await writer.WriteLineAsync(line); }
                finally { sync.Release(); }
            }
        }
        try { await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError), process.WaitForExitAsync(cancellation)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(file)} exited with code {process.ExitCode}. See {log ?? "the output above"}.");
    }
}
