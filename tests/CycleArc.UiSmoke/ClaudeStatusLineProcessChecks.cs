using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Services;

namespace CycleArc.UiSmoke;

internal static class ClaudeStatusLineProcessChecks
{
    internal const string PreviousStatusLineChildArgument = "--claude-previous-statusline-child";
    private const string PreviousOutput = "Existing line 23.5 한글";

    internal static int RunPreviousStatusLineChild()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var input = Console.In.ReadToEnd();
        if (!input.Contains("\"five_hour\":{\"used_percentage\":23.5,", StringComparison.Ordinal)) return 7;
        Console.Out.WriteLine(PreviousOutput);
        return 0;
    }

    public static void Run(string? executable = null)
    {
        executable ??= Path.Combine(RepositoryRoot(), "src", "CycleArc", "bin", "Release",
            "net8.0-windows10.0.17763.0", "CycleArc.exe");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Build the production executable before checking stdin.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cyclearc-stdin-test-" + Guid.NewGuid().ToString("N"), "A space O'Brien $x`"));
        var accounts = new CodexAccountStore(root);
        var state = accounts.LoadOrMigrate(Path.Combine(root, "synthetic-codex-home"));
        var profile = accounts.NewClaude("Synthetic Claude");
        accounts.Save(state with { Version = 2, Profiles = state.Profiles.Append(profile).ToArray() });
        var store = new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(profile.Id), profile.Id);
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(new
        {
            transcript_path = "never-read-synthetic-path", session_id = "never-save-synthetic-session", prompt = "never-save-synthetic-prompt",
            rate_limits = new { five_hour = new { used_percentage = 23.5, resets_at = now.AddHours(5).ToUnixTimeSeconds() },
                seven_day = new { used_percentage = 41.2, resets_at = now.AddDays(7).ToUnixTimeSeconds() } }
        });
        // A regression that enters desktop startup exits at the mutex, before accessing
        // real settings/accounts. A correctly routed receiver works while it is held.
        using var mutex = new Mutex(false, LegacyInstallation.SingleInstanceMutexName);
        var owns = false;
        try
        {
            try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
            var invalidRecovery = RunProcess(executable, ["--apply-update"], "");
            Check(invalidRecovery.Code == 2, "Incomplete recovery mode entered desktop startup.");
            invalidRecovery = RunProcess(executable, ["--apply-update", Path.Combine(root, "job.json")], "");
            Check(invalidRecovery.Code == 1 && invalidRecovery.Output.Length == 0,
                "An untrusted recovery job entered desktop startup or emitted data.");
            var result = RunProcess(executable, [ClaudeStatusLineCommand.Argument, profile.Id, "--data-root", root], json);
            Check(result.Code == 0 && result.Output.Contains("5h 23.5%", StringComparison.Ordinal), $"Production stdin receiver failed ({Describe(result)}).");
            Check(store.Read().State?.LastGood?.SevenDay?.UsedPercentage == 41.2, "Production receiver lost weekly data.");

            using var settings = JsonDocument.Parse(ClaudeStatusLineCommand.SettingsJson(executable, profile.Id, root));
            var command = settings.RootElement.GetProperty("statusLine").GetProperty("command").GetString()!;
            var encoded = command.Split(' ')[^1];
            result = RunProcess("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], json);
            Check(result.Code == 0 && result.Output.Contains("7d 41.2%", StringComparison.Ordinal), $"Generated PowerShell statusLine command failed ({Describe(result)}).");
            // Generated wrappers must work without first-use PowerShell module imports.
            var noModules = Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "$PSModuleAutoLoadingPreference = 'None'; " + Encoding.Unicode.GetString(Convert.FromBase64String(encoded))));
            result = RunProcess("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", noModules], json);
            Check(result.Code == 0 && result.Output.Contains("7d 41.2%", StringComparison.Ordinal),
                $"Generated PowerShell statusLine command required module auto-loading ({Describe(result)}).");
            var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
            var checkedBash = File.Exists(bash);
            if (checkedBash)
            {
                result = RunProcess(bash, ["--noprofile", "--norc", "-c", command], json);
                Check(result.Code == 0 && result.Output.Contains("7d 41.2%", StringComparison.Ordinal), $"Generated Git Bash statusLine command failed ({Describe(result)}).");
            }
            result = RunProcess(executable, [ClaudeStatusLineCommand.Argument, profile.Id, "--data-root", root], "{broken");
            Check(result.Code == 1 && store.Read().State?.LastInputStatus == ClaudeInputStatus.Malformed, "Malformed production input was not rejected.");
            Check(new ClaudeQuotaService(store).Snapshot.Status == CodexQuotaStatus.Stale, "Malformed input discarded the last valid percentages.");
            var saved = File.ReadAllText(accounts.ClaudeStatusLinePath(profile.Id));
            Check(!saved.Contains("never-", StringComparison.Ordinal), "Raw statusLine metadata reached disk.");
            result = RunProcess(executable, [ClaudeStatusLineCommand.Argument, profile.Id, "--data-root", root], null);
            Check(result.Code == 1, "Production stdin deadline did not exit.");
            Check(!File.Exists(Path.Combine(root, "settings.json")), "Collector initialized desktop settings.");
            CheckAuthenticatedBridge(executable, accounts, profile, root, json);
            CheckStopFailureBridge(executable, accounts, profile, root, json);
            Console.WriteLine("PASS: production Claude stdin receiver, held desktop mutex, isolated registry, malformed-input retention, deadline, PowerShell"
                + (checkedBash ? " and Git Bash" : " (Git Bash not installed)") + "; no live account access.");
        }
        finally
        {
            if (owns) mutex.ReleaseMutex();
            var owned = Directory.GetParent(root)!.FullName;
            if (!owned.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(owned).StartsWith("cyclearc-stdin-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid temporary cleanup target.");
            if (Directory.Exists(owned)) Directory.Delete(owned, true);
        }
    }

    private static void CheckAuthenticatedBridge(string executable, CodexAccountStore accounts, CodexAccountProfile profile, string root, string json)
    {
        const string authJson = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"person@example.invalid","orgId":"synthetic-org","subscriptionType":"pro"}""";
        var directory = Path.Combine(root, "Claude home");
        Directory.CreateDirectory(directory);
        var cli = Path.Combine(directory, "synthetic claude.cmd");
        File.WriteAllText(cli, "@echo off\r\necho " + authJson + "\r\nexit /b 0\r\n");
        var auth = ClaudeAuthentication.Parse(authJson, 0);
        var connections = new ClaudeConnectionStore(accounts, profile.Id);
        connections.Save(new(2, profile.Id, directory, cli, false, auth.Fingerprint!, DateTimeOffset.UtcNow,
            BindingGeneration: Guid.NewGuid().ToString("N")));
        // The production wrapper is still exercised through PowerShell and Git Bash.
        // Its preserved command uses a small test child, so the four-second forwarding
        // budget measures stdin/stdout preservation without another PowerShell startup.
        var fixtureExe = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe").Replace('\\', '/');
        Check(File.Exists(fixtureExe), "The existing-statusLine test child is missing.");
        var rejected = RunProcess(fixtureExe, [PreviousStatusLineChildArgument], "{}");
        Check(rejected.Code == 7 && rejected.Output.Length == 0,
            $"The existing-statusLine fixture accepted missing quota input ({Describe(rejected)}).");
        var usesBash = ClaudeStatusLineBridge.ShellStartInfo("").FileName
            .EndsWith("bash.exe", StringComparison.OrdinalIgnoreCase);
        var oldCommand = (usesBash
            ? "'" + fixtureExe.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'"
            : "& '" + fixtureExe.Replace("'", "''", StringComparison.Ordinal) + "'")
            + " " + PreviousStatusLineChildArgument;
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new { theme = "preserve", statusLine = new { type = "command", command = oldCommand, padding = 2 } }));
        var options = ClaudeStatusLineInstaller.InstallAsync(accounts, profile.Id, directory, executable, default).GetAwaiter().GetResult();
        var command = ClaudeStatusLineInstaller.Command(options);
        var result = RunProcess("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", command.Split(' ')[^1]], json);
        Check(result.Code == 0 && result.Output.Trim() == PreviousOutput,
            $"Automatic bridge did not preserve existing output and input ({Describe(result)}, failure '{new ClaudeFailureStore(accounts).Read(profile.Id).State?.Kind}').");
        var store = new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(profile.Id), profile.Id);
        Check(store.Read().State?.LastGood?.FiveHour?.UsedPercentage == 23.5, "Authenticated bridge did not persist official quota fields.");
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (File.Exists(bash))
        {
            result = RunProcess(bash, ["--noprofile", "--norc", "-c", command], json);
            Check(result.Code == 0 && result.Output.Trim() == PreviousOutput,
                $"Automatic bridge failed under Git Bash ({Describe(result)}).");
        }
        var beforeAuthLoss = File.ReadAllBytes(accounts.ClaudeStatusLinePath(profile.Id));
        File.WriteAllText(cli, "@echo off\r\necho {\"loggedIn\":false}\r\nexit /b 1\r\n");
        result = RunProcess(executable, [ClaudeStatusLineBridge.Argument, ClaudeStatusLineInstaller.Payload(options)], json);
        Check(result.Code == 1 && result.Output.Trim() == PreviousOutput, "Auth loss discarded existing status line output.");
        Check(File.ReadAllBytes(accounts.ClaudeStatusLinePath(profile.Id)).SequenceEqual(beforeAuthLoss),
            "Signed-out bridge changed the last quota receipt.");
        Check(new ClaudeFailureStore(accounts).Read(profile.Id).State?.Kind == ClaudeFailureKind.AuthRequired,
            "Signed-out bridge did not record the separate authentication failure.");

        // Exercise plan changes and a delayed old-generation callback in the real executable.
        File.WriteAllText(cli, "@echo off\r\necho " + authJson.Replace("\"pro\"", "\"max\"", StringComparison.Ordinal)
            + "\r\nexit /b 0\r\n");
        connections.Save(connections.Read().Binding! with { BindingGeneration = Guid.NewGuid().ToString("N") });
        var currentOptions = ClaudeStatusLineInstaller.InstallAsync(accounts, profile.Id, directory, executable, default).GetAwaiter().GetResult();
        result = RunProcess(executable, [ClaudeStatusLineBridge.Argument, ClaudeStatusLineInstaller.Payload(currentOptions)], json);
        Check(result.Code == 0, "Same-account plan change prevented a current-generation receipt.");
        var currentQuota = File.ReadAllBytes(accounts.ClaudeStatusLinePath(profile.Id));
        var currentFailure = File.ReadAllBytes(accounts.ClaudeFailurePath(profile.Id));
        result = RunProcess(executable, [ClaudeStatusLineBridge.Argument, ClaudeStatusLineInstaller.Payload(options)], json);
        Check(result.Code == 1 && result.Output.Trim() == PreviousOutput, "Old callback lost previous statusLine output.");
        Check(File.ReadAllBytes(accounts.ClaudeStatusLinePath(profile.Id)).SequenceEqual(currentQuota)
            && File.ReadAllBytes(accounts.ClaudeFailurePath(profile.Id)).SequenceEqual(currentFailure),
            "An old-generation callback changed the current quota or failure record.");
        Check(!File.ReadAllText(accounts.ClaudeStatusLinePath(profile.Id)).Contains("never-", StringComparison.Ordinal), "Bridge stored raw stdin.");
        Console.WriteLine("PASS: automatic Claude bridge in production executable; official auth adapter, quoted paths, preserved statusLine stdin/output, signed-out rejection, PowerShell/Git Bash, isolated synthetic data.");
    }


    private static void CheckStopFailureBridge(string executable, CodexAccountStore accounts,
        CodexAccountProfile profile, string root, string quotaJson)
    {
        var connection = new ClaudeConnectionStore(accounts, profile.Id);
        var binding = connection.Read().Binding! with { BindingGeneration = Guid.NewGuid().ToString("N") };
        connection.Save(binding);
        // Local auth metadata can remain signed in while a real request fails. The official
        // failure event, not a second local auth-status check, must drive the visible state.
        File.WriteAllText(binding.CliExecutable, "@echo off\r\necho {\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"apiProvider\":\"firstParty\",\"email\":\"person@example.invalid\",\"orgId\":\"synthetic-org\",\"subscriptionType\":\"pro\"}\r\nexit /b 0\r\n");
        ClaudeStatusLineInstaller.InstallAsync(accounts, profile.Id, binding.ConfigDirectory, executable, default).GetAwaiter().GetResult();
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(binding.ConfigDirectory, "settings.json")));
        var commands = settings.RootElement.GetProperty("hooks").GetProperty("StopFailure").EnumerateArray()
            .SelectMany(group => group.GetProperty("hooks").EnumerateArray())
            .Select(hook => hook.GetProperty("command").GetString()!)
            .Where(command => ClaudeFailureCommand.TryRead(command, out _)).ToArray();
        Check(commands.Length == 1, "Setup must install exactly one owned failure receiver.");
        var command = commands.Single();
        Check(ClaudeFailureCommand.TryRead(command, out var options), "Installed failure command cannot be decoded.");
        var failureJson = """{"hook_event_name":"StopFailure","error":"authentication_failed","error_details":"never-save-synthetic-detail","last_assistant_message":"never-save-synthetic-response","transcript_path":"never-read-synthetic-path","session_id":"never-save-synthetic-session"}""";
        var quotaPath = accounts.ClaudeStatusLinePath(profile.Id);
        var before = File.ReadAllBytes(quotaPath);
        var result = RunProcess("powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", command.Split(' ')[^1]], failureJson);
        var failures = new ClaudeFailureStore(accounts);
        Check(result.Code == 0 && string.IsNullOrWhiteSpace(result.Output), "Installed failure receiver did not exit silently.");
        Check(failures.Read(profile.Id).State is { Kind: ClaudeFailureKind.AuthRequired },
            "Actual request authentication failure was hidden by signed-in local metadata.");
        Check(File.ReadAllBytes(quotaPath).SequenceEqual(before), "Failure receiver rewrote the last usage receipt.");
        var saved = File.ReadAllBytes(accounts.ClaudeFailurePath(profile.Id));
        Check(!Encoding.UTF8.GetString(saved).Contains("never-", StringComparison.Ordinal),
            "Failure receiver persisted raw message or session metadata.");

        result = RunProcess(executable, [ClaudeFailureCommand.Argument, ClaudeFailureCommand.Payload(options!)],
            """{"hook_event_name":"Stop","error":"authentication_failed"}""");
        Check(result.Code == 1 && File.ReadAllBytes(accounts.ClaudeFailurePath(profile.Id)).SequenceEqual(saved),
            "Wrong hook event overwrote a verified failure.");
        result = RunProcess(executable, [ClaudeFailureCommand.Argument, ClaudeFailureCommand.Payload(options!)],
            """{"hook_event_name":"StopFailure","error":"authentication_failed","error":"server_error"}""");
        Check(result.Code == 1 && File.ReadAllBytes(accounts.ClaudeFailurePath(profile.Id)).SequenceEqual(saved),
            "Duplicate error metadata was accepted.");

        connection.Save(binding with { BindingGeneration = Guid.NewGuid().ToString("N") });
        result = RunProcess(executable, [ClaudeFailureCommand.Argument, ClaudeFailureCommand.Payload(options!)], failureJson);
        Check(result.Code == 1 && File.ReadAllBytes(accounts.ClaudeFailurePath(profile.Id)).SequenceEqual(saved),
            "An old session's failure overwrote a new binding generation.");
        connection.Save(binding with { Disconnected = true });
        result = RunProcess(executable, [ClaudeFailureCommand.Argument, ClaudeFailureCommand.Payload(options!)], failureJson);
        Check(result.Code == 1 && File.ReadAllBytes(accounts.ClaudeFailurePath(profile.Id)).SequenceEqual(saved),
            "Disconnected failure callback changed account state.");
        connection.Save(binding);

        result = RunProcess(executable, [ClaudeFailureCommand.Argument, ClaudeFailureCommand.Payload(options!)], null);
        Check(result.Code == 1, "Missing failure stdin did not respect the receiver deadline.");
        Check(File.ReadAllBytes(quotaPath).SequenceEqual(before), "Failure validation altered quota values or timestamps.");

        // A statusLine refresh can repeat cached percentages after an API failure; it is
        // receipt metadata, not proof that remote authentication has recovered.
        var statusOptions = ClaudeStatusLineInstaller.InstallAsync(accounts, profile.Id, binding.ConfigDirectory, executable, default).GetAwaiter().GetResult();
        result = RunProcess(executable, [ClaudeStatusLineBridge.Argument, ClaudeStatusLineInstaller.Payload(statusOptions)], quotaJson);
        Check(result.Code == 0, "Synthetic signed-in statusLine callback did not complete.");
        var quota = new ClaudeStatusLineStore(quotaPath, profile.Id).Read().State;
        Check(ClaudeFailureClassification.IsActive(failures.Read(profile.Id).State, binding, quota?.LastGood),
            "A repeated cached statusLine value hid an authentication failure.");

        Console.WriteLine("PASS: production StopFailure hook beside desktop mutex; signed-in metadata/authentication failure, preserved quota receipt, metadata privacy, wrong/duplicate event, old generation, disconnection and stdin deadline.");
    }
    private static (int Code, string Output, string Error, TimeSpan Elapsed) RunProcess(string executable, string[] args, string? input)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start test receiver.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
            if (!process.WaitForExit(12000)) throw new TimeoutException($"StatusLine receiver did not exit within 12 seconds ({Path.GetFileName(executable)}).");
            Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check(!output.Result.Contains("never-", StringComparison.Ordinal) && !error.Result.Contains("never-", StringComparison.Ordinal),
                "Raw statusLine data reached process output.");
            return (process.ExitCode, output.Result, error.Result, watch.Elapsed);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
        }
    }

    private static string Describe((int Code, string Output, string Error, TimeSpan Elapsed) result) =>
        $"exit {result.Code}, elapsed {result.Elapsed.TotalSeconds:F2}s, output '{result.Output.Trim()}', error '{result.Error.Trim()}'";

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
