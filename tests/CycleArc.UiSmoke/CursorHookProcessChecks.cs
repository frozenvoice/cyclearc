using System.Diagnostics;
using System.IO;
using System.Text;
using CycleArc.Providers.Cursor;

namespace CycleArc.UiSmoke;

/// <summary>
/// Runs the generated Cursor hook command against the production executable the way Cursor on
/// Windows delivers events: a UTF-8 byte order mark before the JSON, non-ASCII prompt text and a
/// console code page that is not UTF-8.
/// </summary>
internal static class CursorHookProcessChecks
{
    public static void Run(string? executable = null)
    {
        executable ??= Path.Combine(RepositoryRoot(), "src", "CycleArc", "bin", "Release",
            "net10.0-windows10.0.17763.0", "CycleArc.exe");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Build the production executable before checking Cursor hooks.");
        var root = Path.Combine(Path.GetTempPath(), "cyclearc-cursor-hook-test-" + Guid.NewGuid().ToString("N"));
        var data = Directory.CreateDirectory(Path.Combine(root, "A space O'Brien $x`")).FullName;
        try
        {
            var submit = Run(executable, data, CursorHookEvent.SubmitEvent, """
                {"conversation_id":"c","generation_id":"g1","model":"claude-opus-5-5-medium","model_id":"claude-opus-5-5",
                 "model_params":[{"id":"context","value":"300k"},{"id":"effort","value":"medium"},{"id":"fast","value":"false"}],
                 "composer_mode":"agent","prompt":"보냈어 never-prompt","attachments":[],"session_id":"s",
                 "hook_event_name":"beforeSubmitPrompt","cursor_version":"3.24.12","workspace_roots":["e:/한글 폴더"],
                 "user_email":"synthetic@example.invalid","transcript_path":null}
                """);
            Check(submit.Code == 0 && submit.Output == CursorHookCommand.ContinueResponse,
                $"Cursor submit hook did not continue (exit {submit.Code}, output '{submit.Output}').");
            var request = Latest(data);
            Check(request is { Kind: CursorActivityKind.Request, ModelId: "claude-opus-5-5", Effort: "medium" },
                "Cursor submit hook with a byte order mark and Korean text was not recorded.");

            var stop = Run(executable, data, CursorHookEvent.StopEvent, """
                {"conversation_id":"c","generation_id":"g1","model":"claude-opus-5-5-medium","model_id":"claude-opus-5-5",
                 "model_params":[{"id":"effort","value":"medium"}],"status":"completed","loop_count":0,"input_tokens":1,
                 "output_tokens":2,"session_id":"s","hook_event_name":"stop","cursor_version":"3.24.12",
                 "workspace_roots":["e:/한글 폴더"],"user_email":"synthetic@example.invalid","transcript_path":null}
                """);
            Check(stop.Code == 0 && stop.Output == CursorHookCommand.EmptyResponse,
                $"Cursor stop hook did not return an empty response (exit {stop.Code}, output '{stop.Output}').");
            Check(Latest(data) is { Kind: CursorActivityKind.Completion, ModelId: "claude-opus-5-5" },
                "Cursor stop hook with a byte order mark was not recorded as the completion.");
            var stored = File.ReadAllText(new CursorActivityStore(data).Path);
            Check(!stored.Contains("never-prompt", StringComparison.Ordinal) && !stored.Contains("synthetic@", StringComparison.Ordinal),
                "Cursor hook stored prompt text or the raw email.");
            Console.WriteLine($"PASS: production Cursor hook through cmd and Windows PowerShell with code page 949, byte order mark, Korean text and quoted data path ({submit.Elapsed.TotalMilliseconds:F0} ms submit).");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static CursorRecentActivity? Latest(string data) =>
        new CursorActivityStore(data).Read() is { } state
            ? CursorActivityAttribution.Latest(state, CursorActivityAttribution.AccountKey("synthetic@example.invalid")!)
            : null;

    private static (int Code, string Output, TimeSpan Elapsed) Run(string executable, string data, string hookEvent, string json)
    {
        var command = CursorHookInstaller.Command(new(1, hookEvent, executable, data, false, false, false));
        // A console code page that is not UTF-8, as Cursor's hook processes get on Korean Windows.
        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"chcp 949 >nul & " + command + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(json.ReplaceLineEndings(" ") + "\n")).ToArray();
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the Cursor hook command.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            process.StandardInput.BaseStream.Write(bytes);
            process.StandardInput.Close();
            if (!process.WaitForExit(12000)) throw new TimeoutException("Cursor hook command did not exit within 12 seconds.");
            Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check(!output.Result.Contains("never-", StringComparison.Ordinal) && !error.Result.Contains("never-", StringComparison.Ordinal),
                "Cursor hook input reached process output.");
            return (process.ExitCode, output.Result.Trim(), watch.Elapsed);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CycleArc.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate production build.");
    }
}
