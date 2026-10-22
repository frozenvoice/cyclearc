namespace CycleArc.Services;

public sealed class AppLog
{
    private readonly object _gate = new();
    private readonly string _directory;
    private const long MaxBytes = 512 * 1024;
    /// <summary>Daily files older than this are removed when a log opens.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    public AppLog(string? directory = null, DateTimeOffset? now = null)
    {
        _directory = directory ?? AppPaths.Logs;
        Directory.CreateDirectory(_directory);
        RemoveExpired(_directory, now ?? DateTimeOffset.UtcNow);
    }

    // Only this log's own daily files (and their one rotated copy) are candidates; the date in
    // the name decides, so a copied or touched file is never mistaken for a new one.
    private static void RemoveExpired(string directory, DateTimeOffset now)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "cyclearc-*.log*"))
            {
                var name = Path.GetFileName(file);
                if (name.Length < "cyclearc-yyyyMMdd.log".Length
                    || !(name.EndsWith(".log", StringComparison.Ordinal) || name.EndsWith(".log.1", StringComparison.Ordinal))
                    || !DateTime.TryParseExact(name.Substring("cyclearc-".Length, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                    || now.UtcDateTime - day <= Retention)
                    continue;
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public string DirectoryPath => _directory;
    public string CurrentFile => Path.Combine(_directory, $"cyclearc-{DateTime.UtcNow:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null)
    {
        var detail = ex is null ? message : $"{message} :: {ex.GetType().Name}: {Sanitize(ex.Message)}";
        // The innermost cause and where it was thrown make a report actionable; frames hold only
        // method names, and the line is sanitized and bounded like every other.
        if (ex?.InnerException is { } inner)
        {
            while (inner.InnerException is not null) inner = inner.InnerException;
            detail += $" <- {inner.GetType().Name}: {Sanitize(inner.Message)}";
        }
        if (ex?.StackTrace is { Length: > 0 } stack)
            detail += " @ " + string.Join(" | ", stack.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(8));
        Write("ERROR", detail);
    }

    public void Http(string operation, int status, string? extra = null)
    {
        var suffix = string.IsNullOrWhiteSpace(extra) ? "" : " " + Sanitize(extra);
        Info($"http {operation} status={status}{suffix}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {Sanitize(message)}";
        lock (_gate)
        {
            var file = CurrentFile;
            // Logging runs inside failure and shutdown handlers; a full disk or a file held open
            // by another program must never turn a logged failure into a crash.
            try { File.AppendAllText(file, line + Environment.NewLine); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            RotateIfNeeded(file);
        }
    }

    private static void RotateIfNeeded(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Exists && info.Length > MaxBytes)
            {
                var archive = file + ".1";
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }

                File.Move(file, archive);
            }
        }
        catch
        {
            // logging must never throw
        }
    }

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var text = value;
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            "(?i)(authorization|cookie|session[-_ ]?token|access[-_ ]?token)\\s*[:=]\\s*.+",
            "$1=[redacted]");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            "Bearer\\s+[A-Za-z0-9._\\-]+",
            "Bearer [redacted]");
        if (text.Length > 2000)
        {
            text = text[..2000] + "…";
        }

        return text;
    }
}
