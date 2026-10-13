using System.Text;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Services;

namespace CycleArc.Tests;

public class ClaudeResetTimingStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T10:00:00Z");

    [Fact]
    public async Task RestartRestoresOriginalResetProvenanceWithoutRenewingItsClock()
    {
        using var data = new Data();
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        await service.RefreshAsync(default);
        Assert.True(File.Exists(data.Timing.PathName));

        var restarted = data.Service();
        await restarted.RefreshAsync(default);
        Assert.Equal(51, Five(restarted).UsedPercent);
        Assert.Equal(Now.AddHours(2), Five(restarted).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Preserved, Five(restarted).ResetObservation!.State);
        Assert.Equal(ClaudeResetSource.Server, Five(restarted).ResetObservation!.Source);
        Assert.Equal(Now, Five(restarted).ResetObservation!.ObservedAt);
        Assert.Equal(1, data.Client.Calls);
    }

    [Fact]
    public async Task DurableDecreaseInvalidationSurvivesRestartAndOldLiveCacheReplay()
    {
        using var data = new Data();
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 1, 20));
        await service.RefreshAsync(default);
        Assert.Null(Five(service).ResetsAt);
        data.Clock.UtcNow = Now.AddMinutes(2);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 55, 21));
        var restarted = data.Service();
        await restarted.RefreshAsync(default);
        Assert.Null(Five(restarted).ResetsAt);
        Assert.Equal(Now.AddDays(3), Week(restarted).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.NotProvided, Five(restarted).ResetObservation!.State);
        Assert.Equal(1, data.Client.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImplausibleResetInOneWindowCannotPoisonTheOtherWindowsRestartEvidence(bool fiveInvalid)
    {
        using var data = new Data();
        data.Client.Next = new(new(Now,
            new(46, Now.AddHours(fiveInvalid ? 6 : 2)), new(19, Now.AddDays(fiveInvalid ? 3 : 8))));
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        Assert.Null((fiveInvalid ? Five(service) : Week(service)).ResetsAt);
        Assert.False(data.Timing.Read(data.Binding, Now).Unavailable);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        var restarted = data.Service();
        await restarted.RefreshAsync(default);
        Assert.Null((fiveInvalid ? Five(restarted) : Week(restarted)).ResetsAt);
        Assert.Equal(fiveInvalid ? Now.AddDays(3) : Now.AddHours(2),
            (fiveInvalid ? Week(restarted) : Five(restarted)).ResetsAt);
    }

    [Theory]
    [InlineData("live-auth")]
    [InlineData("live-identity")]
    [InlineData("hook-auth")]
    [InlineData("hook-identity")]
    public async Task AuthenticationBoundarySurvivesRecoveryAndRestartUntilANewDateIsSupplied(string failure)
    {
        using var data = new Data();
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = Now.AddMinutes(1);
        var failures = new ClaudeFailureStore(data.Accounts);
        if (failure.StartsWith("live-", StringComparison.Ordinal))
        {
            data.Client.Next = new(null, failure == "live-auth" ? "claude-live-auth-required" : "claude-live-identity-mismatch");
            await service.RefreshLiveAsync(default);
        }
        else
        {
            await failures.RecordAsync(data.Profile.Id, data.Binding.BindingGeneration!,
                failure == "hook-auth" ? ClaudeFailureKind.AuthRequired : ClaudeFailureKind.IdentityMismatch, data.Clock.UtcNow);
            await service.RefreshAsync(default);
        }
        var invalidatedBytes = File.ReadAllBytes(data.Timing.PathName);
        var invalidatedAt = File.GetLastWriteTimeUtc(data.Timing.PathName);
        for (var index = 0; index < 3; index++)
        {
            data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(2);
            await service.RefreshAsync(default);
        }
        Assert.Equal(invalidatedBytes, File.ReadAllBytes(data.Timing.PathName));
        Assert.Equal(invalidatedAt, File.GetLastWriteTimeUtc(data.Timing.PathName));
        data.Clock.UtcNow = Now.AddMinutes(2);
        if (failure.StartsWith("hook-", StringComparison.Ordinal))
            await failures.RecordAsync(data.Profile.Id, data.Binding.BindingGeneration!, ClaudeFailureKind.None, data.Clock.UtcNow);
        data.Client.Next = new(new(data.Clock.UtcNow, new(51, null), new(20, null)));
        await service.RefreshLiveAsync(default);
        Assert.Null(Five(service).ResetsAt);
        Assert.Null(Week(service).ResetsAt);
        var restarted = data.Service();
        await restarted.RefreshAsync(default);
        Assert.Null(Five(restarted).ResetsAt);
        Assert.Null(Week(restarted).ResetsAt);

        data.Clock.UtcNow = Now.AddMinutes(3);
        var newFive = data.Clock.UtcNow.AddHours(5);
        var newWeek = data.Clock.UtcNow.AddDays(7);
        data.Client.Next = new(new(data.Clock.UtcNow, new(52, newFive), new(21, newWeek)));
        await restarted.RefreshLiveAsync(default);
        Assert.Equal(newFive, Five(restarted).ResetsAt);
        Assert.Equal(newWeek, Week(restarted).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Direct, Five(restarted).ResetObservation!.State);
    }

    [Fact]
    public async Task LateAuthenticationReceiptCannotRevokeANewerServerDate()
    {
        using var data = new Data();
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = Now.AddMinutes(2);
        var reset = data.Clock.UtcNow.AddHours(5);
        data.Client.Next = new(new(data.Clock.UtcNow, new(51, reset), new(20, Now.AddDays(3))));
        await service.RefreshLiveAsync(default);
        await new ClaudeFailureStore(data.Accounts).RecordAsync(data.Profile.Id, data.Binding.BindingGeneration!,
            ClaudeFailureKind.AuthRequired, Now.AddMinutes(1));
        await service.RefreshAsync(default);
        Assert.Equal(reset, Five(service).ResetsAt);
        var restarted = data.Service();
        await restarted.RefreshAsync(default);
        Assert.Equal(reset, Five(restarted).ResetsAt);
        Assert.Equal(ClaudeResetObservationState.Direct, Five(restarted).ResetObservation!.State);
    }

    [Fact]
    public async Task UnchangedPassivePollsDoNotMutateTheLedgerOrLiveCache()
    {
        using var data = new Data();
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        await service.RefreshAsync(default);
        var timing = File.ReadAllBytes(data.Timing.PathName);
        var livePath = Path.Combine(Path.GetDirectoryName(data.Timing.PathName)!, "claude-live-usage.json");
        var live = File.ReadAllBytes(livePath);
        var marker = DateTime.Parse("2020-01-01T00:00:00Z", CultureInfo.InvariantCulture).ToUniversalTime();
        File.SetLastWriteTimeUtc(data.Timing.PathName, marker);
        File.SetLastWriteTimeUtc(livePath, marker);
        for (var index = 0; index < 4; index++)
        {
            data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(2);
            await service.RefreshAsync(default);
        }
        Assert.Equal(timing, File.ReadAllBytes(data.Timing.PathName));
        Assert.Equal(live, File.ReadAllBytes(livePath));
        Assert.Equal(marker, File.GetLastWriteTimeUtc(data.Timing.PathName));
        Assert.Equal(marker, File.GetLastWriteTimeUtc(livePath));
        Assert.Equal(1, data.Client.Calls);
    }

    [Fact]
    public async Task RapidOrdinaryDesktopObservationsCoalesceAndPassivePollFlushesTheLatestAtOneMinute()
    {
        using var data = new Data();
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        var initial = File.ReadAllBytes(data.Timing.PathName);
        var marker = DateTime.Parse("2020-01-01T00:00:00Z", CultureInfo.InvariantCulture).ToUniversalTime();
        File.SetLastWriteTimeUtc(data.Timing.PathName, marker);
        data.Clock.UtcNow = Now.AddSeconds(10);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        await service.RefreshAsync(default);
        data.Clock.UtcNow = Now.AddSeconds(20);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 52, 21));
        await service.RefreshAsync(default);
        Assert.Equal(initial, File.ReadAllBytes(data.Timing.PathName));
        Assert.Equal(marker, File.GetLastWriteTimeUtc(data.Timing.PathName));

        // No newer observation arrives. A due passive poll flushes the pending latest state.
        data.Clock.UtcNow = Now.AddMinutes(1);
        await service.RefreshAsync(default);
        Assert.NotEqual(marker, File.GetLastWriteTimeUtc(data.Timing.PathName));
        Assert.NotEqual(initial, File.ReadAllBytes(data.Timing.PathName));
        var saved = new ClaudeResetTimingStore(data.Accounts, data.Profile.Id).Read(data.Binding, data.Clock.UtcNow).State!;
        Assert.Equal(52, saved.FiveHour.Used);
        Assert.Equal(Now.AddSeconds(20), saved.FiveHour.ProgressAt);
        Assert.Equal(Now, saved.FiveHour.ResetObservedAt);
        Assert.Equal(1, data.Client.Calls);
    }

    [Fact]
    public async Task CounterDropInsideCoalescingMinutePersistsImmediatelyAndCannotResurrectAfterRestart()
    {
        using var data = new Data();
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        var initial = File.ReadAllBytes(data.Timing.PathName);
        var marker = DateTime.Parse("2020-01-01T00:00:00Z", CultureInfo.InvariantCulture).ToUniversalTime();
        File.SetLastWriteTimeUtc(data.Timing.PathName, marker);
        foreach (var seconds in new[] { 10, 20 })
        {
            data.Clock.UtcNow = Now.AddSeconds(seconds);
            data.Desktop.Next = new(new(data.Clock.UtcNow, 46 + seconds / 10, 19 + seconds / 10));
            await service.RefreshAsync(default);
        }
        Assert.Equal(initial, File.ReadAllBytes(data.Timing.PathName));
        data.Clock.UtcNow = Now.AddSeconds(21);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 1, 22));
        await service.RefreshAsync(default);
        Assert.NotEqual(marker, File.GetLastWriteTimeUtc(data.Timing.PathName));
        var saved = new ClaudeResetTimingStore(data.Accounts, data.Profile.Id).Read(data.Binding, data.Clock.UtcNow).State!;
        Assert.True(saved.FiveHour.Invalidated);
        Assert.Equal(Now.AddSeconds(21), saved.FiveHour.InvalidatedAt);

        data.Clock.UtcNow = Now.AddSeconds(22);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 55, 23));
        var restarted = data.Service();
        await restarted.RefreshAsync(default);
        Assert.Null(Five(restarted).ResetsAt);
        Assert.Equal(Now.AddDays(3), Week(restarted).ResetsAt);
        Assert.Equal(1, data.Client.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptOrMissingPrimaryCannotRestorePotentiallyRevokedEvidenceFromBackup(bool remove)
    {
        using var data = new Data();
        var service = data.Service();
        await service.RefreshLiveAsync(default);
        File.Copy(data.Timing.PathName, data.Timing.PathName + ".bak");
        data.Clock.UtcNow = Now.AddMinutes(1);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 1, 20));
        await service.RefreshAsync(default);
        if (remove) File.Delete(data.Timing.PathName);
        else File.WriteAllText(data.Timing.PathName, "{broken");

        data.Clock.UtcNow = Now.AddMinutes(2);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 55, 21));
        var restarted = data.Service();
        await restarted.RefreshAsync(default);
        Assert.All(restarted.Snapshot.Windows, window => Assert.Null(window.ResetsAt));
        Assert.Equal(1, data.Client.Calls);

        // A fresh verified server response can establish new evidence after quarantine.
        data.Clock.UtcNow = Now.AddMinutes(3);
        data.Client.Next = new(new(data.Clock.UtcNow, new(56, Now.AddHours(4)), new(22, Now.AddDays(4))));
        await restarted.RefreshLiveAsync(default);
        Assert.Equal(Now.AddHours(4), Five(restarted).ResetsAt);
        Assert.Equal(Now.AddDays(4), Week(restarted).ResetsAt);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("organization")]
    [InlineData("reconnect")]
    public async Task BindingChangesCannotReuseAnotherGenerationOrOrganizationLedger(string change)
    {
        using var data = new Data();
        await data.Service().RefreshLiveAsync(default);
        var changed = change switch
        {
            "generation" => data.Binding with { BindingGeneration = Guid.NewGuid().ToString("N") },
            "organization" => data.Binding with { IdentityFingerprint = ClaudeIdentity.StableFingerprint("reset-cache@example.invalid", "other-org") },
            _ => data.Binding with { ConnectedAt = Now.AddSeconds(1) }
        };
        data.Connections.Save(changed);
        data.Clock.UtcNow = Now.AddMinutes(1);
        Assert.True(data.Timing.Read(changed, data.Clock.UtcNow).Unavailable);
        Assert.Null(data.Timing.Read(changed, data.Clock.UtcNow).State);
        data.Desktop.Next = new(new(data.Clock.UtcNow, 51, 20));
        var restarted = data.Service();
        await restarted.RefreshAsync(default);
        Assert.All(restarted.Snapshot.Windows, window => Assert.Null(window.ResetsAt));
    }

    [Fact]
    public async Task ProjectionContainsOnlyTypedQuotaEvidenceAndLeavesOtherUserFilesUntouched()
    {
        using var data = new Data();
        var other = Path.Combine(Path.GetDirectoryName(data.Timing.PathName)!, "existing-user-data.json");
        File.WriteAllText(other, "{\"preserved\":true}");
        await data.Service().RefreshLiveAsync(default);
        var serialized = File.ReadAllText(data.Timing.PathName);
        Assert.DoesNotContain("reset-cache@example.invalid", serialized);
        Assert.DoesNotContain("org-reset-cache", serialized);
        Assert.DoesNotContain(data.Binding.ConfigDirectory, serialized);
        Assert.DoesNotContain("ExtraUsage", serialized);
        Assert.DoesNotContain("ResetCredits", serialized);
        Assert.Equal("{\"preserved\":true}", File.ReadAllText(other));
        Assert.InRange(new FileInfo(data.Timing.PathName).Length, 1, 8192);
    }

    [Fact]
    public async Task OldBindingCannotCommitAfterGenerationRotationAndTheExistingFileIsPreserved()
    {
        using var data = new Data();
        await data.Service().RefreshLiveAsync(default);
        var previous = File.ReadAllBytes(data.Timing.PathName);
        var state = data.Timing.Read(data.Binding, Now).State!;
        data.Connections.Save(data.Binding with { BindingGeneration = Guid.NewGuid().ToString("N") });
        await Assert.ThrowsAsync<IOException>(() => data.Timing.SaveAsync(data.Binding, state, default));
        Assert.Equal(previous, File.ReadAllBytes(data.Timing.PathName));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(data.Timing.PathName)!, "claude-reset-timing.json.*.tmp"));
    }

    [Fact]
    public async Task ConcurrentWriterCannotOverwriteANewerDurableInvalidationWithItsOlderRead()
    {
        using var data = new Data();
        await data.Service().RefreshLiveAsync(default);
        var writerA = new ClaudeResetTimingStore(data.Accounts, data.Profile.Id);
        var writerB = new ClaudeResetTimingStore(data.Accounts, data.Profile.Id);
        var stateA = writerA.Read(data.Binding, Now).State!;
        var stateB = writerB.Read(data.Binding, Now).State!;
        await writerB.SaveAsync(data.Binding, stateB with
        {
            FiveHour = stateB.FiveHour with { Invalidated = true, InvalidatedAt = Now }
        }, default);
        var invalidated = File.ReadAllBytes(data.Timing.PathName);
        await Assert.ThrowsAsync<IOException>(() => writerA.SaveAsync(data.Binding, stateA, default));
        Assert.Equal(invalidated, File.ReadAllBytes(data.Timing.PathName));
        Assert.True(new ClaudeResetTimingStore(data.Accounts, data.Profile.Id).Read(data.Binding, Now).State!.FiveHour.Invalidated);
    }

    [Theory]
    [InlineData("null-state")]
    [InlineData("null-window")]
    [InlineData("null-seen")]
    [InlineData("null-seen-entry")]
    [InlineData("invalid-source")]
    [InlineData("invalid-reset-source")]
    [InlineData("future-observation")]
    [InlineData("unknown-field")]
    public async Task MalformedBoundedEnvelopeIsUnavailableWithoutThrowing(string mutation)
    {
        using var data = new Data();
        await data.Service().RefreshLiveAsync(default);
        var root = JsonNode.Parse(File.ReadAllText(data.Timing.PathName))!.AsObject();
        var state = root["State"]!.AsObject();
        var five = state["FiveHour"]!.AsObject();
        switch (mutation)
        {
            case "null-state": root["State"] = null; break;
            case "null-window": state["FiveHour"] = null; break;
            case "null-seen": five["Seen"] = null; break;
            case "null-seen-entry": five["Seen"]!.AsArray()[0] = null; break;
            case "invalid-source": five["Seen"]!.AsArray()[0]!["Source"] = 999; break;
            case "invalid-reset-source": five["ResetSource"] = 999; break;
            case "future-observation": five["ProgressAt"] = Now.AddMinutes(1); break;
            default: root["Unknown"] = true; break;
        }
        File.WriteAllText(data.Timing.PathName, root.ToJsonString());
        var read = data.Timing.Read(data.Binding, data.Clock.UtcNow);
        Assert.True(read.Unavailable);
        Assert.Null(read.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8193)]
    public void EmptyOrOversizeEnvelopeIsUnavailable(int length)
    {
        using var data = new Data();
        File.WriteAllBytes(data.Timing.PathName, Encoding.UTF8.GetBytes(new string(' ', length)));
        var read = data.Timing.Read(data.Binding, Now);
        Assert.True(read.Unavailable);
        Assert.Null(read.State);
    }

    private static CodexQuotaWindow Five(ClaudeQuotaService service) =>
        Assert.Single(service.Snapshot.Windows, window => window.Kind == CodexWindowKind.FiveHour);
    private static CodexQuotaWindow Week(ClaudeQuotaService service) =>
        Assert.Single(service.Snapshot.Windows, window => window.Kind == CodexWindowKind.Weekly);

    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = Now; }
    private sealed class Client : IClaudeLiveUsageClient
    {
        public int Calls { get; private set; }
        public ClaudeLiveUsageResponse Next { get; set; } = new(new(Now, new(46, Now.AddHours(2)), new(19, Now.AddDays(3))));
        public Task<ClaudeLiveUsageResponse> FetchAsync(ClaudeConnectionBinding binding, CancellationToken token)
        { Calls++; return Task.FromResult(Next); }
    }
    private sealed class Desktop : IClaudeDesktopUsageSource
    {
        public ClaudeDesktopUsageUpdate Next { get; set; } = new(null);
        public Task<ClaudeDesktopUsageUpdate> RefreshAsync(ClaudeConnectionBinding? binding, CancellationToken token) => Task.FromResult(Next);
    }
    private sealed class Data : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "cyclearc-reset-timing-store-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public Client Client { get; } = new();
        public Desktop Desktop { get; } = new();
        public CodexAccountStore Accounts { get; }
        public CodexAccountProfile Profile { get; }
        public ClaudeConnectionBinding Binding { get; }
        public ClaudeConnectionStore Connections { get; }
        public ClaudeResetTimingStore Timing { get; }
        private ClaudeStatusLineStore Inbox { get; }

        public Data()
        {
            Accounts = new(Root);
            var initial = Accounts.LoadOrMigrate(Path.Combine(Root, "codex-home"));
            Profile = Accounts.NewClaude("Reset cache");
            Accounts.Save(initial with { Version = 3, Profiles = [Profile], SelectedId = Profile.Id });
            Binding = new(2, Profile.Id, Path.Combine(Root, "claude-home"), Path.Combine(Root, "claude.exe"), false,
                ClaudeIdentity.StableFingerprint("reset-cache@example.invalid", "org-reset-cache"), Now.AddDays(-1),
                BindingGeneration: Guid.NewGuid().ToString("N"), Plan: "pro");
            Connections = new(Accounts, Profile.Id);
            Connections.Save(Binding);
            Inbox = new(Accounts.ClaudeStatusLinePath(Profile.Id), Profile.Id);
            Timing = new(Accounts, Profile.Id);
        }
        public ClaudeQuotaService Service() => new(Inbox, Clock, Connections.Read,
            email: _ => "reset-cache@example.invalid", failureStore: new ClaudeFailureStore(Accounts),
            desktop: Desktop, live: new ClaudeLiveUsageCollector(Accounts, Profile.Id, Client, Clock), resetTiming: Timing);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
