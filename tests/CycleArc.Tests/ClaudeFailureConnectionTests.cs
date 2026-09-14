using System.Text.Json;
using System.Text.Json.Nodes;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Services;
using static CycleArc.Tests.ClaudeConnectionTests;

namespace CycleArc.Tests;

public sealed class ClaudeFailureConnectionTests
{
    [Fact]
    public async Task LegacySignedOutBindingGetsRecoveryStateWithoutInstallingNewHook()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var directory = Path.Combine(data.Root, "Claude home");
        Directory.CreateDirectory(directory);
        var cliPath = Path.Combine(data.Root, "claude.cmd");
        File.WriteAllText(cliPath, "@exit /b 1");
        var legacy = new ClaudeConnectionBinding(1, data.Profile.Id, directory, cliPath, false,
            ClaudeAuthentication.Parse(AuthJson, 0).Fingerprint!, data.Clock.UtcNow);
        new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Save(legacy);
        File.WriteAllText(Path.Combine(directory, "settings.json"),
            ClaudeStatusLineCommand.SettingsJson(Path.Combine(data.Root, "CycleArc.exe"), data.Profile.Id, data.Root));
        var cli = new FakeCli(data.Root) { Response = new(ClaudeAuthStatus.SignedOut) };

        var overview = await new ClaudeConnectionService(data.Accounts, cli, data.Clock)
            .InspectAsync(data.Profile.Id, default);

