using CycleArc.Setup;

namespace CycleArc.Tests;

/// <summary>
/// The installer deletes its per-run work directory when Install returns. A log written in
/// there is therefore gone exactly when it is wanted - a failure caused by the install target
/// itself. These drive the production path-choosing code, with the write probe injected so no
/// real folder's permissions are changed.
/// </summary>
public sealed class SetupLogPathTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("cyclearc-log-paths-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A. No candidate may live inside a per-run work directory. Install names that
    //    directory "CycleArc-setup-<guid>" and deletes it whole when it returns, so a log
    //    written under any directory of that shape is gone exactly when it is wanted.
    [Fact]
    public void NoCandidateIsInsideAPerRunWorkDirectory()
    {
        foreach (var candidate in SetupLogPaths.Candidates(null))
        {
            foreach (var segment in Path.GetFullPath(candidate).Split(Path.DirectorySeparatorChar))
            {
                // Exactly Install's shape: CycleArc-setup- followed by a 32-character GUID.
                // The fixed CycleArc-setup-logs directory shares the prefix and is not one.
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(
                        segment, "^CycleArc-setup-[0-9a-fA-F]{32}$"),
                    $"'{candidate}' is under a per-run work directory, which Install deletes.");
            }
        }
    }

    // Candidates cannot even be told where the work directory is, so none can be put in it.
    [Fact]
    public void CandidatesDoNotTakeTheWorkDirectory()
    {
        var parameters = typeof(SetupLogPaths)
            .GetMethod("Candidates")!.GetParameters()
            .Select(parameter => parameter.Name)
            .ToArray();
        Assert.DoesNotContain("work", parameters, StringComparer.OrdinalIgnoreCase);
    }

    // A (continued). With the default unusable, the chosen fallback is still readable after the
    // work directory has been removed - which is what the old fallback failed to be.
    [Fact]
    public void AFallbackSurvivesTheWorkDirectoryBeingDeleted()
    {
        var work = Path.Combine(_work, "work");
        Directory.CreateDirectory(work);
        var fallbackRoot = Path.Combine(_work, "fallback");
        Directory.CreateDirectory(fallbackRoot);
        var fallback = Path.Combine(fallbackRoot, "CycleArc-install.log");

        // The default is refused; the next candidate is accepted and actually written.
        var chosen = SetupLogPaths.Prepare(null, out var error, candidate =>
        {
            if (candidate == SetupLogPaths.DefaultPath) return false;
            File.WriteAllText(fallback, "engine log for this run");
            return true;
        });

        Assert.Null(error);
        Assert.NotNull(chosen);
        Assert.NotEqual(SetupLogPaths.DefaultPath, chosen);
        Assert.False(IsUnder(chosen!, work));

        // Install's finally removes the work directory; the log is not in it.
        Directory.Delete(work, recursive: true);
        Assert.True(File.Exists(fallback), "The fallback log did not survive the cleanup.");
        Assert.Equal("engine log for this run", File.ReadAllText(fallback));
    }

    // B. An explicit --log is honoured and never replaced by a fallback.
    [Fact]
    public void AnExplicitLogPathIsTheOnlyCandidate()
    {
        var requested = Path.Combine(_work, "asked-for.log");
        var candidates = SetupLogPaths.Candidates(requested);
        Assert.Single(candidates);
        Assert.Equal(Path.GetFullPath(requested), candidates[0]);

        var chosen = SetupLogPaths.Prepare(requested, out var error);
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(requested), chosen);
        Assert.True(File.Exists(chosen));
    }

    [Fact]
    public void AnUnwritableExplicitLogPathIsNotSilentlyReplaced()
    {
        var requested = Path.Combine(_work, "refused.log");
        var chosen = SetupLogPaths.Prepare(requested, out var error, _ => false);
        Assert.Null(chosen);
        Assert.NotNull(error);
        Assert.Contains(Path.GetFullPath(requested), error!, StringComparison.Ordinal);
    }

    // C. Nothing writable anywhere is reported as such, with the places it tried.
    [Fact]
    public void NoWritableLocationIsReportedRatherThanInvented()
    {
        var attempted = new List<string>();
        var chosen = SetupLogPaths.Prepare(null, out var error, candidate =>
        {
            attempted.Add(candidate);
            return false;
        });

        Assert.Null(chosen);
        Assert.NotNull(error);
        Assert.Contains("No installer log could be written", error!, StringComparison.Ordinal);
        // Every candidate was tried before giving up, not just the first.
        Assert.True(attempted.Count >= 2, $"Only {attempted.Count} candidate(s) were tried.");
    }

    // The default location itself is outside the install target and the data directory.
    [Fact]
    public void TheDefaultLocationIsNeitherTheInstallTargetNorTheDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var forbidden in new[]
        {
            Path.Combine(localAppData, "Programs", "CycleArc"),
            Path.Combine(localAppData, "CycleArc"),
            Path.Combine(localAppData, "ProMeter"),
        })
        {
            Assert.False(IsUnder(SetupLogPaths.DefaultPath, forbidden),
                $"The default log is under {forbidden}.");
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var baseRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
