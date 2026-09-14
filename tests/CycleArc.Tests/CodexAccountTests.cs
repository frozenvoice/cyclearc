using CycleArc.Codex;
using System.Diagnostics;

namespace CycleArc.Tests;

public class CodexAccountTests
{
    [Theory]
    [InlineData("{}", CodexQuotaStatus.ProtocolMismatch)]
    [InlineData("{\"result\":{}}", CodexQuotaStatus.ProtocolMismatch)]
    [InlineData("{\"result\":{\"account\":null}}", CodexQuotaStatus.ProtocolMismatch)]
    [InlineData("{\"result\":{\"account\":null,\"requiresOpenaiAuth\":true}}", CodexQuotaStatus.SignedOut)]
    [InlineData("{\"result\":{\"account\":{\"type\":\"apiKey\"},\"requiresOpenaiAuth\":true}}", CodexQuotaStatus.Unavailable)]
    [InlineData("{\"result\":{\"account\":{\"type\":\"future\"},\"requiresOpenaiAuth\":true}}", CodexQuotaStatus.ProtocolMismatch)]
    [InlineData("{\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":null,\"planType\":\"pro\"},\"requiresOpenaiAuth\":true}}", CodexQuotaStatus.Available)]
    [InlineData("{\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":27,\"planType\":\"pro\"},\"requiresOpenaiAuth\":true}}", CodexQuotaStatus.ProtocolMismatch)]
    public void AccountProjectionUsesGeneratedShape(string json, CodexQuotaStatus expected) =>
        Assert.Equal(expected, CodexAccountIdentity.Parse(JsonNode.Parse(json)).Status);

    [Fact]
    public void ChildEnvironmentSeparatesHomesWithoutChangingParent()
    {
        var original = Environment.GetEnvironmentVariable("CODEX_HOME");
        var firstHome = Path.Combine(Path.GetTempPath(), "first profile");
        var secondHome = Path.Combine(Path.GetTempPath(), "second & profile");
        var command = new CodexLaunchCommand(@"C:\codex.exe", "app-server --stdio", @"C:\codex.exe", false);
        var first = CodexProcessFactory.CreateStartInfo(command with { CodexHome = firstHome, ManagedHome = true });
        var second = CodexProcessFactory.CreateStartInfo(command with { CodexHome = secondHome });
        Assert.Equal(firstHome, first.Environment["CODEX_HOME"]);
        Assert.Equal(secondHome, second.Environment["CODEX_HOME"]);
        Assert.Equal(original, Environment.GetEnvironmentVariable("CODEX_HOME"));
        Assert.Contains("cli_auth_credentials_store=file", first.Arguments);
        Assert.DoesNotContain("cli_auth_credentials_store", second.Arguments);
        Assert.False(first.Environment.ContainsKey("OPENAI_API_KEY"));
        Assert.False(first.Environment.ContainsKey("CODEX_API_KEY"));
        Assert.DoesNotContain(secondHome, second.Arguments); // no shell interpolation
        Assert.Equal(firstHome, first.WorkingDirectory);
        Assert.True(first.CreateNoWindow);
    }

    [Fact]
    public void NpmShimKeepsIsolationOptionsInsideOuterQuote()
    {
        var command = new CodexLaunchCommand(@"C:\Windows\System32\cmd.exe",
            CodexProcessQuoting.CmdLaunchArguments(@"C:\with space\codex.cmd"), @"C:\with space\codex.cmd", true)
            { CodexHome = Path.Combine(Path.GetTempPath(), "account"), ManagedHome = true };
        var start = CodexProcessFactory.CreateStartInfo(command);
        Assert.EndsWith(" -c analytics.enabled=false -c cli_auth_credentials_store=file\"", start.Arguments);
        Assert.StartsWith("/d /s /c \"\"C:\\with space\\codex.cmd\" app-server --stdio", start.Arguments);
    }

