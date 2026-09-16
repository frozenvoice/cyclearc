using System.Text.Json.Nodes;
using CycleArc.Providers.Claude;
using static CycleArc.Tests.ClaudeConnectionTests;

namespace CycleArc.Tests;

public sealed class ClaudeExecutableMigrationTests
{
    [Fact]
    public async Task InspectMigratesOwnedStatusLineAndFailureHookWhenTheOldExecutableIsGone()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = Path.Combine(data.Root, "Claude home");
        var oldExecutable = Path.Combine(data.Root, "previous", "CycleArc.exe");
        var currentExecutable = Path.Combine(data.Root, "current", "CycleArc.exe");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(Path.GetDirectoryName(oldExecutable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(currentExecutable)!);
        File.WriteAllText(oldExecutable, "old");
        File.WriteAllText(currentExecutable, "current");
        var unrelated = new JsonObject
        {
            ["matcher"] = "Bash",
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "echo keep" })
        };
        File.WriteAllText(Path.Combine(config, "settings.json"), new JsonObject
        {
            ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = "echo previous", ["padding"] = 2 },
            ["theme"] = "keep",
            ["hooks"] = new JsonObject { ["StopFailure"] = new JsonArray(unrelated) }
        }.ToJsonString());

        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, oldExecutable, false, config, default)).Success);

        var settingsPath = Path.Combine(config, "settings.json");
        var previousStatusLine = ReadStatusOptions(config).PreviousStatusLine!["command"]!.GetValue<string>();

        File.Delete(oldExecutable);
        var migrated = await new ClaudeConnectionService(data.Accounts, cli, data.Clock, currentExecutable)
            .InspectAsync(data.Profile.Id, default);

        Assert.Equal(ClaudeAuthStatus.SignedIn, migrated.Authentication.Status);
        Assert.Equal(currentExecutable, ReadStatusOptions(config).CycleArcExecutable);
        Assert.Equal(previousStatusLine, ReadStatusOptions(config).PreviousStatusLine!["command"]!.GetValue<string>());
        Assert.Equal("keep", ReadSettings(config)["theme"]!.GetValue<string>());

        var stop = ReadSettings(config)["hooks"]!["StopFailure"]!.AsArray();
        Assert.Contains(stop, entry => JsonNode.DeepEquals(entry, unrelated));
        var ownedFailure = Assert.Single(stop, IsOwnedFailureHook);
        Assert.True(ClaudeFailureCommand.TryRead(
            ownedFailure!["hooks"]![0]!["command"]!.GetValue<string>(), out var failure));
        Assert.Equal(currentExecutable, failure!.CycleArcExecutable);
        Assert.Equal(migrated.Binding!.BindingGeneration, failure.BindingGeneration);
    }

    [Fact]
    public async Task FailedOrMismatchedAuthenticationLeavesTheOldOwnedWrappersUntouched()
    {
        foreach (var response in new[]
        {
            new ClaudeAuthentication(ClaudeAuthStatus.SignedOut),
            ClaudeAuthentication.Parse(AuthJson.Replace("synthetic-org", "other-org", StringComparison.Ordinal), 0)
        })
        {
            using var data = new ClaudeStatusLineTests.ClaudeTestData();
            var config = Path.Combine(data.Root, "Claude home");
            var oldExecutable = Path.Combine(data.Root, "previous", "CycleArc.exe");
            var currentExecutable = Path.Combine(data.Root, "current", "CycleArc.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(oldExecutable)!);
            Directory.CreateDirectory(Path.GetDirectoryName(currentExecutable)!);
            File.WriteAllText(oldExecutable, "old");
            File.WriteAllText(currentExecutable, "current");

            var cli = new FakeCli(data.Root);
            var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
            Assert.True((await connection.ConnectAsync(data.Profile.Id, oldExecutable, false, config, default)).Success);
            var settingsPath = Path.Combine(config, "settings.json");
            var beforeSettings = File.ReadAllBytes(settingsPath);
            var beforeBinding = new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding;
            cli.Response = response;

            await new ClaudeConnectionService(data.Accounts, cli, data.Clock, currentExecutable)
                .InspectAsync(data.Profile.Id, default);

            Assert.Equal(beforeSettings, File.ReadAllBytes(settingsPath));
            Assert.Equal(beforeBinding, new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read().Binding);
            Assert.Equal(oldExecutable, ReadStatusOptions(config).CycleArcExecutable);
        }
    }

    [Fact]
    public async Task DisconnectedBindingDoesNotMigrateAStaleOwnedWrapper()
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = Path.Combine(data.Root, "Claude home");
        var oldExecutable = Path.Combine(data.Root, "previous", "CycleArc.exe");
        var currentExecutable = Path.Combine(data.Root, "current", "CycleArc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(oldExecutable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(currentExecutable)!);
        File.WriteAllText(oldExecutable, "old");
        File.WriteAllText(currentExecutable, "current");
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, oldExecutable, false, config, default)).Success);

        var store = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        var binding = store.Read().Binding!;
        store.Save(binding with { Disconnected = true });
        File.Delete(oldExecutable);
        var before = File.ReadAllBytes(Path.Combine(config, "settings.json"));

        var overview = await new ClaudeConnectionService(data.Accounts, cli, data.Clock, currentExecutable)
            .InspectAsync(data.Profile.Id, default);

        Assert.True(overview.Binding!.Disconnected);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(config, "settings.json")));
        Assert.Equal(oldExecutable, ReadStatusOptions(config).CycleArcExecutable);
    }

    [Theory]
    [InlineData("CycleArc.exe")]
    [InlineData("missing\\CycleArc.exe")]
    [InlineData("current\\cyclearc.cmd")]
    [InlineData("relative\\CycleArc.exe")]
    public async Task InvalidCallbackExecutableKeepsLegacyInspectionBehavior(string supplied)
    {
        using var data = new ClaudeStatusLineTests.ClaudeTestData();
        var config = Path.Combine(data.Root, "Claude home");
        var oldExecutable = Path.Combine(data.Root, "previous", "CycleArc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(oldExecutable)!);
        File.WriteAllText(oldExecutable, "old");
        var cli = new FakeCli(data.Root);
        var connection = new ClaudeConnectionService(data.Accounts, cli, data.Clock);
        Assert.True((await connection.ConnectAsync(data.Profile.Id, oldExecutable, false, config, default)).Success);
        var settingsPath = Path.Combine(config, "settings.json");
        var before = File.ReadAllBytes(settingsPath);
        File.Delete(oldExecutable);

        await new ClaudeConnectionService(data.Accounts, cli, data.Clock, supplied)
            .InspectAsync(data.Profile.Id, default);

        // A null/invalid callback path intentionally falls back to the old behavior,
        // which requires the old executable to remain available.
        Assert.Equal(before, File.ReadAllBytes(settingsPath));
        Assert.Equal(Path.Combine(data.Root, "previous", "CycleArc.exe"), ReadStatusOptions(config).CycleArcExecutable);
    }

    private static JsonObject ReadSettings(string directory) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")))!.AsObject();

    private static ClaudeBridgeOptions ReadStatusOptions(string directory)
    {
        var command = ReadSettings(directory)["statusLine"]!["command"]!.GetValue<string>();
        Assert.True(ClaudeStatusLineInstaller.TryRead(command, out var options));
        return options!;
    }

    private static bool IsOwnedFailureHook(JsonNode? entry) =>
        entry is JsonObject item && item["hooks"] is JsonArray hooks && hooks.Count == 1
        && hooks[0] is JsonObject command && command["command"] is JsonValue value
        && value.TryGetValue<string>(out var text) && ClaudeFailureCommand.TryRead(text, out _);

    private sealed class FakeCli(string root) : IClaudeCli
    {
        private readonly string _executable = CreateExecutable(root);
        public ClaudeAuthentication Response { get; set; } = ClaudeAuthentication.Parse(AuthJson, 0);
        public string? FindExecutable() => _executable;
        public Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory,
            bool login, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Response);
        }

        private static string CreateExecutable(string root)
        {
            var path = Path.Combine(root, "claude.cmd");
            File.WriteAllText(path, "@exit /b 1");
            return path;
        }
    }
}
