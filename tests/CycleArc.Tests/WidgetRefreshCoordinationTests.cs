using CycleArc.Codex;

namespace CycleArc.Tests;

/// <summary>
/// The widget's header refresh raises the same RefreshRequested event the tray, the flyout
/// and the middle click already raise, and they all reach CodexRefreshCoordinator. These
/// cover the merging the widget button relies on instead of adding a policy of its own.
/// </summary>
public sealed class WidgetRefreshCoordinationTests
{
    private static CodexRefreshResult Result() =>
        new(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available), UsedCache: false, null);

    [Fact]
    public async Task ConcurrentCallersShareOneRefresh()
    {
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new CodexRefreshCoordinator(async _ =>
        {
            Interlocked.Increment(ref started);
            await release.Task;
            return Result();
        });

        var first = coordinator.RefreshAsync(CancellationToken.None);
        var second = coordinator.RefreshAsync(CancellationToken.None);
        var third = coordinator.RefreshAsync(CancellationToken.None);
        Assert.True(coordinator.IsRefreshing);

        release.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, Volatile.Read(ref started));
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task StateReturnsToIdleAfterAFailedRefresh()
    {
        var transitions = 0;
        var coordinator = new CodexRefreshCoordinator(_ => throw new InvalidOperationException("refresh failed"));
        coordinator.StateChanged += () => Interlocked.Increment(ref transitions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RefreshAsync(CancellationToken.None));

        // The button mirrors IsRefreshing, so a failure has to leave it clickable again.
        Assert.False(coordinator.IsRefreshing);
        Assert.True(Volatile.Read(ref transitions) >= 2, "A refresh must report both its start and its end.");
    }

    [Fact]
    public async Task StateReturnsToIdleAfterACancelledRefresh()
    {
        using var cancellation = new CancellationTokenSource();
        var coordinator = new CodexRefreshCoordinator(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Result();
        });

        var refresh = coordinator.RefreshAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        await coordinator.WaitForIdleAsync();
        Assert.False(coordinator.IsRefreshing);
    }

    // A refresh that has already finished must not leave the button disabled.
    [Fact]
    public async Task IdleAfterACompletedRefresh()
    {
        var coordinator = new CodexRefreshCoordinator(_ => Task.FromResult(Result()));
        await coordinator.RefreshAsync(CancellationToken.None);
        Assert.False(coordinator.IsRefreshing);
    }
}