    [Fact]
    public void DiscoveryOnlyChecksKnownDirectoryReferencesAndDeduplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-home");
        var checkedPaths = new List<string>();
        var found = CodexHomeDiscovery.Candidates([path, path + Path.DirectorySeparatorChar, path.ToUpperInvariant(), "relative", null,
            Path.Combine(Path.GetTempPath(), "missing")], value => { checkedPaths.Add(value); return value == path; });
        Assert.Equal(new[] { path }, found);
        Assert.Equal(2, checkedPaths.Count);
        Assert.All(checkedPaths, value => Assert.DoesNotContain("auth", value));
    }

    [Fact]
    public void MigratesReferencesWithoutTouchingLegacySettingsOrQuota()
    {
        using var data = new AccountTestDirectory();
        var legacyPath = Path.Combine(data.Root, "codex-snapshot.json");
        File.WriteAllText(legacyPath, "synthetic old quota");
        File.WriteAllText(Path.Combine(data.Root, "settings.json"), "synthetic preferences");
        var store = new CodexAccountStore(data.Root);
        var state = store.LoadOrMigrate(data.Home("existing"));
        Assert.Single(state.Profiles);
        Assert.Equal("default", state.SelectedId);
        Assert.Equal(legacyPath, store.SnapshotPath(state.Profiles[0]));
        Assert.Equal("synthetic old quota", File.ReadAllText(legacyPath));
        Assert.Equal("synthetic preferences", File.ReadAllText(Path.Combine(data.Root, "settings.json")));
        Assert.Equal(state.Profiles[0], store.LoadOrMigrate(data.Home("different-env")).Profiles[0]);
    }

    [Fact]
    public void RegistrySupportsManyProfilesAndRecoversPreviousGoodBackup()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var initial = store.LoadOrMigrate(data.Home("existing"));
        var profiles = initial.Profiles.Concat(Enumerable.Range(0, 12).Select(i => store.NewManaged("Account " + i))).ToArray();
        var saved = initial with { Profiles = profiles, SelectedId = profiles[8].Id };
        store.Save(saved);
        store.Save(saved); // both primary and backup now contain the complete list
        File.WriteAllText(Path.Combine(data.Root, "codex-accounts.json"), "{invalid");
        var recovered = store.LoadOrMigrate(data.Home("ignored"));
        Assert.True(store.RecoveredFromBackup);
        Assert.Equal(13, recovered.Profiles.Count);
        Assert.Equal(profiles[8].Id, recovered.SelectedId);
        Assert.Equal(13, recovered.Profiles.Select(store.SnapshotPath).Distinct().Count());
        Assert.Equal(13, recovered.Profiles.Select(p => p.HomePath).Distinct().Count());
    }

    [Fact]
    public void UnknownOrCorruptRegistryCannotSilentlyBecomeNewEmptyAccounts()
    {
        using var data = new AccountTestDirectory();
        var path = Path.Combine(data.Root, "codex-accounts.json");
        const string future = "{\"Version\":99,\"Profiles\":[],\"SelectedId\":\"\"}";
        File.WriteAllText(path, future);
        Assert.Throws<InvalidDataException>(() => new CodexAccountStore(data.Root).LoadOrMigrate(data.Home("existing")));
        Assert.Equal(future, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("../../settings")]
    [InlineData("DEFAULT")]
    public void InvalidProfileIdsCannotRedirectCachePaths(string id)
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        Assert.Throws<InvalidDataException>(() => store.SnapshotPath(new(id, data.Root, "")));
    }

    [Fact]
    public void DuplicateHomesAndManagedPathRedirectionAreRejected()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var state = store.LoadOrMigrate(data.Home("existing"));
        var duplicate = new CodexAccountProfile(Guid.NewGuid().ToString("N"), state.Profiles[0].HomePath.ToUpperInvariant(), "");
        Assert.Throws<InvalidDataException>(() => store.Save(state with { Profiles = [state.Profiles[0], duplicate] }));
        var redirected = store.NewManaged("Other") with { HomePath = data.Home("existing") };
        Assert.Throws<InvalidDataException>(() => store.Save(state with { Profiles = [redirected], SelectedId = redirected.Id }));
    }

    [Fact]
    public async Task DifferentIdentityWithFailedQuotaNeverUsesPreviousAccountsCache()
    {
        using var data = new AccountTestDirectory();
        var profile = new CodexAccountProfile("default", data.Home("existing"), "");
        var currentEmail = "first@example.invalid";
        var failQuota = false;
        var factory = new ScriptedCodexProcessFactory { Responder = line =>
        {
            var method = JsonNode.Parse(line)?["method"]?.ToString();
            if (method == "account/read") return [AccountTestProtocol.Account(currentEmail)];
            if (method == "account/rateLimits/read" && failQuota) return ["""{"id":3,"error":{"code":-1}}"""];
            return AccountTestProtocol.Standard(line);
        }};
        var service = data.Service(profile, factory);
        await service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);
        Assert.Equal(25, service.Snapshot.Windows[0].UsedPercent);
        currentEmail = "second@example.invalid";
        failQuota = true;
        await service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);
        Assert.Empty(service.Snapshot.Windows);
        Assert.Null(service.Snapshot.LastSuccessfulRefresh);
        // A foreign account must not be exposed under the existing profile.
        Assert.Null(service.Identity);
        var reloaded = data.Service(profile, factory);
        Assert.Empty(reloaded.Snapshot.Windows);
        var cache = File.ReadAllText(new CodexAccountStore(data.Root).SnapshotPath(profile));
        Assert.DoesNotContain("@", cache);
    }

    [Fact]
    public async Task SameIdentityTransientFailureKeepsItsOwnStaleQuotaAcrossRestart()
    {
        using var data = new AccountTestDirectory();
        var profile = new CodexAccountProfile("default", data.Home("existing"), "");
        var factory = new ScriptedCodexProcessFactory { Responder = AccountTestProtocol.Standard };
        var service = data.Service(profile, factory);
        await service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);
        var timestamp = service.Snapshot.LastSuccessfulRefresh;
        service = data.Service(profile, factory);
        // Restart keeps the verified cache on disk, but hides it until the same identity is validated again.
        Assert.Equal(CodexQuotaStatus.Unavailable, service.Snapshot.Status);
        Assert.Empty(service.Snapshot.Windows);
        factory.Responder = line => JsonNode.Parse(line)?["method"]?.ToString() == "account/rateLimits/read"
            ? ["""{"id":3,"error":{"code":-1}}"""] : AccountTestProtocol.Standard(line);
        await service.RefreshAsync(AccountTestDirectory.Executable, CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        Assert.Equal(25, service.Snapshot.Windows[0].UsedPercent);
        Assert.Equal(timestamp, service.Snapshot.LastSuccessfulRefresh);
    }

    [Fact]
    public async Task IdentityCheckedBeforeResetCreditConsumption()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = AccountTestProtocol.Standard };
        var outcome = await new CodexAppServerClient(factory).ConsumeCreditAsync(AccountTestProtocol.Command, "test", "credit", "key",
            CancellationToken.None, new CodexAccountIdentity(CodexQuotaStatus.Available, "different@example.invalid", "pro").Fingerprint, true);
        Assert.Equal(CreditRedemptionOutcome.Unavailable, outcome);
        Assert.DoesNotContain(factory.LastProcess!.Received, line => line.Contains("/consume"));
    }
}

