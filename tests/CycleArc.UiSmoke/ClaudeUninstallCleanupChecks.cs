using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Providers.Usage;

namespace CycleArc.UiSmoke;

/// <summary>
/// Synthetic Claude connection fixtures for the installed-app verification in
/// scripts/Verify-InstalledUpdate.ps1. Seeding writes a real connection through the
/// production account store and statusLine installer, so what is verified afterwards is
/// the same shape the app itself installs. No Claude login, credential or live request
/// is involved: these are synthetic accounts, not evidence of real subscription usage.
/// </summary>
internal static class ClaudeUninstallCleanupChecks
{
    private const string AuthJson = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"synthetic@example.invalid","orgId":"synthetic-org","subscriptionType":"pro"}""";
    // The bridge forwards the person's previous statusLine through a shell: Git Bash when it is
    // installed, PowerShell otherwise. This fixture command has to behave the same under both,
    // so it uses no shell-specific switch (a "cmd /c ..." form is mangled by Git Bash).
    private const string UserStatusLine = "echo synthetic-user-status-line";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private sealed record SeedFixture(string ProfileId, string ConfigDirectory, string CallbackExecutable,
        string CliExecutable, string UserStatusLineCommand, int UserStatusLinePadding);

    public static void Seed(string dataRoot, string configDirectory, string callbackExecutable, string seedPath)
    {
        dataRoot = Path.GetFullPath(dataRoot);
        configDirectory = Path.GetFullPath(configDirectory);
        callbackExecutable = Path.GetFullPath(callbackExecutable);
        Directory.CreateDirectory(configDirectory);
        var accounts = new CodexAccountStore(dataRoot);
        var state = accounts.LoadOrMigrate(Path.Combine(dataRoot, "synthetic-codex-home"));
        var profile = accounts.NewClaude("Synthetic Claude");
        accounts.Save(state with { Version = 2, Profiles = state.Profiles.Append(profile).ToArray() });

        var cli = Path.Combine(configDirectory, "synthetic-claude.cmd");
        File.WriteAllText(cli, "@echo off\r\necho " + AuthJson + "\r\nexit /b 0\r\n");
        var auth = ClaudeAuthentication.Parse(AuthJson, 0);
        new ClaudeConnectionStore(accounts, profile.Id).Save(new(2, profile.Id, configDirectory, cli, false,
            auth.StableFingerprint!, DateTimeOffset.UtcNow, BindingGeneration: Guid.NewGuid().ToString("N")));

        // A settings file that already carries the person's own statusLine and another
        // tool's hook. Both must survive installation, update and removal untouched.
        File.WriteAllText(Path.Combine(configDirectory, "settings.json"), JsonSerializer.Serialize(new
        {
            theme = "synthetic-preserve",
            statusLine = new { type = "command", command = UserStatusLine, padding = 3 },
            hooks = new Dictionary<string, object>
            {
                ["StopFailure"] = new[] { new { matcher = "Bash", hooks = new[] { new { type = "command", command = "cmd /c echo other-tool" } } } },
            },
        }, Indented));

        ClaudeStatusLineInstaller.InstallAsync(accounts, profile.Id, configDirectory, callbackExecutable, default)
            .GetAwaiter().GetResult();
        Check(ClaudeStatusLineInstaller.IsInstalled(configDirectory, profile.Id), "Seeding did not install the owned wrapper.");

        File.WriteAllText(Path.GetFullPath(seedPath), JsonSerializer.Serialize(
            new SeedFixture(profile.Id, configDirectory, callbackExecutable, cli, UserStatusLine, 3), Indented));
        Console.WriteLine($"PASS: seeded synthetic Claude connection {profile.Id} in {dataRoot} with an existing user statusLine and another tool's StopFailure hook.");
    }

    public static void VerifyInstalled(string dataRoot, string seedPath, string installationRoot)
    {
        var (seed, accounts, settings) = Load(dataRoot, seedPath);
        installationRoot = Path.GetFullPath(installationRoot);
        Check(ClaudeStatusLineInstaller.TryReadOwnedStatusLine(seed.ConfigDirectory, seed.ProfileId, out var owned) && owned is not null,
            "The installed app does not own the Claude statusLine wrapper.");
        Check(ClaudeUninstallCleanup.IsInsideInstallation(installationRoot, owned!.CycleArcExecutable),
            $"The owned wrapper points outside the installation: {owned.CycleArcExecutable}");
        Check(owned.HadStatusLine && owned.PreviousStatusLine?["command"]?.GetValue<string>() == seed.UserStatusLineCommand,
            "The wrapper did not retain the person's previous statusLine.");
        Check(OwnedFailureCommands(settings).Length == 1, "Exactly one owned StopFailure receiver must be installed.");
        CheckUnrelatedHookSurvives(settings);
        CheckPreservedAccountData(accounts, seed);
        Console.WriteLine($"PASS: installed Claude callbacks are owned by {installationRoot} and the person's own statusLine and hook are preserved.");
    }

    public static void VerifyRemoved(string dataRoot, string seedPath, string installationRoot)
    {
        var (seed, accounts, settings) = Load(dataRoot, seedPath);
        installationRoot = Path.GetFullPath(installationRoot);
        var statusLine = settings["statusLine"] as JsonObject
            ?? throw new InvalidOperationException("Removal did not restore the person's statusLine.");
        Check(statusLine["command"]?.GetValue<string>() == seed.UserStatusLineCommand,
            "The restored statusLine is not the command the person had before.");
        Check(statusLine["padding"]?.GetValue<int>() == seed.UserStatusLinePadding,
            "Removal lost a property of the person's own statusLine.");
        Check(!ClaudeStatusLineInstaller.IsInstalled(seed.ConfigDirectory, seed.ProfileId),
            "A CycleArc statusLine wrapper survived removal.");
        Check(OwnedFailureCommands(settings).Length == 0, "A CycleArc StopFailure receiver survived removal.");
        CheckUnrelatedHookSurvives(settings);

        var text = File.ReadAllText(Path.Combine(seed.ConfigDirectory, "settings.json"));
        Check(!text.Contains(installationRoot, StringComparison.OrdinalIgnoreCase),
            "A path inside the removed installation is still referenced by Claude settings.");
        Check(!text.Contains(Path.GetFileName(seed.CallbackExecutable), StringComparison.OrdinalIgnoreCase),
            "A CycleArc executable name is still referenced by Claude settings.");
        CheckPreservedAccountData(accounts, seed);

        var receiptPath = ClaudeUninstallCleanup.ReportPath(accounts);
        Check(File.Exists(receiptPath), "Removal did not record a cleanup receipt.");
        var receipt = JsonSerializer.Deserialize<JsonObject>(File.ReadAllText(receiptPath))
            ?? throw new InvalidOperationException("The cleanup receipt is not readable.");
        Check(receipt["Completed"]?.GetValue<bool>() == true, "The cleanup receipt does not report a completed run.");
        Check(receipt["IncompleteReason"]?.GetValue<string>() == nameof(ClaudeUninstallIncompleteReason.None),
            "The cleanup receipt reports a reason for not finishing.");
        Check(receipt["Restored"]?.GetValue<int>() >= 1, "The cleanup receipt does not report a restored profile.");
        Check(receipt["Failed"]?.GetValue<int>() == 0, "The cleanup receipt reports a failed profile.");
        Check(!File.ReadAllText(receiptPath).Contains("powershell.exe", StringComparison.OrdinalIgnoreCase),
            "The cleanup receipt contains callback command text.");
        Console.WriteLine("PASS: removal restored the person's statusLine, removed both owned callbacks, kept another tool's hook, and preserved accounts, connection and usage data.");
    }

    private static (SeedFixture Seed, CodexAccountStore Accounts, JsonObject Settings) Load(string dataRoot, string seedPath)
    {
        var seed = JsonSerializer.Deserialize<SeedFixture>(File.ReadAllText(Path.GetFullPath(seedPath)))
            ?? throw new InvalidOperationException("The seed description is not readable.");
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(seed.ConfigDirectory, "settings.json")))?.AsObject()
            ?? throw new InvalidOperationException("Claude settings are not readable.");
        return (seed, new CodexAccountStore(Path.GetFullPath(dataRoot)), settings);
    }

    // The account registry, the connection binding and the usage inbox must still be
    // readable by the production stores, not merely present as untouched files.
    private static void CheckPreservedAccountData(CodexAccountStore accounts, SeedFixture seed)
    {
        Check(accounts.ContainsClaude(seed.ProfileId), "The Claude profile is missing from the account registry.");
        var profiles = accounts.ReadClaudeProfiles();
        Check(profiles.Available, "The account registry could not be read back.");
        Check(profiles.Ids.Contains(seed.ProfileId, StringComparer.Ordinal),
            "The Claude profile is no longer listed by the account store.");
        var read = new ClaudeConnectionStore(accounts, seed.ProfileId).Read();
        Check(!read.Unavailable && read.Binding is { Disconnected: false }, "The Claude connection record was lost or revoked.");
        Check(string.Equals(read.Binding!.ConfigDirectory, seed.ConfigDirectory, StringComparison.OrdinalIgnoreCase),
            "The Claude connection now points at a different configuration directory.");
        Check(File.Exists(seed.CliExecutable), "The synthetic Claude CLI fixture was deleted.");
        var state = accounts.LoadOrMigrate(Path.Combine(accounts.RootDirectory, "synthetic-codex-home"));
        Check(state.Profiles.Any(profile => profile.Id == seed.ProfileId && profile.Provider == UsageProviderId.Claude),
            "The account registry no longer describes the Claude profile.");
    }

    private static void CheckUnrelatedHookSurvives(JsonObject settings)
    {
        Check(settings["theme"]?.GetValue<string>() == "synthetic-preserve", "An unrelated Claude setting was changed.");
        var stop = settings["hooks"]?["StopFailure"]?.AsArray()
            ?? throw new InvalidOperationException("Another tool's StopFailure hook was removed.");
        Check(stop.Any(entry => entry?["hooks"]?[0]?["command"]?.GetValue<string>() == "cmd /c echo other-tool"),
            "Another tool's StopFailure hook was removed.");
    }

    private static string[] OwnedFailureCommands(JsonObject settings) =>
        (settings["hooks"]?["StopFailure"]?.AsArray() ?? new JsonArray())
        .SelectMany(entry => entry?["hooks"]?.AsArray() ?? new JsonArray())
        .Select(hook => hook?["command"]?.GetValue<string>())
        .Where(command => command is not null && ClaudeFailureCommand.TryRead(command, out _))
        .Select(command => command!)
        .ToArray();

    /// <summary>Runs the installed wrapper exactly as Claude Code would, with synthetic input.</summary>
    public static void VerifyCallbackRuns(string dataRoot, string seedPath)
    {
        var (seed, accounts, settings) = Load(dataRoot, seedPath);
        var command = settings["statusLine"]?["command"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No statusLine command is installed.");
        Check(ClaudeStatusLineInstaller.TryRead(command, out _), "The installed statusLine is not an owned CycleArc wrapper.");
        var now = DateTimeOffset.UtcNow;
        var input = JsonSerializer.Serialize(new
        {
            rate_limits = new
            {
                five_hour = new { used_percentage = 12.5, resets_at = now.AddHours(5).ToUnixTimeSeconds() },
                seven_day = new { used_percentage = 34.5, resets_at = now.AddDays(7).ToUnixTimeSeconds() },
            },
        });
        var result = RunProcess("powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", command.Split(' ')[^1]], input);
        Check(result.Code == 0, $"The installed Claude callback failed ({Describe(result)}).");
        Check(result.Output.Contains("synthetic-user-status-line", StringComparison.Ordinal),
            $"The installed wrapper stopped forwarding the person's own statusLine output ({Describe(result)}).");
        var state = new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(seed.ProfileId), seed.ProfileId).Read().State;
        Check(state?.LastGood?.FiveHour?.UsedPercentage == 12.5 && state?.LastGood?.SevenDay?.UsedPercentage == 34.5,
            "The installed callback did not record the synthetic usage sample.");
        Console.WriteLine("PASS: the installed Claude callback runs from the current installation and records a synthetic sample (not a live subscription check).");
    }

    /// <summary>Runs the person's own restored statusLine after removal, through the same shell
    /// the bridge would have used, so the check reflects how Claude actually invokes it.</summary>
    public static void VerifyRestoredCallbackRuns(string dataRoot, string seedPath)
    {
        var (seed, _, settings) = Load(dataRoot, seedPath);
        var command = settings["statusLine"]?["command"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No statusLine command remains.");
        Check(!ClaudeStatusLineInstaller.TryRead(command, out _), "A CycleArc wrapper is still installed after removal.");
        Check(command == seed.UserStatusLineCommand, "The restored statusLine is not the command that was seeded.");
        var result = RunProcess(ClaudeStatusLineBridge.ShellStartInfo(command), "{}");
        Check(result.Code == 0 && result.Output.Contains("synthetic-user-status-line", StringComparison.Ordinal),
            $"The person's restored statusLine command does not run ({Describe(result)}).");
        Console.WriteLine("PASS: the person's own statusLine runs after removal and no CycleArc process is invoked.");
    }

    private static string Describe((int Code, string Output, string Error) result) =>
        $"exit {result.Code}, output '{result.Output.Trim()}', error '{result.Error.Trim()}'";

    private static (int Code, string Output, string Error) RunProcess(string executable, string[] args, string? input)
    {
        var info = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return RunProcess(info, input);
    }

    /// <summary>
    /// Generous on purpose. The first callback runs a freshly installed self-contained single-file
    /// desktop, so that launch pays for extracting its native libraries before it reads a line of
    /// input; on a cold runner that alone has exceeded twenty seconds. This is the harness's
    /// patience, not a product deadline.
    /// </summary>
    private static readonly TimeSpan CallbackBudget = TimeSpan.FromSeconds(120);

    private static (int Code, string Output, string Error) RunProcess(
        System.Diagnostics.ProcessStartInfo info, string? input)
    {
        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException("Could not start the callback process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
            if (!process.WaitForExit((int)CallbackBudget.TotalMilliseconds))
                throw new TimeoutException(
                    $"The callback did not exit within {CallbackBudget.TotalSeconds:F0} seconds: {info.FileName}.");
            Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            return (process.ExitCode, output.Result, error.Result);
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
}
