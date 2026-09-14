using CycleArc.Providers.Claude;
using System.Text;
using System.Text.Json.Nodes;

namespace CycleArc.Tests;

public sealed class ClaudeIdentityBindingTests
{
    private const string Prefix = "{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"apiProvider\":\"firstParty\",\"email\":\"person@example.invalid\",\"orgId\":\"synthetic-org\",\"subscriptionType\":";

    [Fact]
    public void StableFingerprintIgnoresPlanChangesButRetainsLegacyEvidence()
    {
        var pro = Parse("\"pro\"}");
        var max = Parse("\"max\"}");

        Assert.Equal(pro.Fingerprint, max.Fingerprint);
        Assert.Equal(pro.StableFingerprint, max.StableFingerprint);
        Assert.NotEqual(pro.LegacyFingerprint, max.LegacyFingerprint);
        Assert.Equal("synthetic-org", pro.OrganizationId);
    }

    [Fact]
    public void LegacyBindingMigratesAcrossKnownPlanChange()
    {
        var old = Parse("\"pro\"}");
        var current = Parse("\"max\"}");
        var binding = new ClaudeConnectionBinding(1, Guid.NewGuid().ToString("N"),
            @"C:\Claude", @"C:\Claude\claude.exe", false, old.LegacyFingerprint!,
            DateTimeOffset.UtcNow, Plan: "pro");

        Assert.True(ClaudeIdentityBinding.Matches(current, binding));
        Assert.True(ClaudeIdentityBinding.TryMigrate(current, binding, out var migrated));
        Assert.Equal(2, migrated.Version);
        Assert.Equal(current.StableFingerprint, migrated.IdentityFingerprint);
        Assert.Equal("max", migrated.Plan);
    }

    [Fact]
    public void LegacyBindingWithUnknownHistoricalPlanFailsClosed()
    {
        var old = Parse("\"custom_legacy\"}");
        var current = Parse("\"max\"}");
        var binding = new ClaudeConnectionBinding(1, Guid.NewGuid().ToString("N"),
            @"C:\Claude", @"C:\Claude\claude.exe", false, old.LegacyFingerprint!,
            DateTimeOffset.UtcNow);

        Assert.False(ClaudeIdentityBinding.Matches(current, binding));
        Assert.False(ClaudeIdentityBinding.TryMigrate(current, binding, out _));
    }

    [Fact]
    public void DifferentEmailOrOrganizationNeverMatchesStableBinding()
    {
        var original = Parse("\"pro\"}");
        var differentEmail = ClaudeAuthentication.Parse(Prefix.Replace("person@example.invalid", "other@example.invalid") + "\"pro\"}", 0);
        var differentOrganization = ClaudeAuthentication.Parse(Prefix.Replace("synthetic-org", "other-org") + "\"pro\"}", 0);
        var binding = new ClaudeConnectionBinding(2, Guid.NewGuid().ToString("N"),
            @"C:\Claude", @"C:\Claude\claude.exe", false, original.Fingerprint!, DateTimeOffset.UtcNow);

        Assert.False(ClaudeIdentityBinding.Matches(differentEmail, binding));
        Assert.False(ClaudeIdentityBinding.Matches(differentOrganization, binding));
    }

    [Fact]
    public void AutomaticWrapperUsesOnePayloadAndStillReadsLegacyWrapper()
    {
        var profileId = Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claude-wrapper-test"));
        var executable = Path.Combine(directory, "CycleArc.exe");
        var options = new ClaudeBridgeOptions(1, profileId, directory, executable,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), true,
            new JsonObject { ["type"] = "command", ["command"] = new string('x', 1000) },
            Guid.NewGuid().ToString("N"));

        var command = ClaudeStatusLineInstaller.Command(options);
        Assert.True(command.Length <= 8000);
        Assert.True(ClaudeStatusLineInstaller.TryRead(command, out var decoded));
        Assert.Equal(options with { PreviousStatusLine = null }, decoded! with { PreviousStatusLine = null });
        Assert.True(JsonNode.DeepEquals(options.PreviousStatusLine, decoded!.PreviousStatusLine));

