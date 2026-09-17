namespace CycleArc.Services;

/// <summary>Desktop launch intent never changes the precedence of a running build.</summary>
public sealed record DesktopLaunchOptions(bool Replace, bool Autorun, bool Show, bool Quiet,
    bool StatusOnly, bool ShutdownOnly, string? ExpectedHash, string? ExpectedVersion)
{
    public static DesktopLaunchOptions Parse(IReadOnlyList<string> args)
    {
        bool replace = false, autorun = false, show = false, quiet = false, status = false, shutdown = false;
        string? hash = null, version = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--replace": case "--install": replace = true; break;
                case "--autorun": autorun = true; break;
                case "--show": case "--accounts": show = true; break;
                case "--quiet": quiet = true; break;
                case "--desktop-status": status = true; break;
                case "--desktop-shutdown": shutdown = true; break;
                case "--expected-sha256" when i + 1 < args.Count: hash = args[++i]; break;
                case "--expected-version" when i + 1 < args.Count: version = args[++i]; break;
                default: throw new ArgumentException("Unknown or incomplete CycleArc launch argument.");
            }
        }
        if ((replace && autorun)
            || (status && (replace || autorun || show || shutdown))
            || (shutdown && (replace || autorun || show))
            || (!replace && (hash is not null || version is not null)))
            throw new ArgumentException("Conflicting CycleArc launch arguments.");
        if (replace && (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)
            || !System.Version.TryParse(version, out _)))
            throw new ArgumentException("Installation requires the selected executable's SHA-256 and file version.");
        return new(replace, autorun, show, quiet, status, shutdown, hash, version);
    }

    // Inspect only the first argument. Headless callback payloads must never be
    // mistaken for a desktop process during the one-time legacy migration.
    public static bool IsLegacyDesktopCommand(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;
        var text = commandLine.AsSpan().TrimStart();
        if (string.IsNullOrEmpty(ReadToken(ref text))) return false;
        var first = ReadToken(ref text);
        return first is not (null or "--claude-statusline" or "--claude-statusline-bridge"
            or "--claude-stop-failure-bridge" or "--apply-update" or "--replace" or "--install"
            or "--desktop-status" or "--desktop-shutdown");
    }

    private static string? ReadToken(ref ReadOnlySpan<char> text)
    {
        text = text.TrimStart();
        if (text.IsEmpty) return "";
        if (text[0] == '"')
        {
            text = text[1..];
            var end = text.IndexOf('"');
            if (end < 0) { text = []; return null; }
            var value = text[..end].ToString();
            text = text[(end + 1)..];
            return value;
        }
        var length = 0;
        while (length < text.Length && !char.IsWhiteSpace(text[length])) length++;
        var token = text[..length].ToString();
        text = text[length..];
        return token;
    }
}
