namespace CycleArc.Updates;

/// <summary>
/// Coordinates the explicit check, download and apply phases of an application update.
/// The release client owns transport, package validation and staging; this type owns only
/// the user-visible lifecycle and safety rules around those operations.
/// </summary>
public sealed class AppUpdateCoordinator
{
    private readonly object _gate = new();
    private readonly IAppUpdateClient _client;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    private AppUpdateState _state;
    private AppUpdateRelease? _release;
    private AppUpdateError? _error;
    private int _progress;
    private long _nextOperationId;
    private long _activeOperationId;

    public AppUpdateCoordinator(
        IAppUpdateClient client,
        int maxAttempts = 2,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (retryDelay is { } delay && delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        _client = client;
        _maxAttempts = maxAttempts;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
        _state = client.IsInstalled ? AppUpdateState.Idle : AppUpdateState.Disabled;
    }

    public event Action? Changed;

    public AppUpdateState State { get { lock (_gate) return _state; } }

    public AppUpdateRelease? Release { get { lock (_gate) return _release; } }

    public int Progress { get { lock (_gate) return _progress; } }

    public AppUpdateError? Error { get { lock (_gate) return _error; } }

    public string CurrentVersion => _client.CurrentVersion;

    /// <summary>Checks for a newer stable release when no other update operation is active.</summary>
    public Task CheckAsync(CancellationToken token = default)
    {
        if (!TryBeginCheck(out var operationId))
            return Task.CompletedTask;

        return RunCheckAsync(operationId, token);
    }

    /// <summary>
    /// Downloads the currently available release after a user has explicitly requested it.
    /// Calling this in any other state is a no-op.
    /// </summary>
    public Task DownloadAsync(CancellationToken token = default)
    {
        if (!TryBeginDownload(out var operationId, out var release))
            return Task.CompletedTask;

        return RunDownloadAsync(operationId, release, token);
    }

    /// <summary>
    /// Stages a previously completed download and asks the caller to exit gracefully.
    /// Returns true only when the client accepted the staged release.
    /// </summary>
    public bool ApplyOnExit()
    {
        AppUpdateRelease release;
        long operationId;
        lock (_gate)
        {
            if (_activeOperationId != 0 || _state != AppUpdateState.Ready || _release is null)
                return false;

            release = _release;
            operationId = ++_nextOperationId;
            _activeOperationId = operationId;
            _state = AppUpdateState.Applying;
            _error = null;
        }
        NotifyChanged();

        try
        {
            _client.ApplyOnExit(release);
            // Applying is intentionally terminal for this process. The caller should exit
            // immediately after receiving true; leaving the state as Applying also prevents
            // a second apply if an exit is delayed by the host.
            return true;
        }
        catch
        {
            lock (_gate)
            {
                if (_activeOperationId != operationId)
                    return false;
                _activeOperationId = 0;
                _state = AppUpdateState.Failed;
                _error = AppUpdateError.ApplyFailed;
            }
            NotifyChanged();
            return false;
        }
    }

    private bool TryBeginCheck(out long operationId)
    {
        operationId = 0;
        var installed = false;
        try { installed = _client.IsInstalled; }
        catch { }

        lock (_gate)
        {
            if (!installed)
            {
                if (_activeOperationId == 0)
                {
                    _state = AppUpdateState.Disabled;
                    _release = null;
                    _error = null;
                    _progress = 0;
                }
                return false;
            }

            if (_activeOperationId != 0 || _state is AppUpdateState.Ready or AppUpdateState.Applying)
                return false;

            operationId = ++_nextOperationId;
            _activeOperationId = operationId;
            _state = AppUpdateState.Checking;
            _release = null;
            _error = null;
            _progress = 0;
        }
        NotifyChanged();
        return true;
    }

    private bool TryBeginDownload(out long operationId, out AppUpdateRelease release)
    {
        operationId = 0;
        release = null!;
        lock (_gate)
        {
            if (_activeOperationId != 0
                || _release is null
                || (_state != AppUpdateState.Available
                    && !(_state == AppUpdateState.Failed
                        && _error is AppUpdateError.DownloadFailed
                            or AppUpdateError.Canceled
                            or AppUpdateError.ApplyFailed)))
                return false;

            operationId = ++_nextOperationId;
            _activeOperationId = operationId;
            release = _release;
            _state = AppUpdateState.Downloading;
            _error = null;
            _progress = 0;
        }
        NotifyChanged();
        return true;
    }

    private async Task RunCheckAsync(long operationId, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!TryParseVersion(_client.CurrentVersion, out var current))
            {
                FinishFailure(operationId, AppUpdateError.InvalidCurrentVersion);
                return;
            }

            AppUpdateRelease? candidate = null;
            for (var attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                try
                {
                    candidate = await _client.CheckAsync(token).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException)
                {
                    FinishFailure(operationId, AppUpdateError.Canceled);
                    return;
                }
                catch
                {
                    if (attempt == _maxAttempts)
                    {
                        FinishFailure(operationId, AppUpdateError.CheckFailed);
                        return;
                    }
                    await DelayBeforeRetryAsync(token).ConfigureAwait(false);
                }
            }

            token.ThrowIfCancellationRequested();
            if (candidate is not null
                && TryParseVersion(candidate.Version, out var advertised)
                && advertised.IsStable
                && CompareVersions(advertised, current) > 0)
            {
                FinishAvailable(operationId, candidate);
            }
            else
            {
                FinishIdle(operationId);
            }
        }
        catch (OperationCanceledException)
        {
            FinishFailure(operationId, AppUpdateError.Canceled);
        }
        catch
        {
            FinishFailure(operationId, AppUpdateError.CheckFailed);
        }
    }

    private async Task RunDownloadAsync(long operationId, AppUpdateRelease release, CancellationToken token)
    {
        var progress = new InlineProgress(value => UpdateProgress(operationId, value));
        try
        {
            token.ThrowIfCancellationRequested();
            for (var attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                try
                {
                    if (attempt > 1)
                        UpdateProgress(operationId, 0);
                    await _client.DownloadAsync(release, progress, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    FinishReady(operationId);
                    return;
                }
                catch (OperationCanceledException)
                {
                    FinishFailure(operationId, AppUpdateError.Canceled);
                    return;
                }
                catch
                {
                    if (attempt == _maxAttempts)
                    {
                        FinishFailure(operationId, AppUpdateError.DownloadFailed);
                        return;
                    }
                    await DelayBeforeRetryAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            FinishFailure(operationId, AppUpdateError.Canceled);
        }
        catch
        {
            FinishFailure(operationId, AppUpdateError.DownloadFailed);
        }
    }

    private Task DelayBeforeRetryAsync(CancellationToken token) =>
        _retryDelay == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(_retryDelay, token);

    private void UpdateProgress(long operationId, int value)
    {
        lock (_gate)
        {
            if (_activeOperationId != operationId || _state != AppUpdateState.Downloading)
                return;
            _progress = Math.Clamp(value, 0, 100);
        }
        NotifyChanged();
    }

    private void FinishAvailable(long operationId, AppUpdateRelease release)
    {
        lock (_gate)
        {
            if (_activeOperationId != operationId)
                return;
            _activeOperationId = 0;
            _release = release;
            _progress = 0;
            _error = null;
            _state = AppUpdateState.Available;
        }
        NotifyChanged();
    }

    private void FinishIdle(long operationId)
    {
        lock (_gate)
        {
            if (_activeOperationId != operationId)
                return;
            _activeOperationId = 0;
            _release = null;
            _progress = 0;
            _error = null;
            _state = AppUpdateState.Idle;
        }
        NotifyChanged();
    }

    private void FinishReady(long operationId)
    {
        lock (_gate)
        {
            if (_activeOperationId != operationId)
                return;
            _activeOperationId = 0;
            _progress = 100;
            _error = null;
            _state = AppUpdateState.Ready;
        }
        NotifyChanged();
    }

    private void FinishFailure(long operationId, AppUpdateError error)
    {
        lock (_gate)
        {
            if (_activeOperationId != operationId)
                return;
            _activeOperationId = 0;
            _error = error;
            _progress = 0;
            _state = AppUpdateState.Failed;
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        // UI subscribers should never turn a completed update operation into a failed one.
        try { Changed?.Invoke(); }
        catch { }
    }

    private readonly struct InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private readonly record struct ParsedVersion(int[] Components, bool IsStable);

    private static int CompareVersions(ParsedVersion left, ParsedVersion right)
    {
        var length = Math.Max(left.Components.Length, right.Components.Length);
        for (var index = 0; index < length; index++)
        {
            var leftPart = index < left.Components.Length ? left.Components[index] : 0;
            var rightPart = index < right.Components.Length ? right.Components[index] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
                return comparison;
        }

        // A stable release is newer than an otherwise equal prerelease. This matters when
        // the app itself was built from a release candidate and the next stable release is
        // advertised from the same numeric version.
        return left.IsStable.CompareTo(right.IsStable);
    }

    private static bool TryParseVersion(string? text, out ParsedVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        if (text.StartsWith('v'))
            text = text[1..];
        if (text.Length == 0)
            return false;

        var buildSeparator = text.IndexOf('+');
        if (buildSeparator >= 0)
        {
            if (buildSeparator == text.Length - 1)
                return false;
            text = text[..buildSeparator];
        }

        var prereleaseSeparator = text.IndexOf('-');
        var core = prereleaseSeparator >= 0 ? text[..prereleaseSeparator] : text;
        var prerelease = prereleaseSeparator >= 0 ? text[(prereleaseSeparator + 1)..] : null;
        if (core.Length == 0 || prerelease is { Length: 0 })
            return false;
        if (prerelease is not null && prerelease.Split('.').Any(part =>
                part.Length == 0 || part.Any(character => !char.IsLetterOrDigit(character) && character != '-')))
            return false;

        var components = core.Split('.');
        if (components.Length < 3)
            return false;
        var numbers = new int[components.Length];
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component.Length == 0 || (component.Length > 1 && component[0] == '0')
                || !int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
                return false;
        }

        version = new ParsedVersion(numbers, prerelease is null);
        return true;
    }
}
