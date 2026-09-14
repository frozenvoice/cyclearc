using System.Text;
using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Services;
using CycleArc.Providers.Usage;

namespace CycleArc.Tests;

public sealed class ClaudeFailureTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuthFailureIsProjectedSeparatelyAndSurvivesAValidCachedQuotaSample()
    {
        using var data = new TestData();
        var quota = new ClaudeStatusLineStore(data.Accounts.ClaudeStatusLinePath(data.Profile.Id), data.Profile.Id);
        await quota.RecordAsync(new ClaudeStatusLineResult(ClaudeInputStatus.Available,
            new ClaudeRateLimit(23.5, Now.AddHours(5).ToUnixTimeSeconds()), null), Now, default);
        var quotaBytes = File.ReadAllBytes(data.Accounts.ClaudeStatusLinePath(data.Profile.Id));
        var failures = new ClaudeFailureStore(data.Accounts);
        await failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.AuthRequired, Now.AddMinutes(1));

        var service = new ClaudeQuotaService(quota, data.Clock,
            () => new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read(), _ => "person@example.invalid", failures);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        Assert.Equal("claude-auth-required", service.Snapshot.TechnicalDetail);
        Assert.Equal(23.5, service.Snapshot.Windows[0].UsedPercent);
        Assert.True(service.IsConnected);
        Assert.Equal(quotaBytes, File.ReadAllBytes(data.Accounts.ClaudeStatusLinePath(data.Profile.Id)));

        // A cached-looking statusLine callback does not prove that OAuth was repaired.
        await quota.RecordAsync(new ClaudeStatusLineResult(ClaudeInputStatus.Available,
            new ClaudeRateLimit(25, Now.AddHours(5).ToUnixTimeSeconds()), null), Now.AddMinutes(2), default);
        await service.RefreshAsync(default);
        Assert.Equal(CodexQuotaStatus.Stale, service.Snapshot.Status);
        Assert.Equal("claude-auth-required", service.Snapshot.TechnicalDetail);
        Assert.Equal(25, service.Snapshot.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task ExplicitRecoveryForCurrentGenerationClearsFailureWithoutChangingQuotaReceipt()
    {
        using var data = new TestData();
        var quota = new ClaudeStatusLineStore(data.Accounts.ClaudeStatusLinePath(data.Profile.Id), data.Profile.Id);
        await quota.RecordAsync(new ClaudeStatusLineResult(ClaudeInputStatus.Available,
            new ClaudeRateLimit(12, Now.AddHours(5).ToUnixTimeSeconds()), null), Now, default);
        var failures = new ClaudeFailureStore(data.Accounts);
        await failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.AuthRequired, Now.AddMinutes(1));
        var before = File.ReadAllBytes(data.Accounts.ClaudeStatusLinePath(data.Profile.Id));
        await failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.None, Now.AddMinutes(2));
        var service = new ClaudeQuotaService(quota, data.Clock,
            () => new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read(), _ => "person@example.invalid", failures);
        Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
        Assert.Null(service.Snapshot.TechnicalDetail);
        Assert.Equal(before, File.ReadAllBytes(data.Accounts.ClaudeStatusLinePath(data.Profile.Id)));
    }

    [Fact]
    public async Task AuthenticationFailureCannotBeHiddenByLaterGenericFailure()
    {
        using var data = new TestData();
        var store = new ClaudeFailureStore(data.Accounts);
        await store.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.AuthRequired, Now.AddMinutes(1));
        await store.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.BridgeUnavailable, Now.AddMinutes(2));
        Assert.Equal(ClaudeFailureKind.AuthRequired, store.Read(data.Profile.Id).State!.Kind);
        await store.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.None, Now.AddMinutes(3));
        Assert.Equal(ClaudeFailureKind.None, store.Read(data.Profile.Id).State!.Kind);
    }
    [Fact]
    public async Task FailureCallbackRequiresCurrentBindingGenerationAndNeverPersistsHookDetails()
    {
        using var data = new TestData();
        var options = data.Options(data.Generation);
        var secret = "synthetic-secret-transcript";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            hook_event_name = "StopFailure", error = "authentication_failed", error_details = secret,
            last_assistant_message = secret, transcript_path = "C:/private/session.jsonl"
        })));
        Assert.Equal(0, await ClaudeFailureBridge.RunAsync(options, input, new StringWriter(), data.Accounts, data.Clock));
        var stored = File.ReadAllText(data.Accounts.ClaudeFailurePath(data.Profile.Id));
        Assert.DoesNotContain(secret, stored);
        Assert.DoesNotContain("transcript", stored);
        Assert.Equal(ClaudeFailureKind.AuthRequired, new ClaudeFailureStore(data.Accounts).Read(data.Profile.Id).State!.Kind);

        var old = data.Options(Guid.NewGuid().ToString("N"));
        using var rejectedInput = new MemoryStream(Encoding.UTF8.GetBytes("{\"hook_event_name\":\"StopFailure\",\"error\":\"authentication_failed\"}"));
        Assert.Equal(1, await ClaudeFailureBridge.RunAsync(old, rejectedInput, new StringWriter(), data.Accounts, data.Clock));
        Assert.Equal(ClaudeFailureKind.AuthRequired, new ClaudeFailureStore(data.Accounts).Read(data.Profile.Id).State!.Kind);
    }

    [Fact]
    public async Task UnknownOrMalformedStopFailureDoesNotBecomeAuthenticationRequired()
    {
        using var data = new TestData();
        foreach (var json in new[]
        {
            "{}", "{\"hook_event_name\":\"StopFailure\",\"error\":\"future_error\"}",
            "{\"hook_event_name\":\"StopFailure\",\"error\":\"authentication_failed\",\"error\":\"unknown\"}"
        })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
            Assert.Equal(1, await ClaudeFailureBridge.RunAsync(data.Options(data.Generation), input, new StringWriter(), data.Accounts, data.Clock));
        }
        Assert.False(File.Exists(data.Accounts.ClaudeFailurePath(data.Profile.Id)));
    }


    [Fact]
    public async Task AuthenticationFailureWithoutQuotaRemainsVisibleAndUnknownAcrossRestart()
    {
        using var data = new TestData();
        var failures = new ClaudeFailureStore(data.Accounts);
        await failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.AuthRequired, Now);
        var quota = new ClaudeStatusLineStore(data.Accounts.ClaudeStatusLinePath(data.Profile.Id), data.Profile.Id);
        var service = new ClaudeQuotaService(quota, data.Clock,
            () => new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read(), _ => null, failures);
        Assert.True(service.IsConnected);
        Assert.Equal(CodexQuotaStatus.Unavailable, service.Snapshot.Status);
        Assert.Equal("claude-auth-required", service.Snapshot.TechnicalDetail);
        Assert.False(service.Snapshot.HasUsablePercentages);
        var view = new CodexAccountView(data.Profile, service.Snapshot) { IsConnected = service.IsConnected };
        Assert.True(UsageAccountOverview.CanDisplay(view));
        Assert.False(view.IsAwaitingUsage);
    }

    [Fact]
    public async Task FailureOnANewBindingCannotRevealThePreviousBindingsQuota()
    {
        using var data = new TestData();
        var quota = new ClaudeStatusLineStore(data.Accounts.ClaudeStatusLinePath(data.Profile.Id), data.Profile.Id);
        await quota.RecordAsync(new(ClaudeInputStatus.Available, new(77, Now.AddHours(5).ToUnixTimeSeconds()), null), Now, default);
        var failures = new ClaudeFailureStore(data.Accounts);
        var service = new ClaudeQuotaService(quota, data.Clock,
            () => new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read(), _ => "person@example.invalid", failures);
        Assert.True(service.Snapshot.HasUsablePercentages);
        var bindings = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        bindings.Save(bindings.Read().Binding! with { ConnectedAt = Now.AddMinutes(1) });
        await failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.AuthRequired, Now.AddMinutes(2));
        await service.RefreshAsync(default);
        Assert.Equal("claude-auth-required", service.Snapshot.TechnicalDetail);
        Assert.Empty(service.Snapshot.Windows);
        Assert.Null(service.Snapshot.LastSuccessfulRefresh);
    }

    [Fact]
    public async Task FailureWaitingForTheStoreLockCannotWriteAfterGenerationRotation()
    {
        using var data = new TestData();
        var failures = new ClaudeFailureStore(data.Accounts);
        var path = data.Accounts.ClaudeFailurePath(data.Profile.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var pending = failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.AuthRequired, Now);
            Assert.False(pending.IsCompleted);
            var bindings = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
            var generation = Guid.NewGuid().ToString("N");
            bindings.Save(bindings.Read().Binding! with { BindingGeneration = generation });
            lease.Dispose();
            await Assert.ThrowsAsync<InvalidDataException>(() => pending);
            await failures.RecordAsync(data.Profile.Id, generation, ClaudeFailureKind.None, Now);
            Assert.Equal(generation, failures.Read(data.Profile.Id).State!.BindingGeneration);
            Assert.Equal(ClaudeFailureKind.None, failures.Read(data.Profile.Id).State!.Kind);
        }
        finally { lease.Dispose(); }
    }

    [Fact]
    public async Task FailureStoreKeepsMonotonicOrderingAndAllowsNewGenerationAfterClockRollback()
    {
        using var data = new TestData();
        var failures = new ClaudeFailureStore(data.Accounts);
        await failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.RequestFailed, Now.AddDays(1));
        await failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.None, Now);
        Assert.Equal(ClaudeFailureKind.RequestFailed, failures.Read(data.Profile.Id).State!.Kind);
        var bindings = new ClaudeConnectionStore(data.Accounts, data.Profile.Id);
        var generation = Guid.NewGuid().ToString("N");
        bindings.Save(bindings.Read().Binding! with { BindingGeneration = generation });
        await failures.RecordAsync(data.Profile.Id, generation, ClaudeFailureKind.None, Now);
        Assert.Equal(ClaudeFailureKind.None, failures.Read(data.Profile.Id).State!.Kind);
    }

    [Fact]
    public async Task DamagedFailureCacheDoesNotResurrectAnEarlierAuthenticationFailure()
    {
        using var data = new TestData();
        var failures = new ClaudeFailureStore(data.Accounts);
        await failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.AuthRequired, Now);
        await failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.None, Now.AddMinutes(1));
        File.WriteAllText(data.Accounts.ClaudeFailurePath(data.Profile.Id), "{broken");
        Assert.True(failures.Read(data.Profile.Id).Unavailable);
        var service = new ClaudeQuotaService(
            new(data.Accounts.ClaudeStatusLinePath(data.Profile.Id), data.Profile.Id), data.Clock,
            () => new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read(), _ => null, failures);
        Assert.True(service.IsConnected);
        Assert.Equal("claude-bridge-unavailable", service.Snapshot.TechnicalDetail);
    }

    [Fact]
    public async Task GenericFailureClearsOnLaterQuotaWithoutRequiringAuthentication()
    {
        using var data = new TestData();
        var failures = new ClaudeFailureStore(data.Accounts);
        await failures.RecordAsync(data.Profile.Id, data.Generation, ClaudeFailureKind.RequestFailed, Now);
        var quota = new ClaudeStatusLineStore(data.Accounts.ClaudeStatusLinePath(data.Profile.Id), data.Profile.Id);
        var service = new ClaudeQuotaService(quota, data.Clock,
            () => new ClaudeConnectionStore(data.Accounts, data.Profile.Id).Read(), _ => null, failures);
        Assert.Equal("claude-request-failed", service.Snapshot.TechnicalDetail);
        await quota.RecordAsync(new(ClaudeInputStatus.Available, new(12, Now.AddHours(5).ToUnixTimeSeconds()), null), Now, default);
        await service.RefreshAsync(default);
        Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
        Assert.Null(service.Snapshot.TechnicalDetail);
    }

    [Fact]
    public void ModifiedFailureCommandsAreNeverClaimedAsOwned()
    {
        using var data = new TestData();
        var options = data.Options(data.Generation);
        var command = ClaudeFailureCommand.Command(options);
        Assert.True(ClaudeFailureCommand.TryRead(command, out var decoded));
        Assert.Equal(options, decoded);
        var encoded = command.Split(' ')[^1];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        var changed = command[..^encoded.Length] + Convert.ToBase64String(Encoding.Unicode.GetBytes(script + "; Write-Output 'custom'"));
        Assert.False(ClaudeFailureCommand.TryRead(changed, out _));
    }

    [Theory]
    [InlineData("rate_limit")]
    [InlineData("server_error")]
    [InlineData("overloaded")]
    [InlineData("unknown")]
    public async Task DocumentedNonAuthenticationErrorsRemainGeneric(string error)
    {
        using var data = new TestData();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { hook_event_name = "StopFailure", error })));
        Assert.Equal(0, await ClaudeFailureBridge.RunAsync(data.Options(data.Generation), input, new StringWriter(), data.Accounts, data.Clock));
        Assert.Equal(ClaudeFailureKind.RequestFailed, new ClaudeFailureStore(data.Accounts).Read(data.Profile.Id).State!.Kind);
    }

    private sealed class TestData : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cyclearc-claude-failure-" + Guid.NewGuid().ToString("N"));
        public CodexAccountStore Accounts { get; }
        public CodexAccountProfile Profile { get; }
        public MutableClock Clock { get; } = new(Now);
        public string Generation { get; } = Guid.NewGuid().ToString("N");

        public TestData()
        {
            Accounts = new(Root);
            var registry = Accounts.LoadOrMigrate(Path.Combine(Root, "codex-home"));
            Profile = Accounts.NewClaude("Failure test");
            Accounts.Save(registry with { Version = 2, Profiles = registry.Profiles.Append(Profile).ToArray() });
            var config = Path.Combine(Root, "claude-home");
            Accounts.Save(Accounts.LoadOrMigrate(Path.Combine(Root, "codex-home")));
            new ClaudeConnectionStore(Accounts, Profile.Id).Save(new(1, Profile.Id, config,
                Path.Combine(Root, "claude.cmd"), false, new string('A', 64), Now, false, false, Generation));
        }

        public ClaudeFailureBridgeOptions Options(string generation) => new(1, Profile.Id,
            Path.Combine(Root, "claude-home"), Path.Combine(Root, "CycleArc.exe"), Root,
            new string('A', 64), generation);

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
