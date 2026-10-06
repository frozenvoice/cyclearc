using System.Diagnostics;

namespace CycleArc.Setup;

/// <summary>
/// Coordinates the interactive installer's handoff to build-local. A direct launch has no
/// acknowledgement path and therefore keeps the shipped installer's original behaviour.
/// When build-local supplies a fresh path, the parent owns stopping the old desktop and must
/// explicitly acknowledge that it is ready before the embedded engine is started.
/// </summary>
internal static class SetupDesktopPreparation
{
    public const string AcknowledgementVariable = "CYCLEARC_SETUP_DESKTOP_ACK_FILE";
    private const int MaxAcknowledgementBytes = 128;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    internal enum ReadOutcome
    {
        Missing,
        Ready,
        Failed,
        Invalid,
        Error,
    }

    internal sealed record Result(bool Ready, string? Detail)
    {
        public static Result Proceed() => new(true, null);
        public static Result Stop(string detail) => new(false, detail);
    }

    /// <summary>
    /// Reports preparation, then waits for the parent to write exactly <c>ready</c>. The
    /// acknowledgement is deliberately one-way: this process never writes it, so an old file
    /// cannot be refreshed by the child and a fresh parent path cannot be replayed accidentally.
    /// </summary>
    public static Result WaitForParent(
        string? acknowledgementPath = null,
        Action<string>? reportState = null,
        Func<string, ReadOutcome>? readAcknowledgement = null,
        Action<TimeSpan>? wait = null,
        Func<TimeSpan>? elapsed = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Result.Stop("Desktop preparation was cancelled before the existing CycleArc desktop was stopped.");

        acknowledgementPath ??= Environment.GetEnvironmentVariable(AcknowledgementVariable);
        if (string.IsNullOrWhiteSpace(acknowledgementPath))
            return Result.Proceed();

        reportState ??= SetupState.Report;
        readAcknowledgement ??= ReadAcknowledgement;
        wait ??= static duration => Thread.Sleep(duration);
        var stopwatch = Stopwatch.StartNew();
        elapsed ??= () => stopwatch.Elapsed;
        timeout ??= DefaultTimeout;
        if (timeout.Value < TimeSpan.Zero)
            return Result.Stop("Desktop preparation acknowledgement timeout is invalid.");

        reportState(SetupState.PreparingDesktop);
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return Result.Stop("Desktop preparation was cancelled while waiting for the existing CycleArc desktop.");
            if (elapsed() >= timeout.Value)
                return Result.Stop("Timed out waiting for the parent process to prepare the existing CycleArc desktop.");

            ReadOutcome outcome;
            try
            {
                outcome = readAcknowledgement(acknowledgementPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Result.Stop("Could not read the desktop preparation acknowledgement: " + ex.Message);
            }

            if (cancellationToken.IsCancellationRequested)
                return Result.Stop("Desktop preparation was cancelled while waiting for the existing CycleArc desktop.");

            switch (outcome)
            {
                case ReadOutcome.Ready:
                    return Result.Proceed();
                case ReadOutcome.Failed:
                    return Result.Stop("The parent process could not prepare the existing CycleArc desktop.");
                case ReadOutcome.Invalid:
                    return Result.Stop("The desktop preparation acknowledgement was invalid.");
                case ReadOutcome.Error:
                    return Result.Stop("Could not read the desktop preparation acknowledgement.");
                case ReadOutcome.Missing:
                    break;
                default:
                    return Result.Stop("The desktop preparation acknowledgement was invalid.");
            }

            var remaining = timeout.Value - elapsed();
            if (remaining <= TimeSpan.Zero)
                return Result.Stop("Timed out waiting for the parent process to prepare the existing CycleArc desktop.");
            wait(remaining < PollInterval ? remaining : PollInterval);
        }
    }

    private static ReadOutcome ReadAcknowledgement(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[MaxAcknowledgementBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }

            if (count == 0) return ReadOutcome.Missing;
            if (count > MaxAcknowledgementBytes) return ReadOutcome.Invalid;
            var text = new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, count);
            return text switch
            {
                "ready" => ReadOutcome.Ready,
                "failed" => ReadOutcome.Failed,
                _ => ReadOutcome.Invalid,
            };
        }
        catch (FileNotFoundException) { return ReadOutcome.Missing; }
        catch (DirectoryNotFoundException) { return ReadOutcome.Missing; }
        catch (System.Text.DecoderFallbackException) { return ReadOutcome.Invalid; }
        catch (IOException) { return ReadOutcome.Error; }
        catch (UnauthorizedAccessException) { return ReadOutcome.Error; }
        catch (ArgumentException) { return ReadOutcome.Error; }
        catch (NotSupportedException) { return ReadOutcome.Error; }
    }
}
