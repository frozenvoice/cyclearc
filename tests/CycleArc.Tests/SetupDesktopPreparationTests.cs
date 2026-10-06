using CycleArc.Setup;

namespace CycleArc.Tests;

public sealed class SetupDesktopPreparationTests
{
    [Fact]
    public void MissingParentAcknowledgementPathPreservesDirectSetupBehaviour()
    {
        var states = new List<string>();
        var result = SetupDesktopPreparation.WaitForParent(
            acknowledgementPath: "",
            reportState: states.Add);

        Assert.True(result.Ready);
        Assert.Empty(states);
    }

    [Fact]
    public void ReadyAcknowledgementStartsOnlyAfterPreparationStateIsPublished()
    {
        var states = new List<string>();
        var reads = 0;
        var result = SetupDesktopPreparation.WaitForParent(
            acknowledgementPath: "unused",
            reportState: state => states.Add(state),
            readAcknowledgement: _ =>
            {
                reads++;
                Assert.Equal("preparing-desktop", states.Single());
                return SetupDesktopPreparation.ReadOutcome.Ready;
            });

        Assert.True(result.Ready);
        Assert.Equal(1, reads);
        Assert.Equal(new[] { "preparing-desktop" }, states);
    }

    [Fact]
    public void FailedInvalidAndTimeoutAcknowledgementsNeverProceed()
    {
        var cancelledReads = 0;
        var cancelledStates = new List<string>();
        var cancelled = SetupDesktopPreparation.WaitForParent(
            acknowledgementPath: "unused",
            reportState: cancelledStates.Add,
            readAcknowledgement: _ =>
            {
                cancelledReads++;
                return SetupDesktopPreparation.ReadOutcome.Ready;
            },
            cancellationToken: new CancellationToken(canceled: true));

        Assert.False(cancelled.Ready);
        Assert.Equal(0, cancelledReads);
        Assert.Empty(cancelledStates);

        foreach (var outcome in new[]
        {
            SetupDesktopPreparation.ReadOutcome.Failed,
            SetupDesktopPreparation.ReadOutcome.Invalid,
            SetupDesktopPreparation.ReadOutcome.Error,
        })
        {
            var readCalls = 0;
            var result = SetupDesktopPreparation.WaitForParent(
                acknowledgementPath: "unused",
                reportState: _ => { },
                readAcknowledgement: _ =>
                {
                    readCalls++;
                    return outcome;
                });

            Assert.False(result.Ready);
            Assert.Equal(1, readCalls);
        }

        var elapsed = TimeSpan.Zero;
        var timeoutResult = SetupDesktopPreparation.WaitForParent(
            acknowledgementPath: "unused",
            reportState: _ => { },
            readAcknowledgement: _ => SetupDesktopPreparation.ReadOutcome.Missing,
            wait: _ => elapsed += TimeSpan.FromSeconds(1),
            elapsed: () => elapsed,
            timeout: TimeSpan.FromSeconds(2));

        Assert.False(timeoutResult.Ready);
        Assert.Contains("Timed out", timeoutResult.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FileAcknowledgementAcceptsOnlyTheExactBoundedTokens()
    {
        var directory = Directory.CreateTempSubdirectory("cyclearc-desktop-ack-");
        try
        {
            var path = Path.Combine(directory.FullName, "handoff.ack");
            File.WriteAllText(path, "ready");
            Assert.True(SetupDesktopPreparation.WaitForParent(path, _ => { }).Ready);

            File.WriteAllText(path, "ready\n");
            var newline = SetupDesktopPreparation.WaitForParent(path, _ => { });
            Assert.False(newline.Ready);
            Assert.Contains("invalid", newline.Detail, StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(path, "failed");
            var failed = SetupDesktopPreparation.WaitForParent(path, _ => { });
            Assert.False(failed.Ready);
            Assert.Contains("could not prepare", failed.Detail, StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(path, new string('x', 129));
            var oversized = SetupDesktopPreparation.WaitForParent(path, _ => { });
            Assert.False(oversized.Ready);
            Assert.Contains("invalid", oversized.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { directory.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

}
