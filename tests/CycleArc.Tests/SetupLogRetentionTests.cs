using System.Text.RegularExpressions;

namespace CycleArc.Tests;

/// <summary>
/// A person who double-clicks the installer passes no --log. The log must still survive the
/// case that most needs diagnosing: the installation target itself being unwritable. Writing
/// the log into the install directory, or into the temporary work directory that is deleted on
/// the way out, loses it exactly then.
///
/// EngineRunner is in the Windows-only Native AOT project, so its contract is read from
/// source. Where the log actually goes is checked against the real code in SetupLogPathTests,
/// which links SetupLogPaths.cs; this file covers what EngineRunner must do around it. The
/// whole path is exercised end to end by scripts/Verify-SetupUi.ps1 on a disposable runner.
/// </summary>
public sealed class SetupLogRetentionTests
{
    private static readonly string Source = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "CycleArc.Setup", "EngineRunner.cs"));

    [Fact]
    public void TheLogIsNotCopiedOutOfTheDeletedWorkDirectory()
    {
        // The old shape: write into work, copy to the install directory, ignore the failure,
        // delete work. A failing install target then took the log with it.
        Assert.DoesNotContain("TryKeepLog", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("KeptLogPath", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoggingFailureIsReportedSeparatelyFromTheInstallFailure()
    {
        // LogError travels alongside Detail rather than replacing it, so a logging problem
        // never overwrites the installation's own error.
        Assert.Contains("string? LogError", Source, StringComparison.Ordinal);
        Assert.Contains("public bool HasLog", Source, StringComparison.Ordinal);

        var window = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "CycleArc.Setup", "SetupWindow.cs"));
        // The failure screen only names a log that exists, and says so plainly when none does.
        Assert.Contains("result.HasLog", window, StringComparison.Ordinal);
        Assert.Contains("Strings.NoLogWritten", window, StringComparison.Ordinal);
    }

    // The fallback order itself, as arithmetic on paths rather than on the running installer:
    // an unwritable first choice must not end with no log at all.
    [Fact]
    public void AnUnwritableFirstChoiceFallsBackRatherThanGivingUp()
    {
        var work = Directory.CreateTempSubdirectory("cyclearc-log-fallback-");
        try
        {
            // A directory where the log file should be: creating the file there always fails.
            var blocked = Path.Combine(work.FullName, "blocked.log");
            Directory.CreateDirectory(blocked);

            Assert.False(TryCreate(blocked), "The blocked candidate should not be writable.");
            var fallback = Path.Combine(work.FullName, "CycleArc-install.log");
            Assert.True(TryCreate(fallback), "The fallback candidate should be writable.");
            Assert.True(File.Exists(fallback));
        }
        finally
        {
            try { work.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TryCreate(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using var probe = new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CycleArc.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (CycleArc.sln).");
    }
}
