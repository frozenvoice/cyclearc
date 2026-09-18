using System.Diagnostics;
using System.Reflection;

namespace CycleArc.Setup;

internal enum EngineOutcome { Succeeded, Failed }

/// <param name="LogPath">Where the log really is, or empty when none could be written.</param>
/// <param name="LogError">
/// Why no log exists, when that is the case. Kept apart from <paramref name="Detail"/> so a
/// logging problem never overwrites the installation's own error.
/// </param>
internal sealed record EngineResult(
    EngineOutcome Outcome, int ExitCode, string LogPath, string? Detail, string? LogError = null)
{
    public bool Succeeded => Outcome == EngineOutcome.Succeeded;
    public bool HasLog => !string.IsNullOrWhiteSpace(LogPath) && File.Exists(LogPath);
}

/// <summary>
/// Runs the embedded Velopack installer. The engine keeps doing the installation; this only
/// hides its one-click behaviour behind the confirmation and progress screens by running it
/// with --silent, and starts the app afterwards only if the person asked for that.
/// </summary>
internal static class EngineRunner
{
    private const string ResourceName = "CycleArc.Setup.Engine.exe";

    public static bool HasEngine
    {
        get
        {
            using var stream = typeof(EngineRunner).Assembly.GetManifestResourceStream(ResourceName);
            return stream is not null;
        }
    }

    /// <param name="logPath">
    /// Where the engine writes its log. A caller that passed --log gets exactly that file, so
    /// a build script finds the log where it asked for it; otherwise one is made alongside the
    /// extracted engine and copied next to the installation afterwards.
    /// </param>
    public static EngineResult Install(string directory, CancellationToken token, string? logPath = null)
    {
        var work = Path.Combine(Path.GetTempPath(), "CycleArc-setup-" + Guid.NewGuid().ToString("N"));
        string? logError = null;
        try
        {
            Directory.CreateDirectory(work);
            // Settled before the engine runs and never moved afterwards, so the path reported
            // on a failure is the path the log is actually at.
            logPath = PrepareLogPath(logPath, out logError);
            var enginePath = Path.Combine(work, "CycleArc-Setup-engine.exe");
            if (!TryExtract(enginePath, out var extractError))
                return new EngineResult(EngineOutcome.Failed, -1, logPath ?? "", extractError, logError);

            var start = new ProcessStartInfo(enginePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = work,
            };
            // --silent so the engine's own one-click UI never appears behind these screens.
            // It also stops the engine launching the app, which the completion page decides.
            start.ArgumentList.Add("--silent");
            start.ArgumentList.Add("--installto");
            start.ArgumentList.Add(directory);
            // Only when there is somewhere to write it. With no usable location the engine
            // is run without --log rather than being handed a path that does not work.
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                start.ArgumentList.Add("--log");
                start.ArgumentList.Add(logPath);
            }

            using var process = Process.Start(start);
            if (process is null)
                return new EngineResult(EngineOutcome.Failed, -1, logPath ?? "", "The installer engine did not start.", logError);

            // No wall-clock limit here: this is the real installation, and the time a person
            // spends on the confirmation screen is not part of it.
            while (!process.WaitForExit(200))
            {
                if (!token.IsCancellationRequested) continue;
                // Cancellation is only offered before the engine starts; if it ever arrives
                // mid-install, letting it finish is safer than leaving a half-written install.
                token.ThrowIfCancellationRequested();
            }

            var exit = process.ExitCode;
            if (exit != 0)
                return new EngineResult(EngineOutcome.Failed, exit, logPath ?? "",
                    logPath is null ? null : ReadTail(logPath), logError);
            if (!InstallTargets.Looks(directory))
                return new EngineResult(EngineOutcome.Failed, exit, logPath ?? "",
                    "The installer finished but no installation is present at " + directory + ".", logError);
            return new EngineResult(EngineOutcome.Succeeded, 0, logPath ?? "", null, logError);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new EngineResult(EngineOutcome.Failed, -1, logPath ?? "", ex.Message, logError);
        }
        finally
        {
            // Only the extracted engine goes. Every log location - the requested one, the
            // default and every fallback - is outside this directory, so nothing has to be
            // rescued from it and a failed rescue cannot lose the log.
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Starts the installed launcher. Only called when the completion page asks.</summary>
    public static bool TryLaunch(string directory, out string? error)
    {
        error = null;
        try
        {
            var launcher = InstallTargets.Launcher(directory);
            if (!File.Exists(launcher)) { error = "The installed launcher is missing: " + launcher; return false; }
            using var started = Process.Start(new ProcessStartInfo(launcher)
            {
                UseShellExecute = true,
                WorkingDirectory = directory,
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryExtract(string path, out string? error)
    {
        error = null;
        try
        {
            using var stream = typeof(EngineRunner).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                error = "This installer was built without its engine and cannot install anything.";
                return false;
            }
            using var file = File.Create(path);
            stream.CopyTo(file);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The last of the engine log, so a failure names a cause and not just a code.</summary>
    private static string? ReadTail(string logPath, int lines = 12)
    {
        try
        {
            if (!File.Exists(logPath)) return null;
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var tail = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                tail.Enqueue(line.Trim());
                if (tail.Count > lines) tail.Dequeue();
            }
            return tail.Count == 0 ? null : string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The default log location, for callers that want to report it.</summary>
    public static string DefaultLogDirectory => SetupLogPaths.DefaultDirectory;

    public static string DefaultLogPath => SetupLogPaths.DefaultPath;

    /// <summary>
    /// Picks the log path and proves it writable before the engine runs. Every candidate is
    /// outside the install target and outside the work directory this run deletes.
    /// </summary>
    private static string? PrepareLogPath(string? requested, out string? logError) =>
        SetupLogPaths.Prepare(requested, out logError);
}
