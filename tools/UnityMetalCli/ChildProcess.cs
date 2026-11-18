using System.Diagnostics;

namespace UnityMetalCli;

internal static class ChildProcess
{
    internal readonly record struct Result(int ExitCode, string Output);

    internal static async Task<Result> RunAsync(
        string file, IEnumerable<string> arguments, CancellationToken cancellation,
        string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null, bool captureOutput = false)
    {
        cancellation.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = captureOutput
        };
        if (workingDirectory is not null)
            info.WorkingDirectory = workingDirectory;
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var item in environment)
                if (item.Value is null)
                    info.Environment.Remove(item.Key);
                else
                    info.Environment[item.Key] = item.Value;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (timeout.HasValue)
            deadline.CancelAfter(timeout.Value);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + file);
        Task<string> read = captureOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await read);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) {}
            }
            await process.WaitForExitAsync();
            _ = await read;
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(file)} timed out after {timeout!.Value.TotalSeconds:g} seconds.");
        }
    }

    internal static async Task<string> CheckedAsync(
        string file, IEnumerable<string> arguments, CancellationToken cancellation,
        bool captureOutput = false, TimeSpan? timeout = null)
    {
        var result = await RunAsync(file, arguments, cancellation, timeout: timeout, captureOutput: captureOutput);
        if (result.ExitCode != 0)
            throw new IOException($"{Path.GetFileName(file)} exited with code {result.ExitCode}.");
        return result.Output;
    }
}
