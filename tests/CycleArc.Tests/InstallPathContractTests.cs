using System.Text.RegularExpressions;

namespace CycleArc.Tests;

/// <summary>
/// The development single-file build replaces itself in one directory; a packaged
/// installation is Velopack's and owns another. Both want a file called CycleArc.exe at the
/// root of theirs, so the two must never be the same directory - which they were when the
/// managed new-install default moved under Programs.
///
/// DesktopBootstrap lives in the Windows-only desktop project, so the three declarations are
/// read from source and compared as the paths they build.
/// </summary>
public sealed class InstallPathContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // DesktopBootstrap.InstallDirectory: LocalApplicationData, "Programs", "<name>"
    private static string DevelopmentDirectory()
    {
        var text = Read("src", "CycleArc", "Services", "DesktopBootstrap.cs");
        var match = Regex.Match(text,
            @"InstallDirectory\s*=>\s*Path\.Combine\(\s*[^;]*?LocalApplicationData\)\s*,\s*""(?<a>[^""]+)""\s*,\s*""(?<b>[^""]+)""");
        Assert.True(match.Success, "DesktopBootstrap.InstallDirectory is no longer a two-segment LocalApplicationData path.");
        return Combine(match.Groups["a"].Value, match.Groups["b"].Value);
    }

    // InstallTargets.DefaultRoot: the managed default for a brand new installation.
    private static string ManagedDefault()
    {
        var text = Read("src", "CycleArc.Setup", "InstallTarget.cs");
        var match = Regex.Match(text,
            @"DefaultRoot\s*=>\s*Path\.Combine\(\s*[^;]*?LocalApplicationData\)\s*,\s*""(?<a>[^""]+)""\s*,\s*""(?<b>[^""]+)""");
        Assert.True(match.Success, "InstallTargets.DefaultRoot is no longer a two-segment LocalApplicationData path.");
        return Combine(match.Groups["a"].Value, match.Groups["b"].Value);
    }

    // dev-run.ps1 installs the development build into $LocalDir. The value is the last
    // single-quoted segment on that line, after the LocalApplicationData lookup.
    private static string DevRunLocalDirectory()
    {
        var line = File.ReadAllLines(Path.Combine(RepoRoot, "dev-run.ps1"))
            .FirstOrDefault(candidate => candidate.TrimStart().StartsWith("$LocalDir", StringComparison.Ordinal));
        Assert.False(line is null, "dev-run.ps1 no longer sets $LocalDir.");
        var quoted = line!.Split('\'');
        Assert.True(quoted.Length >= 3, "dev-run.ps1's $LocalDir has no quoted path segment.");
        return Normalize(quoted[^2]);
    }

    [Fact]
    public void DevelopmentAndManagedRootsAreDifferentDirectories()
    {
        var development = DevelopmentDirectory();
        var managed = ManagedDefault();
        Assert.NotEqual(managed, development);
        // The former managed root must stay clear of the development build too.
        Assert.NotEqual(Normalize("CycleArc"), development);
    }

    [Fact]
    public void DevRunInstallsWhereTheDesktopReplacesItself()
    {
        Assert.Equal(DevelopmentDirectory(), DevRunLocalDirectory());
    }

    // A directory holding only the development build is not a managed installation, so
    // detection must not adopt it and install over it.
    [Fact]
    public void ADevelopmentDirectoryIsNotMistakenForAManagedInstall()
    {
        var work = Directory.CreateTempSubdirectory("cyclearc-dev-root-");
        try
        {
            File.WriteAllText(Path.Combine(work.FullName, "CycleArc.exe"), "single-file build");
            File.WriteAllText(Path.Combine(work.FullName, "tray-migration.json"), "{}");
            Assert.False(LooksManaged(work.FullName));

            Directory.CreateDirectory(Path.Combine(work.FullName, "current"));
            File.WriteAllText(Path.Combine(work.FullName, "current", "CycleArc.exe"), "current");
            File.WriteAllText(Path.Combine(work.FullName, "Update.exe"), "updater");
            Assert.True(LooksManaged(work.FullName));
        }
        finally
        {
            try { work.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // The rule the setup window and the PowerShell helpers both apply.
    private static bool LooksManaged(string root) =>
        File.Exists(Path.Combine(root, "current", "CycleArc.exe")) && File.Exists(Path.Combine(root, "Update.exe"));

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

    private static string Combine(string first, string second) => Normalize(first + "/" + second);

    private static string Normalize(string value) =>
        value.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Trim('/')
            .ToLowerInvariant();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CycleArc.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (CycleArc.sln).");
    }
}
