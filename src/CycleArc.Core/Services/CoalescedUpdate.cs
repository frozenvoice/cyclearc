namespace CycleArc.Services;

/// <summary>
/// Merges update requests from any thread into one pending posted run. The pending flag is
/// cleared before the run reads state, so a change made during a run schedules another one.
/// </summary>
public sealed class CoalescedUpdate
{
    private readonly Action<Action> _post;
    private readonly Action _run;
    private int _pending;

    public CoalescedUpdate(Action<Action> post, Action run)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public void Request()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        try { _post(Run); }
        catch
        {
            Volatile.Write(ref _pending, 0);
            throw;
        }
    }

    private void Run()
    {
        Volatile.Write(ref _pending, 0);
        _run();
    }
}
