using System.Text;
using System.Text.Json;
using CycleArc.Providers.Claude;

namespace CycleArc.Tests;

public sealed class ClaudeDesktopUsageReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private const string Organization = "org-a";

    [Fact]
    public void Parse_SelectsNewestMatchingOrganization_AndKeepsSourceTimestamp()
    {
        var older = Now.AddMinutes(-10);
        var newest = Now.AddMinutes(-2);
        var json = History(
            Sample(newest.AddMinutes(1), "other", 999, -1, 1),
            Sample(older, "other", 99, 99, 1),
            Sample(older, Organization, 15, 7, 36.21),
            Sample(newest, Organization, 42.5, 8, 99));

        var read = Parse(json);

        Assert.False(read.Unavailable);
        Assert.Equal(new ClaudeDesktopUsageSample(newest, 42.5, 8), read.Sample);
    }

    [Fact]
    public void Parse_AllowsMissingOptionalWindows_ButDoesNotInventAReading()
    {
        var fiveOnly = Parse(History(Sample(Now.AddMinutes(-1), Organization, 0, null)));
        var weeklyOnly = Parse(History(Sample(Now.AddMinutes(-1), Organization, null, 100)));
        var noWindows = Parse(History(SampleWithUsage(Now.AddMinutes(-1), Organization, "{}")));

        Assert.Equal(new ClaudeDesktopUsageSample(Now.AddMinutes(-1), 0, null), fiveOnly.Sample);
        Assert.Equal(new ClaudeDesktopUsageSample(Now.AddMinutes(-1), null, 100), weeklyOnly.Sample);
        Assert.False(fiveOnly.Unavailable);
        Assert.False(weeklyOnly.Unavailable);
        Assert.Null(noWindows.Sample);
        Assert.False(noWindows.Unavailable);
    }

    [Fact]
    public void Parse_NewInvalidMatchingSampleDoesNotHideOlderLastGood()
    {
        var older = Now.AddMinutes(-10);
        var newer = Now.AddMinutes(-1);
        var json = History(Sample(older, Organization, 15, 7), Sample(newer, Organization, 101, 7));

        var read = Parse(json);

        Assert.True(read.Unavailable);
        Assert.Equal(new ClaudeDesktopUsageSample(older, 15, 7), read.Sample);
    }

    [Fact]
    public void Parse_OlderInvalidHistoryIsSupersededByNewerValidSample()
    {
        var older = Now.AddMinutes(-10);
        var newer = Now.AddMinutes(-1);
        var json = History(Sample(older, Organization, -1, 7), Sample(newer, Organization, 23.25, null));

        var read = Parse(json);

        Assert.False(read.Unavailable);
        Assert.Equal(new ClaudeDesktopUsageSample(newer, 23.25, null), read.Sample);
    }

    [Fact]
    public void Parse_FutureMatchingSampleIsUnavailable_AndRetainsOlderSample()
    {
        var older = Now.AddMinutes(-10);
        var future = Now.AddMinutes(1);
        var json = History(Sample(older, Organization, 15, 7), Sample(future, Organization, 20, 8));

        var read = Parse(json);

        Assert.True(read.Unavailable);
        Assert.Equal(new ClaudeDesktopUsageSample(older, 15, 7), read.Sample);
    }

    [Fact]
    public void Parse_SameTimestampMalformedMatchCannotHideAValidMatch()
    {
        var observed = Now.AddMinutes(-1);
        var json = History(
            Sample(observed, Organization, 15, 7),
            Sample(observed, Organization, 101, 7));

        var read = Parse(json);

        Assert.True(read.Unavailable);
        Assert.Equal(new ClaudeDesktopUsageSample(observed, 15, 7), read.Sample);
    }

    [Theory]
    [InlineData("{\"version\":1,\"samples\":[]}")]
    [InlineData("{\"version\":2,\"samples\":null}")]
    [InlineData("{\"version\":2,\"samples\":[],\"extra\":true}")]
    [InlineData("{\"version\":2,\"samples\":[{\"t\":1,\"t\":2,\"org\":\"org-a\",\"u\":{\"fh\":1}}]}")]
    [InlineData("{\"version\":2,\"samples\":[{\"t\":1,\"org\":\"org-a\",\"u\":{\"fh\":1,\"fh\":2}}]}")]
    public void Parse_RejectsWrongVersionUnknownFieldsAndDuplicateRequiredKeys(string json)
    {
        var read = Parse(json);
        Assert.True(read.Unavailable);
        Assert.Null(read.Sample);
    }

    [Fact]
    public void Parse_RejectsOversizedInputAndExcessiveSampleCount()
    {
        var oversized = Encoding.UTF8.GetBytes(new string('x', ClaudeDesktopUsageReader.MaxInputBytes + 1));
        var manySamples = JsonSerializer.Serialize(new
        {
            version = 2,
            samples = Enumerable.Repeat(new { t = 1L, org = Organization, u = new { fh = 1.0, sd = (double?)null } },
                ClaudeDesktopUsageReader.MaxSamples + 1)
        });

        Assert.True(ClaudeDesktopUsageReader.Parse(oversized, Organization, Now).Unavailable);
        Assert.True(ClaudeDesktopUsageReader.Parse(Encoding.UTF8.GetBytes(manySamples), Organization, Now).Unavailable);
    }

    [Fact]
    public void Read_DistinguishesMissingFileFromPartialOrMalformedFile()
    {
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, ClaudeDesktopUsageReader.FileName);
        var reader = new ClaudeDesktopUsageReader([path]);

        var missing = reader.Read(Organization, Now);
        File.WriteAllText(path, "{");
        var partial = reader.Read(Organization, Now);
        File.WriteAllText(path, History(Sample(Now.AddMinutes(-1), Organization, 15, 7)));
        var valid = reader.Read(Organization, Now);

        Assert.Null(missing.Sample);
        Assert.False(missing.Unavailable);
        Assert.True(partial.Unavailable);
        Assert.Null(partial.Sample);
        Assert.Equal(new ClaudeDesktopUsageSample(Now.AddMinutes(-1), 15, 7), valid.Sample);
        Assert.False(valid.Unavailable);
    }

    [Fact]
    public void Parse_RecognizesEscapedAllowedPropertyNames()
    {
        var read = Parse("""{"\u0076ersion":2,"\u0073amples":[{"\u0074":1000,"\u006frg":"org-a","\u0075":{"\u0066h":15.5,"\u0073d":7.25,"\u0078u":99}}]}""");

        Assert.False(read.Unavailable);
        Assert.Equal(new ClaudeDesktopUsageSample(DateTimeOffset.FromUnixTimeMilliseconds(1000), 15.5, 7.25), read.Sample);
    }

    [Theory]
    [InlineData("""{"version":2,"\u0076ersion":2,"samples":[]}""")]
    [InlineData("""{"version":2,"samples":[{"t":1000,"org":"org-a","\u006frg":"org-a","u":{"fh":1}}]}""")]
    [InlineData("""{"version":2,"samples":[{"t":1000,"org":"org-a","u":{"fh":1,"\u0066h":2}}]}""")]
    [InlineData("""{"version":2,"samples":[{"t":1000,"org":"org-a","u":{"fh":1,"xu":1,"\u0078u":2}}]}""")]
    [InlineData("""{"version":2,"samples":[{"t":1000,"org":"org-a","u":{"fh":1,"FH":2}}]}""")]
    [InlineData("""{"version":2,"samples":[{"t":1000,"org":"org-a","u":{"fh":1,"fһ":2}}]}""")]
    public void Parse_RejectsDecodedDuplicatesAndOrdinallyDifferentUnknownNames(string json)
    {
        var read = Parse(json);
        Assert.True(read.Unavailable);
        Assert.Null(read.Sample);
    }

    [Fact]
    public void Read_AtomicReplacementWithEqualLengthAndFileTimestampStillAcceptsNewestData()
    {
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, ClaudeDesktopUsageReader.FileName);
        var reader = new ClaudeDesktopUsageReader([path]);
        File.WriteAllText(path, History(Sample(Now.AddMinutes(-2), Organization, 15, 7)));
        var length = new FileInfo(path).Length;
        var writtenAt = File.GetLastWriteTimeUtc(path);
        Assert.Equal(15, reader.Read(Organization, Now).Sample!.FiveHour);

        var temporary = path + ".new";
        File.WriteAllText(temporary, History(Sample(Now.AddMinutes(-1), Organization, 17, 8)));
        File.Move(temporary, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, writtenAt);
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(path));

        var read = reader.Read(Organization, Now);
        Assert.False(read.Unavailable);
        Assert.Equal(new ClaudeDesktopUsageSample(Now.AddMinutes(-1), 17, 8), read.Sample);
    }

    [Fact]
    public void Read_PreviouslyMissingCandidateIsCheckedAgainAndCanSupplyNewerData()
    {
        using var root = new TempRoot();
        var existing = Path.Combine(root.Path, "existing.json");
        var newlyCreated = Path.Combine(root.Path, "new.json");
        var reader = new ClaudeDesktopUsageReader([existing, newlyCreated]);
        File.WriteAllText(existing, History(Sample(Now.AddMinutes(-2), Organization, 15, 7)));
        Assert.Equal(15, reader.Read(Organization, Now).Sample!.FiveHour);

        File.WriteAllText(newlyCreated, History(Sample(Now.AddMinutes(-1), Organization, 18, 8)));
        var read = reader.Read(Organization, Now);
        Assert.False(read.Unavailable);
        Assert.Equal(new ClaudeDesktopUsageSample(Now.AddMinutes(-1), 18, 8), read.Sample);
    }

    [Fact]
    public void Read_ActualFileOverInputLimitIsUnavailable()
    {
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, ClaudeDesktopUsageReader.FileName);
        File.WriteAllBytes(path, new byte[ClaudeDesktopUsageReader.MaxInputBytes + 1]);
        var read = new ClaudeDesktopUsageReader([path]).Read(Organization, Now);
        Assert.True(read.Unavailable);
        Assert.Null(read.Sample);
    }

    private static ClaudeDesktopUsageRead Parse(string json) =>
        ClaudeDesktopUsageReader.Parse(Encoding.UTF8.GetBytes(json), Organization, Now);

    [Fact]
    public void ConflictingValuesAtTheSameTimestampAreNotASuccessfulReceipt()
    {
        var read = Parse(History(Sample(Now, Organization, 15, 7), Sample(Now, Organization, 99, 7)));
        Assert.True(read.Unavailable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveSourceTimestampsAreInvalid(long milliseconds)
    {
        var read = Parse(History(Sample(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), Organization, 15, 7)));
        Assert.True(read.Unavailable);
        Assert.Null(read.Sample);
    }

    private static string History(params string[] samples) =>
        $"{{\"version\":2,\"samples\":[{string.Join(',', samples)}]}}";

    private static string Sample(DateTimeOffset observedAt, string organization, double? fiveHour, double? sevenDay,
        double? extra = null) =>
        SampleWithUsage(observedAt, organization, JsonSerializer.Serialize(new { fh = fiveHour, sd = sevenDay, xu = extra }));

    private static string SampleWithUsage(DateTimeOffset observedAt, string organization, string usage) =>
        $"{{\"t\":{observedAt.ToUnixTimeMilliseconds()},\"org\":\"{organization}\",\"u\":{usage}}}";

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "cyclearc-claude-desktop-reader-" + Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