internal static class AccountTestProtocol
{
    public const string LoginId = "3e2f5a8d-9263-49f5-8407-9fa2e9468434";
    public static readonly CodexLaunchCommand Command = new("synthetic.exe", "app-server --stdio", "synthetic.exe", false);
    public static string Account(string? email = "synthetic@example.invalid") => new JsonObject
    {
        ["id"] = 2, ["result"] = new JsonObject { ["account"] = new JsonObject
            { ["type"] = "chatgpt", ["email"] = email, ["planType"] = "pro" }, ["requiresOpenaiAuth"] = true }
    }.ToJsonString();
    public static IReadOnlyList<string> Standard(string line) => JsonNode.Parse(line)?["method"]?.ToString() switch
    {
        "initialize" => ["""{"id":1,"result":{"userAgent":"synthetic-codex"}}"""],
        "account/read" => [Account()],
        "account/rateLimits/read" => ["""{"id":3,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":10080,"resetsAt":1999999999},"secondary":null},"rateLimitsByLimitId":null,"rateLimitResetCredits":null}}"""],
        "account/login/start" => ["""{"id":10,"result":{"type":"chatgpt","loginId":"3e2f5a8d-9263-49f5-8407-9fa2e9468434","authUrl":"https://auth.openai.com/oauth/authorize?synthetic=true"}}"""],
        "account/login/cancel" => ["""{"id":11,"result":{"status":"canceled"}}"""],
        _ => []
    };
    public const string Completed = """{"method":"account/login/completed","params":{"loginId":"3e2f5a8d-9263-49f5-8407-9fa2e9468434","success":true,"error":null,"onboardingEntrypoint":null}}""";
}

internal sealed class AccountTestDirectory : IDisposable
{
    public const string Executable = @"C:\Tools\codex.exe";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "CycleArc-accounts-" + Guid.NewGuid().ToString("N"));
    public AccountTestDirectory() => Directory.CreateDirectory(Root);
    public string Home(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
    public CodexQuotaService Service(CodexAccountProfile profile, ICodexProcessFactory factory)
    {
        var files = new MemoryCodexFileSystem();
        files.Files.Add(Executable);
        return new(new CodexExecutableLocator(files), new CodexAppServerClient(factory),
            new CodexSnapshotStore(new CodexAccountStore(Root).SnapshotPath(profile)), "test", profile: profile);
    }
    public void Dispose() => Directory.Delete(Root, true);
}