        Assert.Equal(ClaudeFailureKind.AuthRequired, overview.FailureKind);
        Assert.NotNull(new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding!.BindingGeneration);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")))!.AsObject();
        Assert.False(settings.ContainsKey("hooks"));
        Assert.False(ClaudeStatusLineInstaller.TryReadOwnedStatusLine(directory, data.Profile.Id, out _));
    }

    [Fact]
    public async Task ReauthenticationUsesSameBindingAndRotatesOnlyGeneration()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var directory = Path.Combine(data.Root, "Claude home");
        var connected = await connection.ConnectAsync(data.Profile.Id, Path.Combine(data.Root, "CycleArc.exe"), false,
            directory, default);
        Assert.True(connected.Success);
        var before = connected.Binding!;
        var settingsBefore = File.ReadAllBytes(Path.Combine(before.ConfigDirectory, "settings.json"));

        var result = await connection.ReauthenticateAsync(data.Profile.Id, Path.Combine(data.Root, "CycleArc.exe"), default);

        Assert.True(result.Success);
        Assert.Equal(before.ProfileId, result.Binding!.ProfileId);
        Assert.Equal(before.ConfigDirectory, result.Binding.ConfigDirectory);
        Assert.Equal(before.ConnectedAt, result.Binding.ConnectedAt);
        Assert.NotEqual(before.BindingGeneration, result.Binding.BindingGeneration);
        Assert.True(cli.LastLogin);
        Assert.Equal(before.ConfigDirectory, cli.LastDirectory);
        Assert.NotEqual(settingsBefore, File.ReadAllBytes(Path.Combine(before.ConfigDirectory, "settings.json")));
        Assert.Equal(ClaudeFailureKind.None, new ClaudeFailureStore(data.Accounts).Read(data.Profile.Id).State!.Kind);
    }

    [Fact]
    public async Task ReauthenticationDifferentIdentityDoesNotReplaceBindingOrSettings()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var directory = Path.Combine(data.Root, "Claude home");
        var connected = await connection.ConnectAsync(data.Profile.Id, Path.Combine(data.Root, "CycleArc.exe"), false,
            directory, default);
        Assert.True(connected.Success);
        var before = connected.Binding!;
        var settings = File.ReadAllBytes(Path.Combine(before.ConfigDirectory, "settings.json"));
        cli.Response = ClaudeAuthentication.Parse("""{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"other@example.invalid","orgId":"synthetic-org","subscriptionType":"pro"}""", 0);

        var result = await connection.ReauthenticateAsync(data.Profile.Id, Path.Combine(data.Root, "CycleArc.exe"), default);

        Assert.False(result.Success);
        var after = new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding!;
        Assert.Equal(before, after);
        Assert.Equal(settings, File.ReadAllBytes(Path.Combine(before.ConfigDirectory, "settings.json")));
        Assert.Equal(ClaudeFailureKind.IdentityMismatch, new ClaudeFailureStore(data.Accounts).Read(data.Profile.Id).State!.Kind);
    }

    [Fact]
    public async Task StopFailureHookIsAddedOnceAndUnrelatedHooksSurviveDisconnect()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var directory = Path.Combine(data.Root, "Claude home");
        Directory.CreateDirectory(directory);
        var unrelated = new JsonObject
        {
            ["matcher"] = "Bash",
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "echo keep" })
        };
        File.WriteAllText(Path.Combine(directory, "settings.json"), new JsonObject
        {
            ["hooks"] = new JsonObject { ["StopFailure"] = new JsonArray(unrelated), ["PreToolUse"] = new JsonArray(unrelated.DeepClone()) }
        }.ToJsonString());
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, Path.Combine(data.Root, "CycleArc.exe"), false, directory, default)).Success);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")))!.AsObject();
        var stop = settings["hooks"]!["StopFailure"]!.AsArray();
        Assert.Equal(2, stop.Count);
        Assert.Contains(stop, item => item!["matcher"]?.GetValue<string>() == "Bash");
        await connection.DisconnectAsync(data.Profile.Id, default);
        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")))!.AsObject();
        Assert.Single(after["hooks"]!["StopFailure"]!.AsArray());
        Assert.Equal("Bash", after["hooks"]!["StopFailure"]![0]!["matcher"]!.GetValue<string>());
        Assert.NotNull(after["hooks"]!["PreToolUse"]);
    }


    [Fact]
    public async Task LegacyAutomaticWrapperUpgradesWithoutLosingItsIdentityOrSettings()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var (binding, file, app) = LegacySetup(data);
        var original = File.ReadAllText(file);
        Assert.True(ClaudeStatusLineInstaller.TryReadOwnedStatusLine(binding.ConfigDirectory, binding.ProfileId, out var old));
        Assert.Null(old!.BindingGeneration);
        var connection = new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock);
        var overview = await connection.InspectAsync(data.Profile.Id, default);
        Assert.True(overview.Installed);
        Assert.NotNull(overview.Binding!.BindingGeneration);
        Assert.Equal(binding.ConnectedAt, overview.Binding.ConnectedAt);
        Assert.Equal(binding.IdentityFingerprint, overview.Binding.IdentityFingerprint);
        Assert.NotEqual(original, File.ReadAllText(file));
        var first = File.ReadAllBytes(file);
        await connection.InspectAsync(data.Profile.Id, default);
        Assert.Equal(first, File.ReadAllBytes(file));
        Assert.Single(JsonNode.Parse(File.ReadAllText(file))!["hooks"]!["StopFailure"]!.AsArray());
    }

    [Theory]
    [InlineData(ClaudeAuthStatus.TimedOut)]
    [InlineData(ClaudeAuthStatus.Cancelled)]
    public async Task FailedLegacyInspectionDoesNotInvalidateItsWorkingWrapper(ClaudeAuthStatus status)
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var (binding, file, _) = LegacySetup(data);
        var before = File.ReadAllBytes(file);
        var cli = new FakeCli(data.Root) { Response = new(status) };
        await new ClaudeConnectionService(data.Accounts, cli, data.Clock).InspectAsync(data.Profile.Id, default);
        Assert.Equal(binding, new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.True(ClaudeStatusLineInstaller.TryReadOwnedStatusLine(binding.ConfigDirectory, binding.ProfileId, out _));
    }

    [Fact]
    public async Task UserReplacedStatusLineIsPreservedWhileOwnedFailureHookIsRemoved()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var directory = Path.Combine(data.Root, "Claude home");
        var app = Path.Combine(data.Root, "CycleArc.exe");
        var connection = new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, app, false, directory, default)).Success);
        var file = Path.Combine(directory, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var replacement = new JsonObject { ["type"] = "command", ["command"] = "echo user-replacement" };
        settings["statusLine"] = replacement.DeepClone();
        File.WriteAllText(file, settings.ToJsonString());
        var before = File.ReadAllBytes(file);
        await connection.InspectAsync(data.Profile.Id, default);
        Assert.Equal(before, File.ReadAllBytes(file));
        await connection.DisconnectAsync(data.Profile.Id, default);
        settings = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.True(JsonNode.DeepEquals(replacement, settings["statusLine"]));
        Assert.False(settings.ContainsKey("hooks"));
    }

    [Fact]
    public async Task EmptyHookCollectionsSurviveRepeatedSetupAndDisconnect()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var directory = Path.Combine(data.Root, "Claude home");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "settings.json");
        File.WriteAllText(file, """{"hooks":{"StopFailure":[],"PreToolUse":[]}}""");
        var app = Path.Combine(data.Root, "CycleArc.exe");
        var connection = new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, app, false, directory, default)).Success);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, app, false, directory, default)).Success);
        await connection.DisconnectAsync(data.Profile.Id, default);
        var hooks = JsonNode.Parse(File.ReadAllText(file))!["hooks"]!.AsObject();
        Assert.Empty(hooks["StopFailure"]!.AsArray());
        Assert.Empty(hooks["PreToolUse"]!.AsArray());
    }

    [Fact]
    public async Task ReauthenticationPreservesConfigAndQuotaOnSuccessAndCancellation()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var directory = Path.Combine(data.Root, "Claude home");
        var app = Path.Combine(data.Root, "CycleArc.exe");
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        var connected = await connection.ConnectAsync(data.Profile.Id, app, false, directory, default);
        Assert.True(connected.Success);
        var store = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        await data.Store().RecordAsync(new(ClaudeInputStatus.Available,
            new(23.5, data.Clock.UtcNow.AddHours(5).ToUnixTimeSeconds()), null), data.Clock.UtcNow, default);
        var quota = File.ReadAllBytes(data.Accounts.ClaudeStatusLinePath(data.Profile.Id));
        Assert.True((await connection.ReauthenticateAsync(data.Profile.Id, app, default)).Success);
        Assert.Equal(directory, cli.LastDirectory);
        Assert.Equal(quota, File.ReadAllBytes(data.Accounts.ClaudeStatusLinePath(data.Profile.Id)));
        var before = store.Read().Binding;
        var file = Path.Combine(directory, "settings.json");
        var settings = File.ReadAllBytes(file);
        using var cancellation = new CancellationTokenSource();
        cli.BeforeReturn = token => { cancellation.Cancel(); return Task.FromCanceled(token); };
        var result = await connection.ReauthenticateAsync(data.Profile.Id, app, cancellation.Token);
        Assert.False(result.Success);
        Assert.Equal(ClaudeAuthStatus.Cancelled, result.Authentication.Status);
        Assert.Equal(before, store.Read().Binding);
        Assert.Equal(settings, File.ReadAllBytes(file));
        Assert.Equal(quota, File.ReadAllBytes(data.Accounts.ClaudeStatusLinePath(data.Profile.Id)));
    }

    [Fact]
    public async Task ReauthenticationSetupFailureRollsBackBindingAndKeepsSettings()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var directory = Path.Combine(data.Root, "Claude home");
        var app = Path.Combine(data.Root, "CycleArc.exe");
        var connection = new ClaudeConnectionService(data.Accounts, new FakeCli(data.Root), data.Clock);
        var connected = await connection.ConnectAsync(data.Profile.Id, app, false, directory, default);
        Assert.True(connected.Success);
        var file = Path.Combine(directory, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        settings["hooks"] = null;
        File.WriteAllText(file, settings.ToJsonString());
        var before = File.ReadAllBytes(file);
        var result = await connection.ReauthenticateAsync(data.Profile.Id, app, default);
        Assert.False(result.Success);
        Assert.Equal(ClaudeSetupFailure.InvalidSettings, result.Failure);
        Assert.Equal(connected.Binding, new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    private static (ClaudeConnectionBinding Binding, string File, string App) LegacySetup(ClaudeStatusLineTests.ClaudeTestData data)
    {
        var directory = Path.Combine(data.Root, "Claude home");
        Directory.CreateDirectory(directory);
        var app = Path.Combine(data.Root, "CycleArc.exe");
        System.IO.File.WriteAllText(app, "");
        var cli = new FakeCli(data.Root);
        var binding = new ClaudeConnectionBinding(1, data.Profile.Id, directory, cli.FindExecutable()!, false,
            ClaudeAuthentication.Parse(AuthJson, 0).Fingerprint!, data.Clock.UtcNow);
        new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Save(binding);
        // Frozen v0.5.3 shape and command, intentionally independent of the new serializer.
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, ProfileId = data.Profile.Id, ConfigDirectory = directory, CycleArcExecutable = app,
            DataRoot = data.Root, HadStatusLine = false, PreviousStatusLine = (JsonObject?)null
        }));
        var script = "# CycleArc automatic statusLine v1\n# " + payload + "\n"
            + "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::InputEncoding; "
            + "$input | & '" + app.Replace('\\', '/').Replace("'", "''") + "' '--claude-statusline-bridge' '" + payload
            + "' | ForEach-Object { $_ }; exit $LASTEXITCODE";
        var command = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand "
            + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var file = Path.Combine(directory, "settings.json");
        System.IO.File.WriteAllText(file, JsonSerializer.Serialize(new { theme = "preserved", statusLine = new { type = "command", command } }));
        return (binding, file, app);
    }


    [Fact]
    public async Task ImplicitConfigurationIsPassedAsNullWithoutTouchingRealSettings()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var cli = new FakeCli(data.Root) { Response = new(ClaudeAuthStatus.Cancelled) };
        var store = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        var binding = new ClaudeConnectionBinding(1, data.Profile.Id, ClaudeConnectionPaths.ImplicitDirectory,
            cli.FindExecutable()!, false, ClaudeAuthentication.Parse(AuthJson, 0).Fingerprint!, data.Clock.UtcNow,
            UseDefaultConfig: true, BindingGeneration: Guid.NewGuid().ToString("N"));
        store.Save(binding);
        // The fake login cancels before installer access. Only the synthetic binding file is used.
        var result = await new ClaudeConnectionService(data.Accounts, cli, data.Clock)
            .ReauthenticateAsync(data.Profile.Id, Path.Combine(data.Root, "CycleArc.exe"), default);
        Assert.False(result.Success);
        Assert.Equal(ClaudeAuthStatus.Cancelled, result.Authentication.Status);
        Assert.Null(cli.LastDirectory);
        Assert.True(cli.LastLogin);
        Assert.Equal(binding, store.Read().Binding);
    }

    private sealed class FakeCli : IClaudeCli
    {
        private readonly string _executable;
        public ClaudeAuthentication Response { get; set; } = ClaudeAuthentication.Parse(AuthJson, 0);
        public bool LastLogin { get; private set; }
        public Func<CancellationToken, Task>? BeforeReturn { get; set; }
        public string? LastDirectory { get; private set; }
        public FakeCli(string root)
        {
            _executable = Path.Combine(root, "claude.cmd");
            File.WriteAllText(_executable, "@exit /b 1");
        }
        public string? FindExecutable() => _executable;
        public async Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory, bool login, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LastLogin = login;
            LastDirectory = configDirectory;
            if (BeforeReturn is not null) await BeforeReturn(token);
            return Response;
        }
    }
}