        var legacy = LegacyCommand(options);
        Assert.True(legacy.Length > command.Length);
        Assert.True(legacy.Length > 8191);
        Assert.True(ClaudeStatusLineInstaller.TryRead(legacy, out decoded));
        Assert.Equal(options with { PreviousStatusLine = null }, decoded! with { PreviousStatusLine = null });
        Assert.True(JsonNode.DeepEquals(options.PreviousStatusLine, decoded!.PreviousStatusLine));
    }

    [Fact]
    public async Task OversizeWrapperPreservesExistingSettings()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var directory = Path.Combine(data.Root, "claude-home");
        Directory.CreateDirectory(directory);
        var settings = Path.Combine(directory, "settings.json");
        var original = new JsonObject
        {
            ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = new string('x', 4000) },
            ["theme"] = "preserved"
        }.ToJsonString();
        File.WriteAllText(settings, original);
        var failure = await Assert.ThrowsAsync<ClaudeSetupException>(() => ClaudeStatusLineInstaller.InstallAsync(
            data.Accounts, data.Profile.Id, directory, Path.Combine(data.Root, "CycleArc.exe"), default));
        Assert.Equal(ClaudeSetupFailure.InvalidSettings, failure.Failure);
        Assert.Equal(original, File.ReadAllText(settings));
    }

    [Fact]
    public async Task ReauthenticationAcceptsAPlanChangeForTheSameAccount()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var cli = new IdentityCli(data.Root, Parse("\"pro\"}"));
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var directory = Path.Combine(data.Root, "Claude home");
        var app = Path.Combine(data.Root, "CycleArc.exe");
        var connected = await connection.ConnectAsync(data.Profile.Id, app, false, directory, default);
        Assert.True(connected.Success);
        var generation = connected.Binding!.BindingGeneration;

        cli.Response = Parse("\"max\"}");
        var result = await connection.ReauthenticateAsync(data.Profile.Id, app, default);

        Assert.True(result.Success);
        Assert.Equal(connected.Binding.IdentityFingerprint, result.Binding!.IdentityFingerprint);
        Assert.Equal("max", result.Binding.Plan);
        Assert.NotEqual(generation, result.Binding.BindingGeneration);
    }

    [Fact]
    public async Task LegacyInspectionMigratesWhenPersistedPlanProvidesEvidence()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var cli = new IdentityCli(data.Root, Parse("\"max\"}"));
        var directory = Path.Combine(data.Root, "Claude home");
        var executable = cli.FindExecutable()!;
        var old = Parse("\"pro\"}");
        var legacy = new ClaudeConnectionBinding(1, data.Profile.Id, directory, executable, false,
            old.LegacyFingerprint!, data.Clock.UtcNow, Plan: "pro");
        var store = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        store.Save(legacy);

        var overview = await new ClaudeConnectionService(data.Accounts, cli, data.Clock)
            .InspectAsync(data.Profile.Id, default);

        Assert.Equal(2, overview.Binding!.Version);
        Assert.Equal(cli.Response.StableFingerprint, overview.Binding.IdentityFingerprint);
        Assert.Equal("max", overview.Binding.Plan);
        Assert.Equal(2, store.Read().Binding!.Version);
    }

    [Fact]
    public async Task LegacyBindingWithoutPlanCanReceiveAfterAKnownPlanChange()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var cli = new IdentityCli(data.Root, Parse("\"max\"}"));
        var directory = Path.Combine(data.Root, "Claude home");
        var app = Path.Combine(data.Root, "CycleArc.exe");
        File.WriteAllText(app, "");
        var old = Parse("\"pro\"}");
        var binding = new ClaudeConnectionBinding(1, data.Profile.Id, directory, cli.FindExecutable()!, false,
            old.LegacyFingerprint!, data.Clock.UtcNow, BindingGeneration: Guid.NewGuid().ToString("N"));
        var legacyStore = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        legacyStore.Save(binding);
        var legacyJson = JsonNode.Parse(File.ReadAllText(legacyStore.PathName))!.AsObject();
        legacyJson.Remove("Plan");
        File.WriteAllText(legacyStore.PathName, legacyJson.ToJsonString());
        var options = await ClaudeStatusLineInstaller.InstallAsync(data.Accounts, data.Profile.Id,
            directory, app, default);
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":42.5,\"resets_at\":1894060800}}}"));
        using var output = new StringWriter();

        var code = await ClaudeStatusLineBridge.RunAsync(options, input, output,
            data.Accounts, cli, data.Clock);

        Assert.Equal(0, code);
        Assert.Equal(42.5, data.Store().Read().State!.LastGood!.FiveHour!.UsedPercentage);
        var overview = await new ClaudeConnectionService(data.Accounts, cli, data.Clock)
            .InspectAsync(data.Profile.Id, default);
        Assert.Equal(2, overview.Binding!.Version);
        Assert.Equal(cli.Response.StableFingerprint, overview.Binding.IdentityFingerprint);
    }

    [Fact]
    public async Task ReauthenticationRejectsAChangedOrganization()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var cli = new IdentityCli(data.Root, Parse("\"pro\"}"));
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var directory = Path.Combine(data.Root, "Claude home");
        var app = Path.Combine(data.Root, "CycleArc.exe");
        var connected = await connection.ConnectAsync(data.Profile.Id, app, false, directory, default);
        Assert.True(connected.Success);
        var before = new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding;
        cli.Response = ClaudeAuthentication.Parse(Prefix.Replace("synthetic-org", "other-org") + "\"pro\"}", 0);

        var result = await connection.ReauthenticateAsync(data.Profile.Id, app, default);

        Assert.False(result.Success);
        Assert.Equal(ClaudeSetupFailure.AlreadyLinked, result.Failure);
        Assert.Equal(before, new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding);
    }

    [Fact]
    public async Task DuplicateProfileSetupFailureRestoresAnUnmigratedLegacyBinding()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var cli = new IdentityCli(data.Root, Parse("\"max\"}"));
        var directory = Path.Combine(data.Root, "Claude home");
        var app = Path.Combine(data.Root, "CycleArc.exe");
        File.WriteAllText(app, "");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), "[]");
        var old = Parse("\"pro\"}");
        var original = new ClaudeConnectionBinding(1, data.Profile.Id, directory, cli.FindExecutable()!, false,
            old.LegacyFingerprint!, data.Clock.UtcNow, BindingGeneration: Guid.NewGuid().ToString("N"));
        var firstStore = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        firstStore.Save(original);
        var legacyJson = JsonNode.Parse(File.ReadAllText(firstStore.PathName))!.AsObject();
        legacyJson.Remove("Plan");
        File.WriteAllText(firstStore.PathName, legacyJson.ToJsonString());
        var second = data.Accounts.NewClaude("Second");
        var registry = data.Accounts.LoadOrMigrate("unused");
        data.Accounts.Save(registry with { Profiles = registry.Profiles.Append(second).ToArray() });

        var result = await new ClaudeConnectionService(data.Accounts, cli, data.Clock)
            .ConnectAsync(second.Id, app, false, directory, default);

        Assert.False(result.Success);
        Assert.Equal(ClaudeSetupFailure.InvalidSettings, result.Failure);
        Assert.Equal(original, firstStore.Read().Binding);
        Assert.Null(new ClaudeConnectionStore(data.Accounts, second.Id).Read().Binding);
    }

    private static ClaudeAuthentication Parse(string subscriptionType) =>
        ClaudeAuthentication.Parse(Prefix + subscriptionType, 0);

    private static string LegacyCommand(ClaudeBridgeOptions options)
    {
        const string prefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
        var payload = ClaudeStatusLineInstaller.Payload(options);
        var path = options.CycleArcExecutable.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var script = "# CycleArc automatic statusLine v1\n# " + payload + "\n"
            + "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::InputEncoding; "
            + "$input | & '" + path + "' '" + ClaudeStatusLineBridge.Argument + "' '" + payload
            + "' | ForEach-Object { $_ }; exit $LASTEXITCODE";
        return prefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private sealed class IdentityCli : IClaudeCli
    {
        private readonly string _executable;
        public ClaudeAuthentication Response { get; set; }

        public IdentityCli(string root, ClaudeAuthentication response)
        {
            _executable = Path.Combine(root, "claude.cmd");
            File.WriteAllText(_executable, "@exit /b 1");
            Response = response;
        }

        public string? FindExecutable() => _executable;
        public Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory,
            bool login, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Response);
        }
    }
}
