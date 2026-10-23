using H3VR.Converter;
using System.Diagnostics;

namespace H3VR.Converter.Tests;

public static class PublicationTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("publication accepts the pinned AssetRipper gitlink without requiring its object locally", PinnedGitlinkWithoutObject);
        yield return ("publication rejects an unapproved submodule revision", WrongRevision);
        yield return ("publication rejects a second unrelated gitlink", OtherGitlink);
        yield return ("publication rejects a malicious submodule URL", MaliciousUrl);
        yield return ("publication rejects game binaries and disallowed game-source paths", GamePayloads);
        yield return ("publication checks the complete staged path including embedded tabs", TabbedPathCannotHideGameSource);
    }

    private static void PinnedGitlinkWithoutObject()
    {
        using var repo = new TempGit();
        repo.WriteModules(AssetRipperIntegration.UpstreamUrl);
        repo.StageModules();
        repo.StageGitlink(AssetRipperIntegration.SubmodulePath, AssetRipperIntegration.UpstreamCommit);
        Check(repo.GitExit("cat-file", "-e", AssetRipperIntegration.UpstreamCommit) != 0,
            "synthetic fixture unexpectedly contains the upstream submodule commit object");
        Check(Publication.Check(repo.Root) == 0,
            "approved .gitmodules + pinned gitlink was rejected when the gitlink object was not local");
    }

    private static void WrongRevision()
    {
        using var repo = new TempGit();
        repo.WriteModules(AssetRipperIntegration.UpstreamUrl);
        repo.StageModules();
        repo.StageGitlink(AssetRipperIntegration.SubmodulePath, new string('a', 40));
        Check(Publication.Check(repo.Root) != 0, "an AssetRipper gitlink at the wrong SHA was accepted");
    }

    private static void OtherGitlink()
    {
        using var repo = new TempGit();
        repo.WriteModules(AssetRipperIntegration.UpstreamUrl);
        repo.StageModules();
        repo.StageGitlink("extern/Unrelated", AssetRipperIntegration.UpstreamCommit);
        Check(Publication.Check(repo.Root) != 0, "an unrelated gitlink was accepted as the approved AssetRipper dependency");
    }

    private static void MaliciousUrl()
    {
        using var repo = new TempGit();
        repo.WriteModules("https://attacker.invalid/AssetRipper.git");
        repo.StageModules();
        repo.StageGitlink(AssetRipperIntegration.SubmodulePath, AssetRipperIntegration.UpstreamCommit);
        Check(Publication.Check(repo.Root) != 0, "a pinned path with an unapproved submodule URL was accepted");
    }

    private static void GamePayloads()
    {
        using var repo = new TempGit();
        repo.WriteModules(AssetRipperIntegration.UpstreamUrl);
        repo.StageModules();
        repo.StageGitlink(AssetRipperIntegration.SubmodulePath, AssetRipperIntegration.UpstreamCommit);
        repo.Write("game/h3vr_Data/Managed/Assembly-CSharp.dll", "binary\0game payload");
        repo.Write("game/Assets/Scripts/GameCode.cs", "public class GameCode {}\n");
        repo.Git("add", "game/h3vr_Data/Managed/Assembly-CSharp.dll", "game/Assets/Scripts/GameCode.cs");
        Check(Publication.Check(repo.Root) != 0, "game binaries or decompiled game source were accepted for publication");
        Check(!Publication.Allowed("game/Assets/Scripts/GameCode.cs") && !Publication.Allowed("game/h3vr_Data/Managed/Assembly-CSharp.dll"),
            "game content paths were added to the source publication allowlist");
    }

    private static void TabbedPathCannotHideGameSource()
    {
        using var repo = new TempGit();
        repo.WriteModules(AssetRipperIntegration.UpstreamUrl);
        repo.StageModules();
        repo.StageGitlink(AssetRipperIntegration.SubmodulePath, AssetRipperIntegration.UpstreamCommit);
        const string hiddenPath = "src/Allowed.cs\tgame-source.cs";
        repo.Write(hiddenPath, "// content whose actual path must still be audited\n");
        repo.Git("add", "--", hiddenPath);
        Check(Publication.Check(repo.Root) != 0, "a literal tab in a staged path hid a disallowed game-source filename");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TempGit : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "h3vr-publication-tests-" + Guid.NewGuid().ToString("N"));
        public TempGit()
        {
            Directory.CreateDirectory(Root);
            Git("init", "--quiet");
            Git("config", "user.email", "test@example.invalid");
            Git("config", "user.name", "Publication test");
        }
        public void WriteModules(string url) => Write(".gitmodules", $"[submodule \"{AssetRipperIntegration.SubmodulePath}\"]\n\tpath = {AssetRipperIntegration.SubmodulePath}\n\turl = {url}\n");
        public void StageModules() => Git("add", ".gitmodules");
        public void StageGitlink(string path, string sha) => Git("update-index", "--add", "--cacheinfo", $"160000,{sha},{path}");
        public void Write(string path, string content)
        {
            var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, content);
        }
        public void Git(params string[] args)
        {
            var status = GitExit(args);
            if (status != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({status})");
        }
        public int GitExit(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not start git for publication fixture.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            _ = error.GetAwaiter().GetResult();
            _ = output.GetAwaiter().GetResult();
            return process.ExitCode;
        }
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
    }
}
