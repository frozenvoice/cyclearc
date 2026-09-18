namespace CycleArc.Setup;

/// <summary>
/// Chooses where the installer writes its engine log, and proves the choice is writable
/// before the engine starts.
///
/// Every candidate is outside two places on purpose. Not the installation directory: the
/// installation may be exactly what is failing, and a log written into it goes down with it.
/// Not the per-run work directory either: <c>EngineRunner.Install</c> deletes that whole
/// directory on the way out, so a fallback written there would be removed precisely when it
/// was needed. Neither is the ProMeter data directory, which this never touches.
///
/// The probing is injectable so the fallback order can be tested without changing permissions
/// on a real user's folders; production and the tests run this same code.
/// </summary>
internal static class SetupLogPaths
{
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "CycleArc-setup-logs");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "CycleArc-install.log");

    /// <summary>
    /// The candidates to try, in order. An explicit --log path is the only candidate when one
    /// was given: it is honoured, never silently replaced by a fallback.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? requested, string? uniqueName = null)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            try { return new[] { Path.GetFullPath(requested!) }; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Array.Empty<string>();
            }
        }

        var unique = string.IsNullOrWhiteSpace(uniqueName)
            ? "CycleArc-install-" + Guid.NewGuid().ToString("N") + ".log"
            : uniqueName!;
        var candidates = new List<string> { DefaultPath };
        // The temp root itself, in case only the subdirectory was the problem.
        candidates.Add(Path.Combine(Path.GetTempPath(), unique));
        // Then the places a per-user installation can normally write.
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.UserProfile,
        })
        {
            string? root = null;
            try { root = Environment.GetFolderPath(folder); }
            catch (ArgumentException) { }
            if (!string.IsNullOrWhiteSpace(root)) candidates.Add(Path.Combine(root!, unique));
        }

        return candidates;
    }

    /// <summary>Creates the file, so a path that is returned is a path that was writable.</summary>
    public static bool TryCreate(string path)
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

    /// <summary>
    /// The first writable candidate, or null with a reason when none is. Null is reported by
    /// the caller as "no log exists", never smoothed over into a path that is not there.
    /// </summary>
    public static string? Prepare(string? requested, out string? error, Func<string, bool>? tryCreate = null)
    {
        error = null;
        tryCreate ??= TryCreate;
        var attempted = new List<string>();
        foreach (var candidate in Candidates(requested))
        {
            attempted.Add(candidate);
            if (tryCreate(candidate)) return candidate;
        }

        error = attempted.Count == 0
            ? "No installer log could be written: the requested log path is not usable."
            : "No installer log could be written (tried " + string.Join("; ", attempted) + ").";
        return null;
    }
}
