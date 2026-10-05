using CycleArc.Codex;
using CycleArc.Observations;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Cursor;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class QuotaObservationManagerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T00:10:00Z");

    [Fact]
    public async Task StartupReplayAndUnchangedPassiveBindingsDoNotRecordSaveOrRecompute()
    {
        await using var fixture = new Fixture(UsageProviderId.Claude);
        var manager = fixture.Manager;
        var service = fixture.Service;
        await manager.WaitForObservationsIdleAsync();
        Assert.Empty(Points(manager.Selected!.Observations!));
        service.Emit(service.Snapshot);
        await manager.WaitForObservationsIdleAsync();
        var before = manager.ObservationStatistics;
        var history = manager.Selected!.Observations;
        for (var index = 0; index < 20; index++)
        {
            await manager.RefreshPassiveAsync(default);
            manager.Select(fixture.Profile.Id);
            Assert.Same(history, manager.Selected!.Observations);
        }
        await manager.WaitForObservationsIdleAsync();
        Assert.Equal(before.AcceptedSnapshots, manager.ObservationStatistics.AcceptedSnapshots);
        Assert.Equal(before.SaveAttempts, manager.ObservationStatistics.SaveAttempts);
        Assert.Equal(before.PublishedRevisions, manager.ObservationStatistics.PublishedRevisions);
        Assert.Equal(20, service.RefreshCalls);
        Assert.Equal(0, service.RemoteRequests);
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        service.Emit(Snapshot(fixture.Profile.Provider, fixture.Clock.UtcNow, 0));
        await manager.WaitForObservationsIdleAsync();
        Assert.Equal(0m, Assert.Single(Points(manager.Selected!.Observations!)).Value);
    }

    [Fact]
    public async Task GenerationChangeRejectsCachedReceiptAndDisconnectedViewHidesHistory()
    {
        await using var fixture = new Fixture(UsageProviderId.Cursor);
        var manager = fixture.Manager;
        var service = fixture.Service;
        await manager.WaitForObservationsIdleAsync();
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        var old = Snapshot(UsageProviderId.Cursor, fixture.Clock.UtcNow, 17);
        service.Emit(old);
        await manager.WaitForObservationsIdleAsync();
        Assert.Single(Points(manager.Selected!.Observations!));
        service.Connected = false;
        service.Emit(CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut) with { Provider = UsageProviderId.Cursor });
        Assert.Null(manager.Selected!.Observations);
        service.ObservationBindingKey = "different-generation";
        service.Connected = true;
        service.Emit(old);
        await manager.WaitForObservationsIdleAsync();
        Assert.Empty(Points(manager.Selected!.Observations!));
        fixture.Clock.UtcNow = Now.AddMinutes(2);
        service.Emit(Snapshot(UsageProviderId.Cursor, fixture.Clock.UtcNow, 18));
        await manager.WaitForObservationsIdleAsync();
        Assert.Equal(18m, Assert.Single(Points(manager.Selected!.Observations!)).Value);
    }

    [Fact]
    public async Task UncertainBindingMakesAGapWithoutAddingANumericSample()
    {
        await using var fixture = new Fixture(UsageProviderId.Claude);
        await fixture.Manager.WaitForObservationsIdleAsync();
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        fixture.Service.Emit(Snapshot(UsageProviderId.Claude, fixture.Clock.UtcNow, 10));
        await fixture.Manager.WaitForObservationsIdleAsync();
        var binding = fixture.Service.ObservationBindingKey;
        fixture.Service.ObservationBindingKey = null;
        fixture.Service.Emit(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable) with { Provider = UsageProviderId.Claude });
        fixture.Service.ObservationBindingKey = binding;
        fixture.Clock.UtcNow = Now.AddMinutes(2);
        fixture.Service.Emit(Snapshot(UsageProviderId.Claude, fixture.Clock.UtcNow, 11));
        await fixture.Manager.WaitForObservationsIdleAsync();
        var points = Points(fixture.Manager.Selected!.Observations!);
        Assert.Equal(2, points.Length);
        Assert.NotEqual(points[0].SegmentId, points[1].SegmentId);
    }

    [Fact]
    public async Task FailedCodexLoginKeepsHistoryButSuccessfulSameIdentityLoginStartsNewEpoch()
    {
        await using var fixture = new Fixture(UsageProviderId.Codex);
        var manager = fixture.Manager;
        await manager.WaitForObservationsIdleAsync();
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        fixture.Service.Emit(Snapshot(UsageProviderId.Codex, fixture.Clock.UtcNow, 25));
        await manager.WaitForObservationsIdleAsync();
        var original = manager.Selected!.Observations!;
        fixture.Service.LoginStatus = CodexQuotaStatus.Cancelled;
        Assert.Equal(CodexQuotaStatus.Cancelled,
            (await manager.LoginAsync(fixture.Profile.Id, "", (_, _) => Task.CompletedTask, default)).Status);
        Assert.Same(original, manager.Selected!.Observations);
        fixture.Service.LoginStatus = CodexQuotaStatus.Available;
        fixture.Clock.UtcNow = Now.AddMinutes(2);
        fixture.Service.NextRefresh = Snapshot(UsageProviderId.Codex, fixture.Clock.UtcNow, 26);
        Assert.Equal(CodexQuotaStatus.Available,
            (await manager.LoginAsync(fixture.Profile.Id, "", (_, _) => Task.CompletedTask, default)).Status);
        await manager.WaitForObservationsIdleAsync();
        var reset = manager.Selected!.Observations!;
        Assert.NotEqual(original.Context.BindingKey, reset.Context.BindingKey);
        Assert.Equal(26m, Assert.Single(Points(reset)).Value);
        await manager.StopObservationsAsync(TimeSpan.FromSeconds(5));
        var restarted = fixture.Restart();
        await restarted.WaitForObservationsIdleAsync();
        Assert.Equal(reset.Context.BindingKey, restarted.Selected!.Observations!.Context.BindingKey);
        Assert.Equal(26m, Assert.Single(Points(restarted.Selected.Observations)).Value);
        await restarted.StopObservationsAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(UsageProviderId.Codex)]
    [InlineData(UsageProviderId.Cursor)]
    public async Task CompletedSameIdentityReconnectCommitsEpochBeforeQueuedHistorySave(UsageProviderId provider)
    {
        using var releaseOldSave = new ManualResetEventSlim();
        using var releaseNewSave = new ManualResetEventSlim();
        var oldSaveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newSaveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase = 0;
        await using var fixture = new Fixture(provider, beforeIo: stage =>
        {
            if (stage != "save") return;
            if (Interlocked.CompareExchange(ref phase, 2, 1) == 1)
            {
                oldSaveEntered.TrySetResult();
                if (!releaseOldSave.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            else if (Volatile.Read(ref phase) == 2)
            {
                newSaveEntered.TrySetResult();
                if (!releaseNewSave.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
        });
        CodexAccountManager? restarted = null;
        try
        {
            await fixture.Manager.WaitForObservationsIdleAsync();
            fixture.Clock.UtcNow = Now.AddMinutes(1);
            fixture.Service.Emit(Snapshot(provider, fixture.Clock.UtcNow, 25));
            await fixture.Manager.WaitForObservationsIdleAsync();
            var original = fixture.Manager.Selected!.Observations!;
            var originalEpoch = File.ReadAllBytes(fixture.EpochPath);
            Volatile.Write(ref phase, 1);
            fixture.Service.Emit(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable) with { Provider = provider });
            await oldSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Clock.UtcNow = Now.AddMinutes(2);
            fixture.Service.NextRefresh = Snapshot(provider, fixture.Clock.UtcNow, 26);
            Task reconnect = provider == UsageProviderId.Codex
                ? fixture.Manager.LoginAsync(fixture.Profile.Id, "", (_, _) => Task.CompletedTask, default)
                : fixture.Manager.ConnectCursorAsync(fixture.Profile.Id, "", default);
            Assert.False(reconnect.IsCompleted);
            Assert.Equal(originalEpoch, File.ReadAllBytes(fixture.EpochPath));
            releaseOldSave.Set();
            await reconnect.WaitAsync(TimeSpan.FromSeconds(5));
            await newSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(original.Context.BindingKey, fixture.Manager.Selected!.Observations!.Context.BindingKey);
            Assert.Equal(26m, Assert.Single(Points(fixture.Manager.Selected.Observations)).Value);
            // Simulate loss of the old process before its pending trend save. The
            // restart can still see the older numeric file, but only the new epoch.
            restarted = fixture.Restart();
            await restarted.WaitForObservationsIdleAsync();
            Assert.NotEqual(original.Context.BindingKey, restarted.Selected!.Observations!.Context.BindingKey);
            Assert.DoesNotContain(Points(restarted.Selected.Observations), point => point.Value == 25m);
            Assert.Equal(CodexQuotaStatus.Available, restarted.Selected.Snapshot.Status);
        }
        finally
        {
            releaseOldSave.Set();
            releaseNewSave.Set();
            await fixture.Manager.WaitForObservationsIdleAsync();
            if (restarted is not null) await restarted.StopObservationsAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task FailedActiveCursorReconnectPreservesHistoryAndSuccessfulReconnectRotatesOnlyHistoryEpoch()
    {
        await using var fixture = new Fixture(UsageProviderId.Cursor);
        await fixture.Manager.WaitForObservationsIdleAsync();
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        fixture.Service.Emit(Snapshot(UsageProviderId.Cursor, fixture.Clock.UtcNow, 17));
        await fixture.Manager.WaitForObservationsIdleAsync();
        var original = fixture.Manager.Selected!.Observations!;
        var binding = fixture.Service.ObservationBindingKey;
        fixture.Service.ConnectSuccess = false;
        Assert.False((await fixture.Manager.ConnectCursorAsync(fixture.Profile.Id, "", default)).Success);
        Assert.Same(original, fixture.Manager.Selected!.Observations);
        fixture.Service.ConnectSuccess = true;
        fixture.Clock.UtcNow = Now.AddMinutes(2);
        fixture.Service.NextRefresh = Snapshot(UsageProviderId.Cursor, fixture.Clock.UtcNow, 18);
        Assert.True((await fixture.Manager.ConnectCursorAsync(fixture.Profile.Id, "", default)).Success);
        await fixture.Manager.WaitForObservationsIdleAsync();
        Assert.Equal(binding, fixture.Service.ObservationBindingKey);
        Assert.NotEqual(original.Context.BindingKey, fixture.Manager.Selected!.Observations!.Context.BindingKey);
        Assert.Equal(18m, Assert.Single(Points(fixture.Manager.Selected.Observations)).Value);
    }

    [Fact]
    public async Task ShutdownTimeoutDoesNotWaitForBlockedOptionalWarning()
    {
        using var releaseSave = new ManualResetEventSlim();
        using var releaseWarning = new ManualResetEventSlim();
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warningEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warningExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(UsageProviderId.Claude, warning: _ =>
        {
            warningEntered.TrySetResult();
            releaseWarning.Wait(TimeSpan.FromSeconds(10));
            warningExited.TrySetResult();
        }, beforeIo: stage =>
        {
            if (stage != "save") return;
            saveEntered.TrySetResult();
            if (!releaseSave.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        });
        try
        {
            await fixture.Manager.WaitForObservationsIdleAsync();
            fixture.Clock.UtcNow = Now.AddMinutes(1);
            fixture.Service.Emit(Snapshot(UsageProviderId.Claude, fixture.Clock.UtcNow, 10));
            await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Manager.StopObservationsAsync(TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(1));
            await warningEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(warningExited.Task.IsCompleted);
            Assert.True(fixture.Manager.ObservationStatistics.Failures > 0);
        }
        finally
        {
            releaseSave.Set();
            releaseWarning.Set();
            await fixture.Manager.WaitForObservationsIdleAsync();
            await warningExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StartupRemovesOnlyExactCrashLeftObservationTempsForKnownAndForgottenProfiles()
    {
        string[] owned = [], preserved = [], forgottenOwned = [];
        await using var fixture = new Fixture(UsageProviderId.Claude, beforeStart: prepared =>
        {
            (owned, preserved) = SeedTemporaryFiles(prepared.Root, prepared.Profile.Id);
            (forgottenOwned, _) = SeedTemporaryFiles(prepared.Root, Guid.NewGuid().ToString("N"));
        });
        await fixture.Manager.WaitForObservationsIdleAsync();
        Assert.All(owned.Concat(forgottenOwned), path => Assert.False(File.Exists(path)));
        Assert.All(preserved, path => Assert.Equal("synthetic unrelated file", File.ReadAllText(path)));
        Assert.Equal(0, fixture.Service.RemoteRequests);
        Assert.Equal(0, fixture.Manager.ObservationStatistics.SaveAttempts);
    }

    [Fact]
    public async Task RemovalRejectsAlreadyCapturedServiceCallbackAndPreservesOldQuotaFile()
    {
        await using var fixture = new Fixture(UsageProviderId.Claude);
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        fixture.Service.Emit(Snapshot(UsageProviderId.Claude, fixture.Clock.UtcNow, 15));
        await fixture.Manager.WaitForObservationsIdleAsync();
        var (ownedTemporary, preservedTemporary) = SeedTemporaryFiles(fixture.Root, fixture.Profile.Id);
        var late = fixture.Service.CaptureCallbacks();
        var cache = fixture.Store.SnapshotPath(fixture.Profile);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "synthetic current quota cache stays unchanged");
        Assert.True(fixture.Manager.Remove(fixture.Profile.Id));
        late?.Invoke(Snapshot(UsageProviderId.Claude, Now.AddMinutes(2), 99));
        await fixture.Manager.WaitForObservationsIdleAsync();
        Assert.Empty(fixture.Manager.Accounts);
        Assert.False(File.Exists(fixture.HistoryPath));
        Assert.False(File.Exists(fixture.HistoryPath + ".bak"));
        Assert.All(ownedTemporary, path => Assert.False(File.Exists(path)));
        Assert.All(preservedTemporary, path => Assert.Equal("synthetic unrelated file", File.ReadAllText(path)));
        Assert.Equal("synthetic current quota cache stays unchanged", File.ReadAllText(cache));
    }

    [Fact]
    public async Task SaveFailureAndThrowingWarningSubscriberCannotTurnHealthyQuotaIntoFailure()
    {
        await using var fixture = new Fixture(UsageProviderId.Cursor, _ => throw new InvalidOperationException("synthetic log failure"));
        await fixture.Manager.WaitForObservationsIdleAsync();
        Directory.CreateDirectory(fixture.HistoryPath);
        var registry = File.ReadAllBytes(Path.Combine(fixture.Root, "codex-accounts.json"));
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        fixture.Service.Emit(Snapshot(UsageProviderId.Cursor, fixture.Clock.UtcNow, 12));
        await fixture.Manager.WaitForObservationsIdleAsync();
        Assert.Equal(CodexQuotaStatus.Available, fixture.Manager.Selected!.Snapshot.Status);
        Assert.Single(Points(fixture.Manager.Selected.Observations!));
        Assert.True(fixture.Manager.Selected.ObservationStorageUnavailable);
        Assert.Equal(registry, File.ReadAllBytes(Path.Combine(fixture.Root, "codex-accounts.json")));
        Assert.True(fixture.Manager.ObservationStatistics.Failures > 0);
    }

    [Fact]
    public async Task CorruptCodexEpochDisablesOnlyHistoryAndSuccessfulLoginCanRepairIt()
    {
        await using var fixture = new Fixture(UsageProviderId.Codex);
        await fixture.Manager.StopObservationsAsync(TimeSpan.FromSeconds(5));
        File.WriteAllText(fixture.EpochPath, "{corrupt");
        var restarted = fixture.Restart();
        await restarted.WaitForObservationsIdleAsync();
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        fixture.Service.Emit(Snapshot(UsageProviderId.Codex, fixture.Clock.UtcNow, 18));
        await restarted.WaitForObservationsIdleAsync();
        Assert.Equal(CodexQuotaStatus.Available, restarted.Selected!.Snapshot.Status);
        Assert.Null(restarted.Selected.Observations);
        Assert.True(restarted.Selected.ObservationStorageUnavailable);
        fixture.Service.LoginStatus = CodexQuotaStatus.Available;
        fixture.Clock.UtcNow = Now.AddMinutes(2);
        fixture.Service.NextRefresh = Snapshot(UsageProviderId.Codex, fixture.Clock.UtcNow, 19);
        await restarted.LoginAsync(fixture.Profile.Id, "", (_, _) => Task.CompletedTask, default);
        await restarted.WaitForObservationsIdleAsync();
        Assert.False(restarted.Selected!.ObservationStorageUnavailable);
        Assert.Equal(19m, Assert.Single(Points(restarted.Selected.Observations!)).Value);
        await restarted.StopObservationsAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CorruptHistoryRestartDoesNotResetCurrentQuotaAccountsOrSettings()
    {
        await using var fixture = new Fixture(UsageProviderId.Cursor);
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        fixture.Service.Emit(Snapshot(UsageProviderId.Cursor, fixture.Clock.UtcNow, 12));
        await fixture.Manager.StopObservationsAsync(TimeSpan.FromSeconds(5));
        File.WriteAllText(fixture.HistoryPath, "{damaged");
        File.WriteAllText(fixture.HistoryPath + ".bak", "{damaged");
        var settingsPath = Path.Combine(fixture.Root, "settings.json");
        File.WriteAllText(settingsPath, "synthetic settings sentinel");
        var registry = File.ReadAllBytes(Path.Combine(fixture.Root, "codex-accounts.json"));
        var restarted = fixture.Restart();
        await restarted.WaitForObservationsIdleAsync();
        Assert.Equal(CodexQuotaStatus.Available, restarted.Selected!.Snapshot.Status);
        Assert.Equal(12, restarted.Selected.Snapshot.Windows[0].UsedPercent);
        Assert.Empty(Points(restarted.Selected.Observations!));
        Assert.True(restarted.Selected.ObservationStorageUnavailable);
        Assert.Equal(registry, File.ReadAllBytes(Path.Combine(fixture.Root, "codex-accounts.json")));
        Assert.Equal("synthetic settings sentinel", File.ReadAllText(settingsPath));
        await restarted.StopObservationsAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IncompleteCodexEpochResetCannotRestoreOldHistoryAfterRestart()
    {
        await using var fixture = new Fixture(UsageProviderId.Codex);
        await fixture.Manager.WaitForObservationsIdleAsync();
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        fixture.Service.Emit(Snapshot(UsageProviderId.Codex, fixture.Clock.UtcNow, 15));
        await fixture.Manager.WaitForObservationsIdleAsync();
        File.Delete(fixture.EpochPath + ".bak");
        Directory.CreateDirectory(fixture.EpochPath + ".bak");
        fixture.Clock.UtcNow = Now.AddMinutes(2);
        fixture.Service.NextRefresh = Snapshot(UsageProviderId.Codex, fixture.Clock.UtcNow, 16);
        await fixture.Manager.LoginAsync(fixture.Profile.Id, "", (_, _) => Task.CompletedTask, default);
        await fixture.Manager.StopObservationsAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CodexQuotaStatus.Available, fixture.Manager.Selected!.Snapshot.Status);
        Assert.Null(fixture.Manager.Selected.Observations);
        Assert.True(fixture.Manager.Selected.ObservationStorageUnavailable);
        var restarted = fixture.Restart();
        await restarted.WaitForObservationsIdleAsync();
        Assert.Equal(16, restarted.Selected!.Snapshot.Windows[0].UsedPercent);
        Assert.Null(restarted.Selected.Observations);
        Assert.True(restarted.Selected.ObservationStorageUnavailable);
        await restarted.StopObservationsAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ImportedReplacementRecordsItsAlreadyAcceptedQuotaOnceAndRemovesOnlyOldTrend()
    {
        await using var fixture = new Fixture(UsageProviderId.Codex);
        await fixture.Manager.StopObservationsAsync(TimeSpan.FromSeconds(5));
        var imported = fixture.Profile with { IsManaged = false };
        fixture.Store.Save(new(3, imported.Id, [imported]));
        var oldCache = fixture.Store.SnapshotPath(imported);
        Directory.CreateDirectory(Path.GetDirectoryName(oldCache)!);
        File.WriteAllText(oldCache, "synthetic imported quota cache");
        var identity = new CodexAccountIdentity(CodexQuotaStatus.Available, "new-managed@example.invalid", "pro");
        fixture.Clock.UtcNow = Now.AddMinutes(1);
        fixture.Provider.Configure = (profile, service) =>
        {
            if (profile.Id == imported.Id) return;
            service.LoginIdentity = identity;
            service.NextRefresh = Snapshot(UsageProviderId.Codex, fixture.Clock.UtcNow, 33)
                with { IdentityFingerprint = identity.StableAccountFingerprint };
        };
        var restarted = fixture.Restart();
        var result = await restarted.LoginAsync(imported.Id, "", (_, _) => Task.CompletedTask, default);
        Assert.Equal(CodexQuotaStatus.Available, result.Status);
        await restarted.WaitForObservationsIdleAsync();
        var replacement = Assert.Single(restarted.Accounts);
        Assert.NotEqual(imported.Id, replacement.Profile.Id);
        Assert.True(replacement.Profile.IsManaged);
        Assert.Equal(33m, Assert.Single(Points(replacement.Observations!)).Value);
        Assert.False(File.Exists(fixture.HistoryPath));
        Assert.Equal("synthetic imported quota cache", File.ReadAllText(oldCache));
        await restarted.StopObservationsAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ManyEventsRetainIntermediatePointsAndAccountsStayIndependent()
    {
        await using var fixture = new Fixture(UsageProviderId.Claude);
        var otherProfile = fixture.Manager.AddClaude("Other synthetic account");
        var other = fixture.Provider.Services[otherProfile.Id];
        await fixture.Manager.WaitForObservationsIdleAsync();
        for (var index = 1; index <= 40; index++)
        {
            fixture.Clock.UtcNow = Now.AddSeconds(index);
            fixture.Service.Emit(Snapshot(UsageProviderId.Claude, fixture.Clock.UtcNow, index));
            other.Emit(Snapshot(UsageProviderId.Claude, fixture.Clock.UtcNow, 80 + index / 10.0));
        }
        await fixture.Manager.WaitForObservationsIdleAsync();
        var accounts = fixture.Manager.Accounts;
        Assert.Equal(Enumerable.Range(1, 40).Select(index => (decimal)index), Points(accounts[0].Observations!).Select(point => point.Value));
        Assert.Equal(40, Points(accounts[1].Observations!).Length);
        Assert.All(Points(accounts[1].Observations!), point => Assert.InRange(point.Value, 80m, 85m));
        Assert.NotEqual(accounts[0].Observations!.Context.BindingKey, accounts[1].Observations!.Context.BindingKey);
        Assert.Equal(0, fixture.Service.RemoteRequests + other.RemoteRequests);
    }

    private static QuotaObservationPoint[] Points(QuotaObservationHistorySnapshot history) => history.Series
        .Where(series => series.Metric == QuotaObservationMetric.UsedPercent).SelectMany(series => series.Points).ToArray();

    private static (string[] Owned, string[] Preserved) SeedTemporaryFiles(string root, string id)
    {
        var directory = Path.Combine(root, "accounts", id);
        Directory.CreateDirectory(directory);
        var owned = new[] { QuotaObservationRecorder.FileName + ".", QuotaObservationRecorder.EpochFileName + ".",
                QuotaObservationRecorder.EpochFileName + ".bak." }
            .Select(prefix => Path.Combine(directory, prefix + Guid.NewGuid().ToString("N") + ".tmp")).ToArray();
        var preserved = new[]
        {
            Path.Combine(directory, QuotaObservationRecorder.FileName + ".tmp"),
            Path.Combine(directory, QuotaObservationRecorder.FileName + ".not-a-guid.tmp"),
            Path.Combine(directory, QuotaObservationRecorder.FileName + "." + Guid.NewGuid().ToString("D") + ".tmp"),
            Path.Combine(directory, QuotaObservationRecorder.FileName + ".bak." + Guid.NewGuid().ToString("N") + ".tmp"),
            Path.Combine(directory, "auth.json." + Guid.NewGuid().ToString("N") + ".tmp"),
            Path.Combine(directory, "nested", QuotaObservationRecorder.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp")
        };
        foreach (var path in owned.Concat(preserved))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "synthetic unrelated file");
        }
        return (owned, preserved);
    }

    private static CodexQuotaSnapshot Snapshot(UsageProviderId provider, DateTimeOffset at, double value) =>
        new(CodexQuotaStatus.Available, "pro", at, at, true, null, null,
            [new("synthetic-five-hour", value, 300, Now.AddHours(4), CodexWindowKind.FiveHour)], null)
        { Provider = provider, IdentityFingerprint = new string('A', 64) };

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cyclearc-observation-manager-" + Guid.NewGuid().ToString("N"));
        public MutableClock Clock { get; } = new(Now);
        public CodexAccountStore Store { get; }
        public CodexAccountProfile Profile { get; }
        public FakeProvider Provider { get; }
        public CodexAccountManager Manager { get; }
        public FakeService Service => Provider.Services[Profile.Id];
        public string HistoryPath => Path.Combine(Root, "accounts", Profile.Id, QuotaObservationRecorder.FileName);
        public string EpochPath => Path.Combine(Root, "accounts", Profile.Id, QuotaObservationRecorder.EpochFileName);
        private readonly Action<string>? _warning;
        private readonly List<CodexAccountManager> _managers = [];

        public Fixture(UsageProviderId provider, Action<string>? warning = null, Action<string>? beforeIo = null,
            Action<Fixture>? beforeStart = null)
        {
            Directory.CreateDirectory(Root);
            Store = new CodexAccountStore(Root);
            var id = Guid.NewGuid().ToString("N");
            Profile = new(id, provider == UsageProviderId.Codex ? Path.Combine(Root, "accounts", id, "codex-home") : "", "Synthetic", provider == UsageProviderId.Codex)
            { Provider = provider };
            Store.Save(new(3, Profile.Id, [Profile]));
            Provider = new(provider);
            _warning = warning;
            beforeStart?.Invoke(this);
            Manager = TrackManager(new(Store, Path.Combine(Root, "unused-default-home"), [Provider], Clock, warning, beforeIo));
        }

        public CodexAccountManager Restart() => TrackManager(new(Store, Path.Combine(Root, "unused-default-home"), [Provider], Clock, _warning));
        private CodexAccountManager TrackManager(CodexAccountManager manager)
        {
            _managers.Add(manager);
            return manager;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var manager in _managers) await AccountTestDirectory.StopManagerAsync(manager);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeProvider(UsageProviderId id) : IUsageProvider
    {
        public UsageProviderId Id => id;
        public Dictionary<string, FakeService> Services { get; } = new(StringComparer.Ordinal);
        public Action<CodexAccountProfile, FakeService>? Configure { get; set; }
        public IUsageAccountService Create(CodexAccountProfile profile)
        {
            var previous = Services.GetValueOrDefault(profile.Id);
            var service = new FakeService(profile.Provider, profile.Id, previous?.Snapshot);
            Configure?.Invoke(profile, service);
            Services[profile.Id] = service;
            return service;
        }
    }

    private sealed class FakeService(UsageProviderId provider, string id, CodexQuotaSnapshot? cached)
        : IUsageAccountService, IUsageObservationBinding, ICodexAccountOperations, ICursorAccountOperations
    {
        public CodexQuotaSnapshot Snapshot { get; private set; } = cached ?? QuotaObservationManagerTests.Snapshot(provider, Now.AddMinutes(-1), 3);
        public string? Email => null;
        public string? IdentityFingerprint => Snapshot.IdentityFingerprint;
        public string? ObservationBindingKey { get; set; } = id;
        public bool Connected { get; set; } = true;
        public bool ConnectSuccess { get; set; } = true;
        public string? BoundIdentityFingerprint => IdentityFingerprint;
        public bool IsConnected => Connected;
        public bool IsRefreshing => false;
        public bool ReceivesPassiveUpdates => provider == UsageProviderId.Claude;
        public int RemoteRequests { get; private set; }
        public int RefreshCalls { get; private set; }
        public CodexQuotaStatus LoginStatus { get; set; } = CodexQuotaStatus.Available;
        public CodexAccountIdentity? LoginIdentity { get; set; }
        public CodexQuotaSnapshot? NextRefresh { get; set; }
        public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => false;
        public event Action<CodexQuotaSnapshot>? Changed;
        public void Emit(CodexQuotaSnapshot snapshot) { Snapshot = snapshot; Changed?.Invoke(snapshot); }
        public Action<CodexQuotaSnapshot>? CaptureCallbacks() => Changed;
        public Task<CodexRefreshResult> RefreshAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RefreshCalls++;
            if (provider != UsageProviderId.Claude) RemoteRequests++;
            Emit(NextRefresh ?? Snapshot);
            return Task.FromResult(new CodexRefreshResult(Snapshot, false, null));
        }
        public Task<CodexAccountIdentity> ProbeAccountAsync(CancellationToken token)
        {
            RemoteRequests++;
            return Task.FromResult(new CodexAccountIdentity(CodexQuotaStatus.Available, "synthetic@example.invalid", "pro"));
        }
        public Task<CodexLoginResult> LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken token)
        {
            RemoteRequests++;
            if (LoginStatus == CodexQuotaStatus.Available)
                Emit(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable) with { Provider = provider,
                    IdentityFingerprint = LoginIdentity?.StableAccountFingerprint ?? new string('A', 64) });
            return Task.FromResult(new CodexLoginResult(LoginStatus, LoginIdentity));
        }
        public Task<CreditRedemptionOutcome> ConsumeCreditAsync(string creditId, CancellationToken token) => Task.FromResult(CreditRedemptionOutcome.Unavailable);
        public Task<CursorConnectionResult> ConnectCurrentAsync(CancellationToken token)
        {
            RemoteRequests++;
            return Task.FromResult(new CursorConnectionResult(ConnectSuccess, IdentityFingerprint: IdentityFingerprint));
        }
        public Task DisconnectAsync(CancellationToken token) { Connected = false; return Task.CompletedTask; }
    }
}
