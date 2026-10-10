namespace CycleArc.Providers.Cursor;

/// <summary>
/// Applies the Cursor recent-request option to hooks.json one change at a time and always ends on
/// the latest setting: a change requested while an apply is running is applied after it, never dropped.
/// </summary>
public sealed class CursorHookSynchronizer
{
    private readonly Func<bool> _desired;
    private readonly Func<bool, CancellationToken, Task> _apply;
    private readonly CancellationToken _cancellation;
    private readonly object _gate = new();
    private long _requested;
    private bool _active;
    private Task _running = Task.CompletedTask;

    /// <param name="desired">Reads the current setting each time an apply starts.</param>
    /// <param name="apply">Brings hooks.json to the given state; handles its own failures.</param>
    public CursorHookSynchronizer(Func<bool> desired, Func<bool, CancellationToken, Task> apply,
        CancellationToken cancellation)
    {
        _desired = desired;
        _apply = apply;
        _cancellation = cancellation;
    }

    /// <summary>The running sync, or a completed task when idle.</summary>
    public Task Completion
    {
        get { lock (_gate) return _running; }
    }

    public Task Request()
    {
        lock (_gate)
        {
            _requested++;
            if (_active) return _running;
            _active = true;
            _running = RunAsync();
            return _running;
        }
    }

    private async Task RunAsync()
    {
        // Nothing runs under the caller's lock.
        await Task.Yield();
        var finished = false;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                long seen;
                lock (_gate) seen = _requested;
                await _apply(_desired(), _cancellation);
                lock (_gate)
                {
                    if (_requested != seen) continue;
                    _active = false;
                    finished = true;
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        finally
        {
            // A request that arrives after a normal finish starts its own run; do not clear its flag.
            if (!finished)
                lock (_gate) _active = false;
        }
    }
}
