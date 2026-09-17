using CycleArc.Codex;
using CycleArc.Providers.Claude;

namespace CycleArc.Tests;

/// <summary>
/// Removing an installation must take its own dead callbacks with it and nothing else:
/// no other installation's wrapper, no user-owned statusLine, no unrelated hook or property,
/// and no account, setting, cache or credential file.
/// </summary>
public sealed class ClaudeUninstallCleanupTests
{
    private const string AuthJson = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"person@example.invalid","orgId":"synthetic-org","subscriptionType":"pro","accessToken":"never-store-secret"}""";

    [Fact]
    public async Task RemovingTheInstallationRestoresAnAbsentStatusLineAndKeepsUnrelatedSettings()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        File.WriteAllText(SettingsPath(config), """{"theme":"dark","env":{"SYNTHETIC":"preserve"}}""");
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));

        var report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        Assert.True(report.Completed);
        Assert.Equal(ClaudeUninstallIncompleteReason.None, report.IncompleteReason);
        Assert.Equal(1, report.Restored);
        Assert.Equal(0, report.Failed);
        Assert.Equal(ClaudeUninstallStatus.Restored, Assert.Single(report.Profiles).Status);
        var settings = ReadSettings(config);
        Assert.False(settings.ContainsKey("statusLine"));
        Assert.False(settings.ContainsKey("hooks"));
        Assert.Equal("dark", settings["theme"]!.GetValue<string>());
        Assert.Equal("preserve", settings["env"]!["SYNTHETIC"]!.GetValue<string>());
        Assert.DoesNotContain(installation, File.ReadAllText(SettingsPath(config)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovingTheInstallationRestoresThePreviousUserStatusLineExactly()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        var original = JsonNode.Parse("""{"statusLine":{"type":"command","command":"cmd /c echo mine","padding":2},"theme":"light"}""")!;
        File.WriteAllText(SettingsPath(config), original.ToJsonString());
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));
        Assert.True(ClaudeStatusLineInstaller.IsInstalled(config, data.Profile.Id));

        await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(File.ReadAllText(SettingsPath(config)))));
    }

    [Fact]
    public async Task AStatusLineTheUserReplacedAfterConnectingIsNeverRewritten()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));
        var replaced = ReadSettings(config);
        replaced["statusLine"] = JsonNode.Parse("""{"type":"command","command":"cmd /c echo replaced"}""");
        File.WriteAllText(SettingsPath(config), replaced.ToJsonString());

        var report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        // The owned failure hook is still this installation's, so the run is a real change,
        // but the statusLine the user chose stays exactly as they wrote it.
        Assert.Equal(ClaudeUninstallStatus.Restored, Assert.Single(report.Profiles).Status);
        var settings = ReadSettings(config);
        Assert.Equal("cmd /c echo replaced", settings["statusLine"]!["command"]!.GetValue<string>());
        Assert.False(settings.ContainsKey("hooks"));
    }

    [Fact]
    public async Task OtherToolsHooksAndPropertiesSurviveRemoval()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        var unrelatedStop = JsonNode.Parse("""{"matcher":"Bash","hooks":[{"type":"command","command":"echo keep"}]}""")!;
        File.WriteAllText(SettingsPath(config), new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(JsonNode.Parse("""{"hooks":[{"type":"command","command":"echo other"}]}""")),
                ["StopFailure"] = new JsonArray(unrelatedStop.DeepClone()),
            },
            ["permissions"] = JsonNode.Parse("""{"allow":["Read"]}"""),
        }.ToJsonString());
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));

        await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        var settings = ReadSettings(config);
        var stop = settings["hooks"]!["StopFailure"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(unrelatedStop, Assert.Single(stop)));
        Assert.Single(settings["hooks"]!["PreToolUse"]!.AsArray());
        Assert.Equal("Read", settings["permissions"]!["allow"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task OnlyCallbacksInsideTheRemovedInstallationAreTouched()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var mine = ConfigDirectory(data, "claude-mine");
        var theirs = ConfigDirectory(data, "claude-theirs");
        var removed = Installation(data, "install");
        var other = Installation(data, "install-other");
        var otherProfile = AddClaudeProfile(data.Accounts, "Other installation");
        await ConnectAsync(data, data.Profile.Id, mine, Callback(removed));
        await ConnectAsync(data, otherProfile.Id, theirs, Callback(other));
        var untouched = File.ReadAllBytes(SettingsPath(theirs));

        var report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, removed, data.Clock);

        Assert.Equal(2, report.Inspected);
        Assert.Equal(1, report.Restored);
        Assert.Equal(ClaudeUninstallStatus.NotOwned,
            Assert.Single(report.Profiles, entry => entry.ProfileId == otherProfile.Id).Status);
        Assert.Equal(untouched, File.ReadAllBytes(SettingsPath(theirs)));
        Assert.True(ClaudeStatusLineInstaller.IsInstalled(theirs, otherProfile.Id));
        Assert.False(ClaudeStatusLineInstaller.IsInstalled(mine, data.Profile.Id));
    }

    [Fact]
    public async Task AWrapperAlreadyMigratedToANewerInstallationIsLeftAlone()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        var removed = Installation(data, "install");
        var replacement = Installation(data, "install-next");
        await ConnectAsync(data, data.Profile.Id, config, Callback(removed));
        // The same profile and configuration directory, now served by a different installation.
        await ConnectAsync(data, data.Profile.Id, config, Callback(replacement));
        var migrated = File.ReadAllBytes(SettingsPath(config));

        var report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, removed, data.Clock);

        Assert.Equal(ClaudeUninstallStatus.NotOwned, Assert.Single(report.Profiles).Status);
        Assert.Equal(migrated, File.ReadAllBytes(SettingsPath(config)));
        Assert.True(ClaudeStatusLineInstaller.IsInstalled(config, data.Profile.Id));
    }

    [Fact]
    public async Task RepeatedRemovalCleanupIsSafeAndReportsNothingLeftToDo()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        File.WriteAllText(SettingsPath(config), """{"statusLine":{"type":"command","command":"cmd /c echo mine"}}""");
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));
        await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);
        var restored = File.ReadAllBytes(SettingsPath(config));

        var again = await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        Assert.Equal(0, again.Restored);
        Assert.Equal(ClaudeUninstallStatus.NotOwned, Assert.Single(again.Profiles).Status);
        Assert.Equal(restored, File.ReadAllBytes(SettingsPath(config)));
    }

    [Fact]
    public async Task DamagedSettingsFailWithoutAnyRewriteAndAreReportedAsFailed()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));
        const string damaged = "{\"statusLine\": broken";
        File.WriteAllText(SettingsPath(config), damaged);

        var report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        Assert.Equal(ClaudeUninstallStatus.CleanupFailed, Assert.Single(report.Profiles).Status);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Restored);
        Assert.Equal(damaged, File.ReadAllText(SettingsPath(config)));
    }

    [Fact]
    public async Task ASettingsFileHeldByAnotherProcessIsReportedAsFailedAndPreserved()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));
        var before = File.ReadAllBytes(SettingsPath(config));

        ClaudeUninstallCleanupReport report;
        using (new FileStream(SettingsPath(config), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        Assert.Equal(ClaudeUninstallStatus.CleanupFailed, Assert.Single(report.Profiles).Status);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath(config)));
        Assert.True(ClaudeStatusLineInstaller.IsInstalled(config, data.Profile.Id));
    }

    [Fact]
    public async Task AnUnreadableConnectionRecordNeverGuessesAConfigurationDirectory()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));
        var installed = File.ReadAllBytes(SettingsPath(config));
        File.WriteAllText(data.Accounts.ClaudeConnectionPath(data.Profile.Id), "{not json");

        var report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        var entry = Assert.Single(report.Profiles);
        Assert.Equal(ClaudeUninstallStatus.ConnectionUnavailable, entry.Status);
        Assert.Null(entry.ConfigDirectory);
        Assert.Equal(installed, File.ReadAllBytes(SettingsPath(config)));
    }

    [Fact]
    public async Task ADamagedAccountRegistryAndBackupAreNeverReadAsHavingNoProfiles()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));
        var installed = File.ReadAllBytes(SettingsPath(config));
        var registry = Path.Combine(data.Root, "codex-accounts.json");
        File.WriteAllText(registry, "{damaged");
        File.WriteAllText(registry + ".bak", "{damaged too");

        var report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        // The callback stays behind because its owner cannot be confirmed. That is a worse
        // outcome than a clean removal, so it is recorded as one rather than hidden.
        Assert.False(report.Completed);
        Assert.Equal(ClaudeUninstallIncompleteReason.AccountsUnavailable, report.IncompleteReason);
        Assert.Empty(report.Profiles);
        Assert.Equal(installed, File.ReadAllBytes(SettingsPath(config)));
        var receipt = JsonNode.Parse(File.ReadAllText(ClaudeUninstallCleanup.ReportPath(data.Accounts)))!;
        Assert.False(receipt["Completed"]!.GetValue<bool>());
        Assert.Equal(nameof(ClaudeUninstallIncompleteReason.AccountsUnavailable),
            receipt["IncompleteReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAccountRegistryHeldByAnotherProcessIsReportedAsUnavailable()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));
        var installed = File.ReadAllBytes(SettingsPath(config));
        var registry = Path.Combine(data.Root, "codex-accounts.json");

        ClaudeUninstallCleanupReport report;
        using (new FileStream(registry, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (new FileStream(registry + ".bak", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        Assert.False(report.Completed);
        Assert.Equal(ClaudeUninstallIncompleteReason.AccountsUnavailable, report.IncompleteReason);
        Assert.Equal(installed, File.ReadAllBytes(SettingsPath(config)));
        Assert.True(ClaudeStatusLineInstaller.IsInstalled(config, data.Profile.Id));
    }

    [Fact]
    public void AnAbsentAccountRegistryIsNothingToCleanUpRatherThanAFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "cyclearc-uninstall-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profiles = new CodexAccountStore(root).ReadClaudeProfiles();
            Assert.True(profiles.Available);
            Assert.Empty(profiles.Ids);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AccountSettingsAndConnectionFilesAreNeverDeletedOrRewritten()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = ConfigDirectory(data, "claude-home");
        var installation = Installation(data, "install");
        await ConnectAsync(data, data.Profile.Id, config, Callback(installation));
        var preserved = new[]
        {
            Path.Combine(data.Root, "codex-accounts.json"),
            data.Accounts.ClaudeConnectionPath(data.Profile.Id),
        };
        var before = preserved.ToDictionary(path => path, File.ReadAllBytes);

        var report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, installation, data.Clock);

        foreach (var path in preserved)
        {
            Assert.True(File.Exists(path));
            Assert.Equal(before[path], File.ReadAllBytes(path));
        }
        var receipt = File.ReadAllText(ClaudeUninstallCleanup.ReportPath(data.Accounts));
        Assert.Contains("Restored", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("person@", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("never-store", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell.exe", receipt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(report.CompletedAt, data.Clock.UtcNow);
    }

    [Theory]
    [InlineData("install", "install/current/CycleArc.exe", true)]
    [InlineData("install", "install/CycleArc.exe", true)]
    [InlineData("install", "install-other/current/CycleArc.exe", false)]
    [InlineData("install", "other/CycleArc.exe", false)]
    public void OwnershipCoversTheRemovedTreeOnlyAndNeverASimilarlyNamedNeighbour(
        string root, string candidate, bool owned)
    {
        var parent = Path.Combine(Path.GetTempPath(), "cyclearc-ownership-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(owned, ClaudeUninstallCleanup.IsInsideInstallation(Path.Combine(parent, root),
            Path.Combine(parent, Path.Combine(candidate.Split('/')))));
    }

    [Fact]
    public async Task AnUnusableInstallationRootCleansNothingAndIsNotReportedAsComplete()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var report = await ClaudeUninstallCleanup.RunAsync(data.Accounts, "not-a-full-path", data.Clock);
        Assert.False(report.Completed);
        Assert.Equal(ClaudeUninstallIncompleteReason.InstallationRootUnusable, report.IncompleteReason);
        Assert.Empty(report.Profiles);
    }

    private static string ConfigDirectory(ClaudeStatusLineTests.ClaudeTestData data, string name)
    {
        var directory = Path.Combine(data.Root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Installation(ClaudeStatusLineTests.ClaudeTestData data, string name)
    {
        var root = Path.Combine(data.Root, name);
        Directory.CreateDirectory(Path.Combine(root, "current"));
        File.WriteAllText(Callback(root), name);
        return root;
    }

    private static string Callback(string installationRoot) =>
        Path.Combine(installationRoot, "current", "CycleArc.exe");

    private static string SettingsPath(string configDirectory) => Path.Combine(configDirectory, "settings.json");

    private static JsonObject ReadSettings(string configDirectory) =>
        JsonNode.Parse(File.ReadAllText(SettingsPath(configDirectory)))!.AsObject();

    private static CodexAccountProfile AddClaudeProfile(CodexAccountStore accounts, string label)
    {
        var state = accounts.LoadOrMigrate(Path.Combine(accounts.RootDirectory, "codex-home"));
        var profile = accounts.NewClaude(label);
        accounts.Save(state with { Version = 2, Profiles = state.Profiles.Append(profile).ToArray() });
        return profile;
    }

    private static async Task ConnectAsync(ClaudeStatusLineTests.ClaudeTestData data, string profileId,
        string configDirectory, string callbackExecutable)
    {
        var result = await new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock)
            .ConnectAsync(profileId, callbackExecutable, false, configDirectory, default);
        Assert.True(result.Success);
    }

    private sealed class FakeCli : IClaudeCli
    {
        private readonly string _executable;
        public FakeCli(string root)
        {
            _executable = Path.Combine(root, "claude.cmd");
            if (!File.Exists(_executable)) File.WriteAllText(_executable, "@exit /b 1");
        }
        public string? FindExecutable() => _executable;
        public Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory,
            bool login, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(ClaudeAuthentication.Parse(AuthJson, 0));
        }
    }
}
