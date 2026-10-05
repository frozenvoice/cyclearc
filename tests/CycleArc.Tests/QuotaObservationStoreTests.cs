using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using CycleArc.Codex;
using CycleArc.Observations;
using CycleArc.Services;

namespace CycleArc.Tests;

public class QuotaObservationStoreTests
{
    private static readonly DateTimeOffset Start = QuotaObservationHistoryTests.Start;
    private static QuotaObservationHistorySnapshot History(MutableClock clock, int value = 20)
    {
        var history = new QuotaObservationHistory(QuotaObservationHistoryTests.Context(), clock);
        history.Observe(QuotaObservationHistoryTests.Quota(clock.UtcNow, QuotaObservationHistoryTests.Window(value)), clock.UtcNow);
        return history.Snapshot;
    }

    [Fact]
    public void RoundtripContainsOnlyQuotaMetadataAndReplayWatermarks()
    {
        using var fixture = new Fixture();
        var history = History(fixture.Clock, 0);
        fixture.Store.Save(history);
        var loaded = fixture.Store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(history.Context, loaded.Context);
        Assert.Equal(history.Watermark, loaded.Watermark);
        Assert.Equal(0m, loaded.Series.Single().Points.Single().Value);
        var json = Encoding.UTF8.GetString(fixture.Files.Read(fixture.Store.StoragePath));
        foreach (var secret in new[] { "@", "email", "access_token", "refresh_token", "authorization", "cookie", "technicalDetail", "prompt" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        var restarted = new QuotaObservationHistory(history.Context, fixture.Clock);
        restarted.MergeLoaded(loaded);
        Assert.False(restarted.Observe(QuotaObservationHistoryTests.Quota(Start), fixture.Clock.UtcNow));
        Assert.Single(restarted.Snapshot.Series.Single().Points);
    }

    [Fact]
    public void CorruptPrimaryRecoversBackupAndCannotOverwriteIt()
    {
        using var fixture = new Fixture();
        fixture.Store.Save(History(fixture.Clock, 10));
        fixture.Clock.UtcNow = Start.AddMinutes(1);
        fixture.Store.Save(History(fixture.Clock, 20));
        var backup = fixture.Files.Read(fixture.Store.BackupPath);
        fixture.Files.Set(fixture.Store.StoragePath, Encoding.UTF8.GetBytes("{broken"));
        Assert.Equal(10m, fixture.Store.Load()!.Series.Single().Points.Single().Value);
        Assert.True(fixture.Store.RecoveredFromBackup);
        Assert.True(fixture.Store.Unavailable);
        fixture.Store.Save(History(fixture.Clock, 30));
        Assert.Equal(backup, fixture.Files.Read(fixture.Store.BackupPath));
        Assert.False(fixture.Store.Unavailable);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("replace")]
    public void InterruptedWritePreservesBothFilesAndRemovesTemporary(string operation)
    {
        using var fixture = new Fixture();
        fixture.Store.Save(History(fixture.Clock, 10));
        fixture.Store.Save(History(fixture.Clock, 20));
        var primary = fixture.Files.Read(fixture.Store.StoragePath);
        var backup = fixture.Files.Read(fixture.Store.BackupPath);
        fixture.Files.FailOperation = operation;
        Assert.Throws<IOException>(() => fixture.Store.Save(History(fixture.Clock, 30)));
        Assert.Equal(primary, fixture.Files.Read(fixture.Store.StoragePath));
        Assert.Equal(backup, fixture.Files.Read(fixture.Store.BackupPath));
        Assert.DoesNotContain(fixture.Files.Paths, p => p.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("enum")]
    [InlineData("duplicate-series")]
    [InlineData("duplicate-point")]
    [InlineData("negative")]
    [InlineData("future")]
    [InlineData("unknown-property")]
    [InlineData("binding")]
    [InlineData("missing-value")]
    [InlineData("missing-gap")]
    [InlineData("missing-limit")]
    [InlineData("missing-basis-unit")]
    public void UnsupportedOrMalformedMetadataFailsSafely(string kind)
    {
        using var fixture = new Fixture();
        fixture.Store.Save(History(fixture.Clock));
        var root = JsonNode.Parse(fixture.Files.Read(fixture.Store.StoragePath))!;
        var series = root["snapshot"]!["series"]![0]!;
        switch (kind)
        {
            case "version": root["version"] = 99; break;
            case "enum": series["metric"] = "Forecast"; break;
            case "duplicate-series": root["snapshot"]!["series"]!.AsArray().Add(series.DeepClone()); break;
            case "duplicate-point": series["points"]!.AsArray().Add(series["points"]![0]!.DeepClone()); break;
            case "negative": series["points"]![0]!["value"] = -1; break;
            case "future": series["points"]![0]!["observedAt"] = Start.AddDays(1); break;
            case "unknown-property": series["secret"] = "do-not-keep"; break;
            case "binding": root["snapshot"]!["context"]!["bindingKey"] = "not-a-hash"; break;
            case "missing-value": series["points"]![0]!.AsObject().Remove("value"); break;
            case "missing-gap": series.AsObject().Remove("pendingGap"); break;
            case "missing-limit": series.AsObject().Remove("limitId"); break;
            case "missing-basis-unit": series["points"]![0]!.AsObject().Remove("basisUnit"); break;
        }
        fixture.Files.Set(fixture.Store.StoragePath, Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.Null(fixture.Store.Load());
        Assert.True(fixture.Store.Unavailable);
    }

    [Fact]
    public void ScopeMismatchCannotRecoverAnotherProfileOrGenerationFromBackup()
    {
        using var fixture = new Fixture();
        fixture.Store.Save(History(fixture.Clock));
        fixture.Store.Save(History(fixture.Clock));
        var context = QuotaObservationHistoryTests.Context(binding: new string('b', 64));
        var other = new QuotaObservationStore(fixture.Store.StoragePath, context, fixture.Clock, fixture.Files);
        Assert.Null(other.Load());
        Assert.False(other.Unavailable); // Expected isolated generation, not damaged current quota.
        var otherProfile = new QuotaObservationStore(fixture.Store.StoragePath,
            context with { ProfileId = Guid.NewGuid().ToString("N") }, fixture.Clock, fixture.Files);
        Assert.Null(otherProfile.Load());
    }

    [Fact]
    public void CorruptCurrentPrimaryWarnsEvenWhenBackupBelongsToAnOlderBinding()
    {
        using var fixture = new Fixture();
        fixture.Store.Save(History(fixture.Clock));
        fixture.Store.Save(History(fixture.Clock));
        fixture.Files.Set(fixture.Store.StoragePath, Encoding.UTF8.GetBytes("{broken"));
        var context = QuotaObservationHistoryTests.Context(binding: new string('b', 64));
        var current = new QuotaObservationStore(fixture.Store.StoragePath, context, fixture.Clock, fixture.Files);
        Assert.Null(current.Load());
        Assert.True(current.Unavailable);
    }

    [Fact]
    public void DuplicateJsonKeysOversizedFilesAndNonFiniteJsonAreRejected()
    {
        using var fixture = new Fixture();
        fixture.Files.Set(fixture.Store.StoragePath, Encoding.UTF8.GetBytes("{\"version\":1,\"version\":1}"));
        Assert.Null(fixture.Store.Load());
        fixture.Files.Set(fixture.Store.StoragePath, new byte[QuotaObservationStore.MaxSerializedBytes + 1]);
        Assert.Null(fixture.Store.Load());
        fixture.Files.Set(fixture.Store.StoragePath, Encoding.UTF8.GetBytes("{\"version\":NaN}"));
        Assert.Null(fixture.Store.Load());
        Assert.Equal(QuotaObservationStore.MaxSerializedBytes, fixture.Files.LastReadBound);
    }

    [Fact]
    public void ExpiredPointsAreRemovedOnRestartWithoutLosingValidNewerHistory()
    {
        using var fixture = new Fixture();
        var history = new QuotaObservationHistory(QuotaObservationHistoryTests.Context(), fixture.Clock);
        history.Observe(QuotaObservationHistoryTests.Quota(Start), fixture.Clock.UtcNow);
        fixture.Clock.UtcNow = Start.AddDays(6);
        history.Observe(QuotaObservationHistoryTests.Quota(fixture.Clock.UtcNow,
            QuotaObservationHistoryTests.Window(30, fixture.Clock.UtcNow.AddHours(5))), fixture.Clock.UtcNow);
        fixture.Store.Save(history.Snapshot);
        fixture.Clock.UtcNow = Start.AddDays(8);
        var loaded = fixture.Store.Load();
        Assert.NotNull(loaded);
        Assert.Single(loaded.Series.Single().Points);
        Assert.Equal(Start.AddDays(6), loaded.Series.Single().Points.Single().ObservedAt);
    }

    [Fact]
    public void SaveRejectsExcessPointsInvalidIdsAndCrossWindowConnections()
    {
        using var fixture = new Fixture();
        var history = History(fixture.Clock);
        var series = history.Series.Single();
        var point = series.Points.Single();
        var duplicates = Enumerable.Repeat(point, QuotaObservationHistory.MaxPointsPerLimit + 1).ToImmutableArray();
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(history with { Series = [series with { Points = duplicates }] }));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(history with { Series = [series with { LimitId = "access_token" }] }));
        fixture.Clock.UtcNow = Start.AddMinutes(1);
        var later = point with { ObservedAt = fixture.Clock.UtcNow, ReceivedAt = fixture.Clock.UtcNow, ResetAt = Start.AddHours(10) };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(history with { Watermark = fixture.Clock.UtcNow,
            Series = [series with { Watermark = fixture.Clock.UtcNow, Points = [point, later] }] }));
    }

    [Fact]
    public void TotalSeriesAndCrossUnitPerLimitLimitsAreValidatedBeforeSerialization()
    {
        using var fixture = new Fixture();
        fixture.Clock.UtcNow = Start.AddMinutes(40);
        var history = History(fixture.Clock);
        var prototype = history.Series.Single();
        var points = Enumerable.Range(0, 1024).Select(i => new QuotaObservationPoint(Start.AddSeconds(i),
            Start.AddSeconds(i), 50m, 1, Start.AddHours(5), 300, CodexWindowKind.FiveHour, null)).ToImmutableArray();
        var manySeries = Enumerable.Range(0, 17).Select(i => prototype with
            { LimitId = "limit-" + i, Points = points }).ToImmutableArray();
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(history with { Series = manySeries }));
        var excessSeries = Enumerable.Range(0, QuotaObservationHistory.MaxSeries + 1).Select(i => prototype with
            { LimitId = "limit-" + i, Points = [] }).ToImmutableArray();
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(history with { Series = excessSeries }));
        var usd = prototype with { Metric = QuotaObservationMetric.UsedAmount, Unit = "USD", Points = points };
        var eur = usd with { Unit = "EUR", Points = points.Add(points[^1] with { ObservedAt = Start.AddSeconds(1024),
            ReceivedAt = Start.AddSeconds(1024) }) };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(history with { Series = [usd, eur] }));
    }

    [Fact]
    public void DecreasingRemainingAmountRoundtripsAndIncreasingWithinSameSegmentIsRejected()
    {
        using var fixture = new Fixture();
        fixture.Clock.UtcNow = Start.AddMinutes(1);
        var snapshot = History(fixture.Clock);
        var prototype = snapshot.Series.Single() with { Metric = QuotaObservationMetric.RemainingAmount, Unit = "USD" };
        var first = new QuotaObservationPoint(Start, Start, 50, 1, Start.AddHours(5), 300,
            CodexWindowKind.FiveHour, 100, "USD");
        var second = first with { ObservedAt = fixture.Clock.UtcNow, ReceivedAt = fixture.Clock.UtcNow, Value = 41 };
        var valid = snapshot with { Series = [prototype with { Points = [first, second] }] };
        fixture.Store.Save(valid);
        var restored = fixture.Store.Load()!.Series.Single().Points;
        Assert.Equal(new[] { 50m, 41m }, restored.Select(p => p.Value));
        Assert.Equal(restored[0].SegmentId, restored[1].SegmentId);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(valid with
            { Series = [prototype with { Points = [second with { ObservedAt = Start, ReceivedAt = Start },
                first with { ObservedAt = fixture.Clock.UtcNow, ReceivedAt = fixture.Clock.UtcNow }] }] }));
    }

    [Fact]
    public void ExplicitNullLimitIdRoundtripsWithoutAliasingLiteralUnspecifiedOrWeeklyWindow()
    {
        using var fixture = new Fixture();
        var history = new QuotaObservationHistory(QuotaObservationHistoryTests.Context(), fixture.Clock);
        var five = QuotaObservationHistoryTests.Window(20) with { LimitId = null };
        var week = QuotaObservationHistoryTests.Window(70, Start.AddDays(7), 10080, CodexWindowKind.Weekly)
            with { LimitId = null };
        var named = QuotaObservationHistoryTests.Window(90, id: "unspecified");
        history.Observe(QuotaObservationHistoryTests.Quota(Start) with { Windows = [five, week, named] }, fixture.Clock.UtcNow);
        fixture.Store.Save(history.Snapshot);
        var loaded = fixture.Store.Load()!;
        Assert.Equal(3, loaded.Series.Length);
        Assert.Equal(2, loaded.Series.Count(s => s.LimitId is null));
        Assert.Equal(20m, loaded.GetCurrentWindow(five).Points.Single().Value);
        Assert.Same(loaded.GetCurrentWindow(five), loaded.GetCurrentWindow(five));
        Assert.Equal(70m, loaded.GetCurrentWindow(week).Points.Single().Value);
        Assert.Equal(90m, loaded.GetCurrentWindow(named).Points.Single().Value);
        Assert.False(fixture.Store.Unavailable);
        var json = Encoding.UTF8.GetString(fixture.Files.Read(fixture.Store.StoragePath));
        Assert.Contains("\"limitId\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ActualLocalFileRoundtripUsesIsolatedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "CycleArc-observation-tests", Guid.NewGuid().ToString("N"));
        var clock = new MutableClock(Start);
        try
        {
            var path = Path.Combine(root, "history.json");
            var store = new QuotaObservationStore(path, QuotaObservationHistoryTests.Context(), clock);
            Assert.Null(store.Load());
            Assert.False(store.Unavailable);
            store.Save(History(clock));
            Assert.Single(store.Load()!.Series.Single().Points);
            store.Save(History(clock, 30));
            Assert.True(File.Exists(path + ".bak"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Fixture : IDisposable
    {
        public MutableClock Clock { get; } = new(Start);
        public MemoryFiles Files { get; } = new();
        public QuotaObservationStore Store { get; }
        public Fixture() => Store = new QuotaObservationStore(Path.Combine(Path.GetTempPath(), "CycleArc-observation-tests", Guid.NewGuid().ToString("N"), "history.json"),
            QuotaObservationHistoryTests.Context(), Clock, Files);
        public void Dispose() { }
    }

    private sealed class MemoryFiles : IQuotaObservationFiles
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<string> Paths => _files.Keys;
        public string? FailOperation { get; set; }
        public int LastReadBound { get; private set; }
        public byte[] Read(string path) => _files[path].ToArray();
        public void Set(string path, byte[] bytes) => _files[path] = bytes.ToArray();
        public byte[]? ReadBounded(string path, int maxBytes)
        { LastReadBound = maxBytes; return _files.TryGetValue(path, out var bytes) && bytes.Length <= maxBytes ? bytes.ToArray() : null; }
        public void CreateDirectory(string path) { }
        public bool Exists(string path) => _files.ContainsKey(path);
        public void WriteAndFlush(string path, byte[] bytes)
        { Set(path, bytes); if (FailOperation == "write") throw new IOException("Synthetic interrupted write."); }
        public void Replace(string temporary, string path, string backup)
        {
            if (FailOperation == "replace") throw new IOException("Synthetic interrupted replace.");
            Set(backup, Read(path));
            Move(temporary, path);
        }
        public void Move(string temporary, string path) { Set(path, Read(temporary)); Delete(temporary); }
        public void Delete(string path) => _files.Remove(path);
    }
}
