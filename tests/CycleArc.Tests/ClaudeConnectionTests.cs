using System.Diagnostics;
using System.Text;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Services;
using CycleArc.Providers.Usage;
using static CycleArc.Tests.ClaudeStatusLineTests;

namespace CycleArc.Tests;

public class ClaudeConnectionTests
{
    internal const string AuthJson = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"person@example.invalid","orgId":"synthetic-org","subscriptionType":"pro","accessToken":"never-store-secret"}""";
    private static readonly ClaudeAuthentication Auth = ClaudeAuthentication.Parse(AuthJson, 0);
    private static string DirectoryFor(ClaudeTestData data) => System.IO.Path.Combine(data.Root, "Claude home");
    private static string AppFor(ClaudeTestData data) => System.IO.Path.Combine(data.Root, "CycleArc.exe");
    private static string Settings(ClaudeTestData data) => System.IO.Path.Combine(DirectoryFor(data), "settings.json");

    [Theory]
    [InlineData("{}", 0, ClaudeAuthStatus.InvalidResponse)]
    [InlineData("[]", 0, ClaudeAuthStatus.InvalidResponse)]
    [InlineData("{\"loggedIn\":false}", 1, ClaudeAuthStatus.SignedOut)]
    [InlineData("{\"loggedIn\":true,\"loggedIn\":false}", 0, ClaudeAuthStatus.InvalidResponse)]
    [InlineData("{\"loggedIn\":true,\"authMethod\":\"api_key\",\"apiProvider\":\"firstParty\"}", 0, ClaudeAuthStatus.Unsupported)]
    [InlineData(AuthJson, 2, ClaudeAuthStatus.Failed)]
    public void AuthenticationNeverInfersSuccessfulLogin(string json, int code, ClaudeAuthStatus status) =>
        Assert.Equal(status, ClaudeAuthentication.Parse(json, code).Status);

    [Fact]
    public async Task ExistingLoginConnectsWithoutInteractiveLoginAndKeepsOtherSettings()
    {
        using var data = new ClaudeTestData();
        Directory.CreateDirectory(DirectoryFor(data));
        const string original = """{"theme":"dark","env":{"SYNTHETIC":"preserve"},"permissions":{"allow":["Read"]}}""";
        File.WriteAllText(Settings(data), original);
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var result = await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default);
        Assert.True(result.Success);
        Assert.False(cli.LastLogin);
        Assert.Equal(DirectoryFor(data), cli.LastDirectory);
        Assert.Equal(Auth.Email, connection.Email(data.Profile.Id, Auth.Fingerprint));
        Assert.Null(connection.Email(data.Profile.Id, new string('B', 64)));
        var settings = JsonNode.Parse(File.ReadAllText(Settings(data)))!;
        Assert.Equal("dark", settings["theme"]!.GetValue<string>());
        Assert.Equal("preserve", settings["env"]!["SYNTHETIC"]!.GetValue<string>());
        Assert.True(ClaudeStatusLineInstaller.IsInstalled(DirectoryFor(data), data.Profile.Id));
        Assert.Equal(original, File.ReadAllText(Settings(data) + ".cyclearc.bak"));
        var saved = File.ReadAllText(data.Accounts.ClaudeConnectionPath(data.Profile.Id));
        Assert.DoesNotContain("person@", saved);
        Assert.DoesNotContain("never-store", saved);
        Assert.Contains(Auth.Fingerprint!, saved);
    }

    [Fact]
    public async Task NewLoginUsesIsolatedClaudeHomeAndCancellationNeverInstalls()
    {
        using var data = new ClaudeTestData();
        var cli = new FakeCli(data.Root) { Response = new(ClaudeAuthStatus.Cancelled) };
        var result = await new ClaudeConnectionService(data.Accounts, cli, data.Clock)
            .ConnectAsync(data.Profile.Id, AppFor(data), true, null, default);
        Assert.False(result.Success);
        Assert.True(cli.LastLogin);
        Assert.StartsWith(data.Accounts.ManagedClaudeDirectory(data.Profile.Id) + System.IO.Path.DirectorySeparatorChar, cli.LastDirectory);
        Assert.False(File.Exists(data.Accounts.ClaudeConnectionPath(data.Profile.Id)));
        Assert.False(File.Exists(System.IO.Path.Combine(cli.LastDirectory!, "settings.json")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"statusLine\":\"custom\"}")]
    [InlineData("{\"theme\":\"a\",\"theme\":\"b\"}")]
    [InlineData("{broken")]
    public async Task MalformedSettingsRemainByteIdenticalAndBindingRollsBack(string json)
    {
        using var data = new ClaudeTestData();
        Directory.CreateDirectory(DirectoryFor(data)); File.WriteAllText(Settings(data), json);
        var result = await new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock)
            .ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default);
        Assert.False(result.Success);
        Assert.Equal(ClaudeSetupFailure.InvalidSettings, result.Failure);
        Assert.Equal(json, File.ReadAllText(Settings(data)));
        Assert.Null(new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding);
    }

    [Fact]
    public async Task ReconnectDoesNotNestWrapperOrOverwriteUserPaddingAndDisconnectRestoresOriginal()
    {
        using var data = new ClaudeTestData();
        Directory.CreateDirectory(DirectoryFor(data));
        var original = JsonNode.Parse("""{"statusLine":{"type":"command","command":"echo existing","padding":2},"theme":"light"}""")!;
        File.WriteAllText(Settings(data), original.ToJsonString());
        var connection = new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        var first = File.ReadAllBytes(Settings(data));
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        Assert.Equal(first, File.ReadAllBytes(Settings(data)));
        var edited = JsonNode.Parse(first)!; edited["statusLine"]!["padding"] = 7;
        File.WriteAllText(Settings(data), edited.ToJsonString());
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(1);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        var installed = JsonNode.Parse(File.ReadAllText(Settings(data)))!;
        Assert.Equal(7, installed["statusLine"]!["padding"]!.GetValue<int>());
        Assert.True(ClaudeStatusLineInstaller.TryRead(installed["statusLine"]!["command"]!.GetValue<string>(), out var options));
        Assert.Equal("echo existing", options!.PreviousStatusLine!["command"]!.GetValue<string>());
        await connection.DisconnectAsync(data.Profile.Id, default);
        Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(File.ReadAllText(Settings(data)))));
        Assert.True(new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding!.Disconnected);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"statusLine\":null}")]
    public async Task DisconnectPreservesAbsentVersusNullStatusLine(string original)
    {
        using var data = new ClaudeTestData();
        Directory.CreateDirectory(DirectoryFor(data)); File.WriteAllText(Settings(data), original);
        var connection = new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        await connection.DisconnectAsync(data.Profile.Id, default);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(File.ReadAllText(Settings(data)))));
    }

    [Fact]
    public async Task DisconnectCommitsRevocationBeforeInvalidSettingsCleanup()
    {
        using var data = new ClaudeTestData();
        Directory.CreateDirectory(DirectoryFor(data)); File.WriteAllText(Settings(data), "{}");
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        var command = JsonNode.Parse(File.ReadAllText(Settings(data)))!["statusLine"]!["command"]!.GetValue<string>();
        Assert.True(ClaudeStatusLineInstaller.TryRead(command, out var options));
        const string invalid = "{\"hooks\":[]}";
        File.WriteAllText(Settings(data), invalid);

        var failure = await Assert.ThrowsAsync<ClaudeDisconnectCleanupException>(
            () => connection.DisconnectAsync(data.Profile.Id, default));

        Assert.Equal(ClaudeSetupFailure.DisconnectCleanupIncomplete, failure.Failure);
        Assert.True(new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding!.Disconnected);
        Assert.Equal(invalid, File.ReadAllText(Settings(data)));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Payload()));
        Assert.Equal(1, await ClaudeStatusLineBridge.RunAsync(options!, input, new StringWriter(),
            data.Accounts, cli, data.Clock));
    }

    [Fact]
    public async Task DisconnectReportsUsageInboxCleanupFailureAfterRevocation()
    {
        using var data = new ClaudeTestData();
        Directory.CreateDirectory(DirectoryFor(data)); File.WriteAllText(Settings(data), "{}");
        var connection = new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        var store = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        var binding = store.Read().Binding!;
        using var lease = new FileStream(data.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var failure = await Assert.ThrowsAsync<ClaudeDisconnectCleanupException>(
            () => connection.DisconnectAsync(data.Profile.Id, default));

        Assert.Equal(ClaudeSetupFailure.DisconnectCleanupIncomplete, failure.Failure);
        Assert.IsType<IOException>(failure.InnerException);
        Assert.True(store.Read().Binding!.Disconnected);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{}"), JsonNode.Parse(File.ReadAllText(Settings(data)))));
        Assert.Equal(binding with { Disconnected = true }, store.Read().Binding);
    }

    [Fact]
    public async Task DisconnectBindingSaveFailureIsNotReportedAsSuccess()
    {
        using var data = new ClaudeTestData();
        Directory.CreateDirectory(DirectoryFor(data)); File.WriteAllText(Settings(data), "{}");
        var connection = new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        var store = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        var before = store.Read().Binding!;
        using var lease = new FileStream(store.PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() => connection.DisconnectAsync(data.Profile.Id, default));

        Assert.Equal(before, store.Read().Binding);
    }

    [Fact]
    public async Task ConcurrentUserEditIsDetectedBeforeReplacingSettings()
    {
        using var data = new ClaudeTestData();
        Directory.CreateDirectory(DirectoryFor(data)); File.WriteAllText(Settings(data), "{}");
        const string changed = """{"theme":"changed-by-user"}""";
        var exception = await Assert.ThrowsAsync<ClaudeSetupException>(() => ClaudeStatusLineInstaller.InstallAsync(data.Accounts,
            data.Profile.Id, DirectoryFor(data), AppFor(data), default, () => File.WriteAllText(Settings(data), changed)));
        Assert.Equal(ClaudeSetupFailure.SettingsChanged, exception.Failure);
        Assert.Equal(changed, File.ReadAllText(Settings(data)));
    }

    [Fact]
    public async Task AnotherActiveProfileCannotTakeOverTheSameSettings()
    {
        using var data = new ClaudeTestData();
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        var first = File.ReadAllBytes(Settings(data));
        var second = data.Accounts.NewClaude("Second");
        var registry = data.Accounts.LoadOrMigrate("unused");
        data.Accounts.Save(registry with { Profiles = registry.Profiles.Append(second).ToArray() });
        cli.Response = Auth with { Email = "different@example.invalid", Fingerprint = new string('B', 64) };
        var result = await connection.ConnectAsync(second.Id, AppFor(data), false, DirectoryFor(data), default);
        Assert.False(result.Success); Assert.Equal(ClaudeSetupFailure.AlreadyLinked, result.Failure);
        Assert.Equal(first, File.ReadAllBytes(Settings(data)));
        Assert.Null(new ClaudeConnectionStore(data.Accounts, second.Id).Read().Binding);
    }

    [Fact]
    public async Task RepeatedCurrentLoginReusesTheExistingBindingAndPreservesItsUsageAndName()
    {
        using var data = new ClaudeTestData();
        await data.Receive(Payload());
        var connection = new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock);
        var original = await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default);
        Assert.True(original.Success);
        var settings = File.ReadAllBytes(Settings(data));
        var quota = File.ReadAllBytes(data.Path);
        var second = data.Accounts.NewClaude("Temporary name");
        var registry = data.Accounts.LoadOrMigrate("unused");
        data.Accounts.Save(registry with { Profiles = registry.Profiles.Append(second).ToArray() });
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(1);
        var repeated = await connection.ConnectAsync(second.Id, AppFor(data), false, DirectoryFor(data), default);
        Assert.True(repeated.Success);
        Assert.Equal(original.Binding, repeated.Binding);
        Assert.Null(new ClaudeConnectionStore(data.Accounts, second.Id).Read().Binding);
        Assert.Equal(Auth.Email, connection.Email(data.Profile.Id, Auth.Fingerprint));
        Assert.Null(connection.Email(second.Id, Auth.Fingerprint));
        Assert.Equal(settings, File.ReadAllBytes(Settings(data)));
        Assert.Equal(quota, File.ReadAllBytes(data.Path));
        Assert.Equal(data.Profile, data.Accounts.LoadOrMigrate("unused").Profiles.Single(p => p.Id == data.Profile.Id));
    }

    [Fact]
    public async Task SameIdentityInDifferentSettingsFoldersRemainsIndependent()
    {
        using var data = new ClaudeTestData();
        var connection = new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        var second = data.Accounts.NewClaude("Independent");
        var registry = data.Accounts.LoadOrMigrate("unused");
        data.Accounts.Save(registry with { Profiles = registry.Profiles.Append(second).ToArray() });
        var result = await connection.ConnectAsync(second.Id, AppFor(data), false, Path.Combine(data.Root, "other-home"), default);
        Assert.True(result.Success);
        Assert.Equal(second.Id, result.Binding!.ProfileId);
        Assert.True(ClaudeStatusLineInstaller.IsInstalled(DirectoryFor(data), data.Profile.Id));
        Assert.True(ClaudeStatusLineInstaller.IsInstalled(result.Binding.ConfigDirectory, second.Id));
    }

    [Fact]
    public async Task VerifiedConnectionAppearsBeforeUsageAndAfterRestartInspectionWithoutInventingNumbers()
    {
        using var data = new ClaudeTestData();
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var service = new ClaudeUsageProvider(data.Accounts, data.Clock, connection).Create(data.Profile);
        CodexAccountView View() => new(data.Profile, service.Snapshot, service.Email) { IsConnected = service.IsConnected };
        await connection.InspectAsync(data.Profile.Id, default);
        Assert.False(service.IsConnected); // A signed-in CLI without a binding is not connected.
        Assert.False(UsageAccountOverview.CanDisplay(View()));
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        await service.RefreshAsync(default);
        Assert.True(View().IsAwaitingUsage);
        Assert.True(UsageAccountOverview.CanDisplay(View()));
        Assert.False(service.Snapshot.HasUsablePercentages);
        Assert.Null(service.Snapshot.LastSuccessfulRefresh);
        Assert.Equal(Auth.Email, View().Email);
        Assert.Contains("Claude Code", ClaudeUsagePresentation.StatusText(service.Snapshot));
        Assert.Contains(UiText.T("usage page", "사용량 페이지"), ClaudeUsagePresentation.StatusText(service.Snapshot));
        connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        service = new ClaudeUsageProvider(data.Accounts, data.Clock, connection).Create(data.Profile);
        Assert.True(service.IsConnected); // A persisted verified binding remains visible during startup inspection.
        await connection.InspectAsync(data.Profile.Id, default);
        await service.RefreshAsync(default);
        Assert.True(View().IsAwaitingUsage);
        Assert.True(UsageAccountOverview.CanDisplay(View()));
        await connection.DisconnectAsync(data.Profile.Id, default);
        await service.RefreshAsync(default);
        Assert.False(service.IsConnected);
        Assert.False(UsageAccountOverview.CanDisplay(View()));
    }

    [Fact]
    public async Task AuthenticatedBridgeRejectsChangedIdentityWithoutReplacingLastGoodUsage()
    {
        using var data = new ClaudeTestData();
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var connected = await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default);
        Assert.True(connected.Success);
        var command = JsonNode.Parse(File.ReadAllText(Settings(data)))!["statusLine"]!["command"]!.GetValue<string>();
        Assert.True(ClaudeStatusLineInstaller.TryRead(command, out var options));
        async Task<int> Receive(double value)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Payload(value)));
            return await ClaudeStatusLineBridge.RunAsync(options!, input, new StringWriter(), data.Accounts, cli, data.Clock);
        }
        Assert.Equal(0, await Receive(23.5));
        cli.Response = Auth with { Fingerprint = new string('B', 64), Email = "changed@example.invalid" };
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        Assert.Equal(1, await Receive(92));
        Assert.Equal(23.5, data.Store().Read().State!.LastGood!.FiveHour!.UsedPercentage);
        Assert.Equal(CodexQuotaStatus.Stale, new ClaudeUsageProvider(data.Accounts, data.Clock, connection)
            .Create(data.Profile).Snapshot.Status);
        Assert.Equal(0, (await data.Receive(Payload(99))).Code); // Legacy command is inert once bound.
        Assert.Equal(23.5, data.Store().Read().State!.LastGood!.FiveHour!.UsedPercentage);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        var provider = new ClaudeUsageProvider(data.Accounts, data.Clock, connection).Create(data.Profile);
        Assert.False(provider.Snapshot.HasUsablePercentages);
        Assert.Equal(1, await Receive(92)); // A callback from the previous binding cannot populate this account.
        command = JsonNode.Parse(File.ReadAllText(Settings(data)))!["statusLine"]!["command"]!.GetValue<string>();
        Assert.True(ClaudeStatusLineInstaller.TryRead(command, out options));
        Assert.Equal(0, await Receive(92));
        await provider.RefreshAsync(default);
        Assert.Equal(92, provider.Snapshot.Windows[0].UsedPercent);
        Assert.Equal("changed@example.invalid", provider.Email);
    }

    [Fact]
    public void NullOrMalformedBindingPathsAreRejected()
    {
        using var data = new ClaudeTestData();
        var store = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        Assert.Throws<InvalidDataException>(() => store.Save(new(1, data.Profile.Id, null!, "C:/claude.exe", false, Auth.Fingerprint!, data.Clock.UtcNow)));
        var options = new ClaudeBridgeOptions(1, data.Profile.Id, null!, AppFor(data), data.Root, false, null);
        Assert.Throws<ClaudeSetupException>(() => ClaudeStatusLineInstaller.Decode(ClaudeStatusLineInstaller.Payload(options)));
    }

    [Fact]
    public void ChildAuthenticationUsesOfficialCommandsAndScopedEnvironment()
    {
        var start = ClaudeCli.StartInfo(@"C:\A space\claude.cmd", @"C:\Claude home", true);
        Assert.Contains("auth login --claudeai", start.Arguments);
        Assert.Equal(@"C:\Claude home", start.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.False(start.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.True(start.CreateNoWindow);
        Assert.Contains("auth status --json", ClaudeCli.StartInfo(@"C:\claude.exe", @"C:\Claude home", false).Arguments);
        Assert.False(ClaudeCli.IsExecutablePath(@"C:\%TEMP%\claude.cmd"));
        var implicitDefault = ClaudeCli.StartInfo(@"C:\claude.exe", null, false);
        Assert.False(implicitDefault.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Equal(ClaudeConnectionPaths.ImplicitDirectory, implicitDefault.WorkingDirectory);
        // Explicitly selecting that same folder is a distinct official CLI mode.
        Assert.Equal(ClaudeConnectionPaths.ImplicitDirectory, ClaudeCli.StartInfo(@"C:\claude.exe", ClaudeConnectionPaths.ImplicitDirectory, false).Environment["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public async Task OfficialCommandAdapterHandlesWindowsBatchStatusAndCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var data = new ClaudeTestData();
        Process? child = null;
        var passed = false;
        try
        {
            var path = System.IO.Path.Combine(data.Root, "synthetic claude.cmd");
            File.WriteAllText(path, "@echo off\r\necho " + AuthJson + "\r\nexit /b 0\r\n");
            Assert.Equal(ClaudeAuthStatus.SignedIn, (await new ClaudeCli().AuthenticateAsync(path, data.Root, false, default)).Status);
            var ready = System.IO.Path.Combine(data.Root, "child-ready");
            // Publish the actual descendant PID atomically, without PowerShell module loading.
            // Its 30-second lifetime is longer than either cleanup assertion's deadline.
            var script = "$ready = [IO.Path]::Combine([Environment]::CurrentDirectory, 'child-ready'); "
                + "[IO.File]::WriteAllText($ready + '.tmp', [string]$PID); [IO.File]::Move($ready + '.tmp', $ready); "
                + "[Threading.Thread]::Sleep(30000)";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            File.WriteAllText(System.IO.Path.Combine(data.Root, "child.cmd"),
                "@echo off\r\npowershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded + "\r\n");
            File.WriteAllText(path, "@echo off\r\ncmd /d /s /c \"\"%~dp0child.cmd\"\"\r\n");
            using var cancel = new CancellationTokenSource();
            var login = new ClaudeCli().AuthenticateAsync(path, data.Root, true, cancel.Token);
            try
            {
                Assert.True(await WaitForFileAsync(ready, TimeSpan.FromSeconds(10)),
                    "The cancellation fixture did not start its child process.");
                Assert.True(int.TryParse(File.ReadAllText(ready).Trim(), out var pid));
                child = Process.GetProcessById(pid);
                _ = child.Handle; // Retain this process identity before cancellation, avoiding PID reuse.
                Assert.False(child.HasExited, "The cancellation fixture exited before cancellation.");
            }
            finally { cancel.Cancel(); }
            var status = (await login.WaitAsync(TimeSpan.FromSeconds(10))).Status;
            Assert.Equal(ClaudeAuthStatus.Cancelled, status);
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException)
            { Assert.Fail($"Cancelled authentication left synthetic child PID {child.Id} running after two seconds."); }

            // Check process exit independently before allowing a short filesystem rundown.
            // A leaked 30-second child must fail above, not be hidden by a deletion retry.
            await DeleteProcessFixtureAsync(data.Root);
            passed = true;
        }
        finally
        {
            try
            {
                if (child is { HasExited: false })
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception ex) when (!passed && ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
            finally { child?.Dispose(); }
            // Preserve the original assertion instead of replacing it with fixture disposal.
            try { data.Dispose(); }
            catch (IOException) when (!passed) { }
        }
    }

    [Fact]
    public async Task SuccessfulCommandAdapterLeavesDetachedChildRunning()
    {
        if (!OperatingSystem.IsWindows()) return;
        var data = new ClaudeTestData();
        var path = System.IO.Path.Combine(data.Root, "synthetic claude.cmd");
        var ready = System.IO.Path.Combine(data.Root, "detached-ready");
        var stop = System.IO.Path.Combine(data.Root, "detached-stop");
        // Avoid first-use PowerShell module loading and publish the PID atomically.
        var childScript = "$root = [Environment]::CurrentDirectory; "
            + "$ready = [IO.Path]::Combine($root, 'detached-ready'); "
            + "[IO.File]::WriteAllText($ready + '.tmp', [string]$PID); [IO.File]::Move($ready + '.tmp', $ready); "
            + "$stop = [IO.Path]::Combine($root, 'detached-stop'); "
            + "$watch = [Diagnostics.Stopwatch]::StartNew(); "
            + "while (-not [IO.File]::Exists($stop) -and $watch.Elapsed.TotalSeconds -lt 60) { [Threading.Thread]::Sleep(25) }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(childScript));
        // Shell launch models a login browser: inheriting the parent's stdout
        // would keep the adapter's EOF wait open, a different lifecycle scenario.
        var parentScript = "$start = [Diagnostics.ProcessStartInfo]::new('powershell.exe'); "
            + "$start.UseShellExecute = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden; "
            + "$start.Arguments = '-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded + "'; "
            + "$child = [Diagnostics.Process]::Start($start); "
            + "[Console]::Out.WriteLine('" + AuthJson.Replace("'", "''", StringComparison.Ordinal) + "'); $child.Dispose()";
        var parentEncoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(parentScript));
        Process? child = null;
        var assertionsPassed = false;
        try
        {
            File.WriteAllText(path, "@echo off\r\npowershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand "
                + parentEncoded + "\r\nexit /b 0\r\n");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await ClaudeCli.RunAsync(ClaudeCli.StartInfo(path, data.Root, false), 16384, deadline.Token);
            var status = ClaudeAuthentication.Parse(result.Output, result.ExitCode).Status;
            Assert.Equal(ClaudeAuthStatus.SignedIn, status);
            Assert.True(await WaitForFileAsync(ready, TimeSpan.FromSeconds(10)), "Detached child did not start.");
            Assert.True(int.TryParse(File.ReadAllText(ready).Trim(), out var pid));
            child = Process.GetProcessById(pid);
            _ = child.Handle;
            Assert.False(child.HasExited, "Successful completion terminated the detached child.");
            assertionsPassed = true;
        }
        finally
        {
            try
            {
                if (child is null && await WaitForFileAsync(ready, TimeSpan.FromSeconds(10))
                    && int.TryParse(File.ReadAllText(ready).Trim(), out var cleanupPid))
                {
                    try { child = Process.GetProcessById(cleanupPid); _ = child.Handle; }
                    catch (ArgumentException) { } // The fixture has already exited.
                }
                File.WriteAllText(stop, string.Empty);
                if (child is not null)
                {
                    // A done-marker is written before process teardown. Wait on the actual
                    // retained handle instead, and do not ignore a timed-out wait.
                    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException)
                    {
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                }
                await DeleteProcessFixtureAsync(data.Root);
            }
            // A cleanup failure must not replace the original authentication/liveness assertion.
            catch (Exception) when (!assertionsPassed) { }
            finally { child?.Dispose(); }
        }
    }

    private static async Task DeleteProcessFixtureAsync(string root)
    {
        IOException? cleanupError;
        var cleanup = Stopwatch.StartNew();
        do
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                cleanupError = null;
                break;
            }
            catch (IOException ex) { cleanupError = ex; }
            await Task.Delay(25);
        } while (cleanup.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(cleanupError is null,
            $"Process fixture directory remained locked after {cleanup.Elapsed}: {cleanupError}");
    }

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path) && watch.Elapsed < timeout)
            await Task.Delay(25);
        return File.Exists(path);
    }

    private static string FrozenManualCommand(string executable, string profileId, string dataRoot)
    {
        const string prefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
        var path = Path.GetFullPath(executable).Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var root = " '--data-root' '" + Path.GetFullPath(dataRoot).Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::InputEncoding; "
            + "$input | & '" + path + "' '--claude-statusline' '" + profileId + "'"
            + root + " | ForEach-Object { $_ }; exit $LASTEXITCODE";
        return prefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    [Fact]
    public async Task AnotherLoginUsesANewHomeAndOldRunningSessionCannotWriteToTheNewAccount()
    {
        using var data = new ClaudeTestData();
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var first = await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default);
        var command = JsonNode.Parse(File.ReadAllText(Settings(data)))!["statusLine"]!["command"]!.GetValue<string>();
        Assert.True(ClaudeStatusLineInstaller.TryRead(command, out var oldOptions));
        cli.Response = Auth with { Fingerprint = new string('B', 64), Email = "another@example.invalid" };
        var next = await connection.ConnectAsync(data.Profile.Id, AppFor(data), true, null, default);
        Assert.True(next.Success);
        Assert.NotEqual(first.Binding!.ConfigDirectory, next.Binding!.ConfigDirectory);
        Assert.True(next.Binding.Managed);
        Assert.False(JsonNode.Parse(File.ReadAllText(Settings(data)))!.AsObject().ContainsKey("statusLine"));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Payload(90)));
        Assert.Equal(1, await ClaudeStatusLineBridge.RunAsync(oldOptions!, input, new StringWriter(), data.Accounts, cli, data.Clock));
        Assert.Null(data.Store().Read().State?.LastGood);
    }

    [Fact]
    public async Task ManualLegacyCommandIsUpgradedWithoutRecursivelyWrappingItself()
    {
        using var data = new ClaudeTestData();
        Directory.CreateDirectory(DirectoryFor(data));
        File.WriteAllText(Settings(data), ClaudeStatusLineCommand.SettingsJson(AppFor(data), data.Profile.Id, data.Root));
        var options = await ClaudeStatusLineInstaller.InstallAsync(data.Accounts, data.Profile.Id, DirectoryFor(data), AppFor(data), default);
        Assert.False(options.HadStatusLine); Assert.Null(options.PreviousStatusLine);
        await ClaudeStatusLineInstaller.RestoreAsync(DirectoryFor(data), data.Profile.Id, default);
        Assert.False(JsonNode.Parse(File.ReadAllText(Settings(data)))!.AsObject().ContainsKey("statusLine"));
    }

    [Fact]
    public async Task FrozenManualCommandIsUpgradedAndRestored()
    {
        using var data = new ClaudeTestData();
        Directory.CreateDirectory(DirectoryFor(data));
        var frozen = FrozenManualCommand(AppFor(data), data.Profile.Id, data.Root);
        File.WriteAllText(Settings(data), JsonSerializer.Serialize(new
        {
            theme = "preserve",
            statusLine = new { type = "command", command = frozen, padding = 2 }
        }));

        var options = await ClaudeStatusLineInstaller.InstallAsync(data.Accounts, data.Profile.Id,
            DirectoryFor(data), AppFor(data), default);
        Assert.False(options.HadStatusLine);
        Assert.Null(options.PreviousStatusLine);
        var installed = JsonNode.Parse(File.ReadAllText(Settings(data)))!;
        var replacement = installed["statusLine"]!["command"]!.GetValue<string>();
        Assert.NotEqual(frozen, replacement);
        Assert.True(ClaudeStatusLineInstaller.TryRead(replacement, out _));

        await ClaudeStatusLineInstaller.RestoreAsync(DirectoryFor(data), data.Profile.Id, default);
        var restored = JsonNode.Parse(File.ReadAllText(Settings(data)))!;
        Assert.Equal("preserve", restored["theme"]!.GetValue<string>());
        Assert.False(restored.AsObject().ContainsKey("statusLine"));
    }

    [Fact]
    public async Task DisconnectNeverOverwritesAStatusLineTheUserReplaced()
    {
        using var data = new ClaudeTestData();
        await ClaudeStatusLineInstaller.InstallAsync(data.Accounts, data.Profile.Id, DirectoryFor(data), AppFor(data), default);
        const string changed = """{"statusLine":{"type":"command","command":"echo user-replacement"}}""";
        File.WriteAllText(Settings(data), changed);
        await ClaudeStatusLineInstaller.RestoreAsync(DirectoryFor(data), data.Profile.Id, default);
        Assert.Equal(changed, File.ReadAllText(Settings(data)));
    }

    [Fact]
    public async Task LoginAndInspectionAreSingleFlightAndCancellationReleasesTheGate()
    {
        using var data = new ClaudeTestData();
        var cli = new BlockingCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        using var cancel = new CancellationTokenSource();
        var login = connection.ConnectAsync(data.Profile.Id, AppFor(data), true, null, cancel.Token);
        await cli.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var inspect = connection.InspectAsync(data.Profile.Id, default);
        Assert.False(inspect.IsCompleted); Assert.Equal(1, cli.Calls);
        cancel.Cancel();
        Assert.Equal(ClaudeAuthStatus.Cancelled, (await login).Authentication.Status);
        Assert.Equal(ClaudeAuthStatus.SignedIn, (await inspect).Authentication.Status);
        Assert.Equal(2, cli.Calls);
    }

    [Fact]
    public async Task DisconnectedProfileStaysHiddenAcrossRestartAndNeedsANewSampleAfterReconnect()
    {
        using var data = new ClaudeTestData();
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, DirectoryFor(data), default)).Success);
        var options = await ClaudeStatusLineInstaller.InstallAsync(data.Accounts, data.Profile.Id, DirectoryFor(data), AppFor(data), default);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Payload()));
        Assert.Equal(0, await ClaudeStatusLineBridge.RunAsync(options, input, new StringWriter(), data.Accounts, cli, data.Clock));
        var provider = new ClaudeUsageProvider(data.Accounts, data.Clock, connection);
        var service = provider.Create(data.Profile);
        Assert.True(UsageAccountOverview.CanDisplay(new(data.Profile, service.Snapshot)));
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        await connection.DisconnectAsync(data.Profile.Id, default);
        await service.RefreshAsync(default);
        Assert.Equal(CodexQuotaStatus.SignedOut, service.Snapshot.Status);
        Assert.False(UsageAccountOverview.CanDisplay(new(data.Profile, service.Snapshot)));
        Assert.Equal(CodexQuotaStatus.SignedOut, provider.Create(data.Profile).Snapshot.Status);
        Assert.Equal(23.5, data.Store().Read().State!.LastGood!.FiveHour!.UsedPercentage);
        input.Position = 0;
        Assert.Equal(1, await ClaudeStatusLineBridge.RunAsync(options, input, new StringWriter(), data.Accounts, cli, data.Clock));
        var reconnected = await connection.ConnectAsync(data.Profile.Id, AppFor(data), false, null, default);
        Assert.True(reconnected.Success); Assert.Equal(DirectoryFor(data), reconnected.Binding!.ConfigDirectory);
        await service.RefreshAsync(default);
        Assert.False(service.Snapshot.HasUsablePercentages);
        input.Position = 0;
        Assert.Equal(1, await ClaudeStatusLineBridge.RunAsync(options, input, new StringWriter(), data.Accounts, cli, data.Clock));
        var refreshedCommand = JsonNode.Parse(File.ReadAllText(Settings(data)))!["statusLine"]!["command"]!.GetValue<string>();
        Assert.True(ClaudeStatusLineInstaller.TryRead(refreshedCommand, out options));
        input.Position = 0;
        Assert.Equal(0, await ClaudeStatusLineBridge.RunAsync(options!, input, new StringWriter(), data.Accounts, cli, data.Clock));
        await service.RefreshAsync(default);
        Assert.True(UsageAccountOverview.CanDisplay(new(data.Profile, service.Snapshot)));
    }

    private sealed class BlockingCli(string root) : IClaudeCli
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public string? FindExecutable() => System.IO.Path.Combine(root, "synthetic.exe");
        public async Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? directory, bool login, CancellationToken token)
        {
            Calls++;
            if (login) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Auth;
        }
    }

    private sealed class FakeCli : IClaudeCli
    {
        private readonly string _executable;
        public ClaudeAuthentication Response { get; set; } = Auth;
        public bool LastLogin { get; private set; }
        public string? LastDirectory { get; private set; }
        public FakeCli(string root)
        {
            _executable = System.IO.Path.Combine(root, "claude.cmd"); File.WriteAllText(_executable, "@exit /b 1");
        }
        public string? FindExecutable() => _executable;
        public Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory, bool login, CancellationToken token)
        { token.ThrowIfCancellationRequested(); LastLogin = login; LastDirectory = configDirectory; return Task.FromResult(Response); }
    }
}
