using System.Text.RegularExpressions;

namespace CycleArc.Tests;

/// <summary>
/// A person who double-clicks the installer passes no --log. The log must still survive the
/// case that most needs diagnosing: the installation target itself being unwritable. Writing
/// the log into the install directory, or into the temporary work directory that is deleted on
/// the way out, loses it exactly then.
///
/// EngineRunner is in the Windows-only Native AOT project, so the contract is read from its
/// source. The behaviour it describes is exercised end to end by scripts/Verify-SetupUi.ps1 on
/// a disposable runner.
/// </summary>
public sealed class SetupLogRetentionTests
{
    private static readonly string Source = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "CycleArc.Setup", "EngineRunner.cs"));

    [Fact]
    public void TheDefaultLogIsOutsideTheInstallTargetAndTheWorkDirectory()
    {
        // Under the user's temp root, which is neither the install target nor the per-run work
        // directory that is deleted at the end of Install.
        Assert.Matches(new Regex(@"DefaultLogDirectory\s*=>\s*Path\.Combine\(\s*Path\.GetTempPath\(\)"), Source);
        Assert.Contains("\"CycleArc-setup-logs\"", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogIsNotCopiedOutOfTheDeletedWorkDirectory()
    {
        // The old shape: write into work, copy to the install directory, ignore the failure,
        // delete work. A failing install target then took the log with it.
        Assert.DoesNotContain("TryKeepLog", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("KeptLogPath", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogPathIsProvenWritableBeforeTheEngineRuns()
    {
        // PrepareLogPath opens each candidate before use, so a path that is reported is a path
        // that was writable, and the fallback happens before the engine starts rather than after.
        Assert.Contains("PrepareLogPath", Source, StringComparison.Ordinal);
        Assert.Contains("FileMode.Create", Source, StringComparison.Ordinal);
        var prepare = Source.IndexOf("PrepareLogPath(string? requested", StringComparison.Ordinal);
        Assert.True(prepare > 0, "PrepareLogPath is no longer the shape this test describes.");
        var body = Source[prepare..];
        // An explicit --log is honoured and never silently replaced by the default.
        Assert.Contains("attempts.Add(Path.GetFullPath(requested!));", body, StringComparison.Ordinal);
        // The install directory is not one of the candidates.
        Assert.DoesNotContain("attempts.Add(directory", body, StringComparison.Ordinal);
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
