using System.Globalization;
using System.Text;
using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

/// <summary>Headless entry point in the same executable; stdin is never logged or saved.</summary>
public static class ClaudeStatusLineCommand
{
    public const string Argument = "--claude-statusline";
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(string[] args, Stream input, TextWriter output,
        CodexAccountStore accounts, IClock? clock = null, CancellationToken token = default)
    {
        if (args is not [Argument, var id] || !Guid.TryParseExact(id, "N", out _) || !accounts.ContainsClaude(id))
        {
            await output.WriteLineAsync("Claude | CycleArc profile unavailable").ConfigureAwait(false);
            return 2;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Deadline);
        // Capture receipt order before reading, so an older slow invocation cannot replace
        // a sample from a newer invocation. This is a local receipt time, not an API timestamp.
        var received = (clock ?? SystemClock.Instance).UtcNow;
        try
        {
            var bytes = await ReadBoundedAsync(input, deadline.Token).ConfigureAwait(false);
            var connection = new ClaudeConnectionStore(accounts, id).Read();
            // Automatically bound accounts require the bridge's official CLI identity check.
            // An old copied command must not bypass that check after the account is connected.
            if (connection.Binding is not null || connection.Unavailable)
            {
                await output.WriteLineAsync(Format(new(ClaudeInputStatus.Missing))).ConfigureAwait(false);
                return 0;
            }
            var parsed = ClaudeStatusLineParser.Parse(bytes);
            var store = new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(id), id);
            await store.RecordForUnboundProfileAsync(parsed, received, accounts, deadline.Token).ConfigureAwait(false);
            await output.WriteLineAsync(Format(parsed)).ConfigureAwait(false);
            return parsed.Status == ClaudeInputStatus.Malformed ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException)
        {
            // No exception text: stdin and paths may contain unrelated private session data.
            await output.WriteLineAsync("Claude | CycleArc unavailable").ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(chunk.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > ClaudeStatusLineParser.MaxInputBytes) return [];
            buffer.Write(chunk, 0, count);
        }
    }

    public static string SettingsJson(string executable, string profileId, string? dataRoot = null)
    {
        if (!Guid.TryParseExact(profileId, "N", out _) || !Path.IsPathFullyQualified(executable)
            || executable.Any(char.IsControl) || (dataRoot is not null
                && (!Path.IsPathFullyQualified(dataRoot) || dataRoot.Any(char.IsControl))))
            throw new ArgumentException("Invalid statusLine command.");
        // Claude Code uses Git Bash when installed and PowerShell otherwise. The encoded
        // PowerShell command is shell-neutral and quotes even spaces, apostrophes, $, and `
        // in an installation path. The output pipeline makes Windows wait for the WinExe.
        var path = Path.GetFullPath(executable).Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var rootArgument = dataRoot is null ? "" : " '--data-root' '"
            + Path.GetFullPath(dataRoot).Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); "
            + "$OutputEncoding = [Console]::InputEncoding; "
            + "$input | & '" + path + "' '" + Argument + "' '" + profileId
            + "'" + rootArgument + " | ForEach-Object { $_ }; exit $LASTEXITCODE";
        var command = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return JsonSerializer.Serialize(new { statusLine = new { type = "command", command } },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Percent(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    internal static string Format(ClaudeStatusLineResult parsed)
    {
        var parts = new List<string> { "Claude" };
        if (parsed.FiveHour is { } five) parts.Add("5h " + Percent(five.UsedPercentage));
        if (parsed.SevenDay is { } week) parts.Add("7d " + Percent(week.UsedPercentage));
        if (parts.Count == 1) parts.Add("--");
        return string.Join(" | ", parts);
    }
}
