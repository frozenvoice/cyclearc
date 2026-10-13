using CycleArc.Codex;

namespace CycleArc.Providers.Claude;

public enum ClaudeResetSource { Server, StatusLine, Desktop }
public enum ClaudeResetObservationState { Direct, Preserved, NotProvided, Elapsed, Stale }
public sealed record ClaudeResetObservation(ClaudeResetObservationState State,
    ClaudeResetSource? Source, DateTimeOffset? ObservedAt, DateTimeOffset? LastKnownResetAt);

public sealed record ClaudeResetSourceReading(ClaudeResetSource Source, DateTimeOffset At,
    ClaudeLiveUsageWindow? FiveHour, ClaudeLiveUsageWindow? SevenDay);
public sealed record ClaudeResetSeen(ClaudeResetSource Source, DateTimeOffset At, double? Used, DateTimeOffset? ResetAt);
public sealed record ClaudeResetWindowEvidence(DateTimeOffset? ResetAt, DateTimeOffset? ResetObservedAt,
    ClaudeResetSource? ResetSource, DateTimeOffset? ProgressAt, double? Used, bool Invalidated,
    DateTimeOffset? InvalidatedAt, IReadOnlyList<ClaudeResetSeen> Seen,
    int? GrantsRemaining = null, DateTimeOffset? GrantsObservedAt = null);
public sealed record ClaudeResetTimingState(ClaudeResetWindowEvidence FiveHour, ClaudeResetWindowEvidence SevenDay,
    DateTimeOffset? BlockedThrough = null)
{
    public static ClaudeResetTimingState Empty { get; } = new(NewWindow(), NewWindow());
    private static ClaudeResetWindowEvidence NewWindow() => new(null, null, null, null, null, false, null, []);
}

/// <summary>Bounded observation continuity, never a calculated reset or an assertion about unobserved activity.</summary>
internal sealed class ClaudeResetTimingTracker(ClaudeResetTimingStore? store)
{
    internal static readonly TimeSpan ContinuityGap = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan BoundaryMargin = TimeSpan.FromSeconds(30);
    private ClaudeConnectionBinding? _binding;
    private ClaudeResetTimingState _state = ClaudeResetTimingState.Empty;
    private bool _dirty;
    private bool _urgent;
    private DateTimeOffset? _lastPersistAt;
    private DateTimeOffset? _retryAt;

    public void Bind(ClaudeConnectionBinding? binding, DateTimeOffset now)
    {
        if (_binding == binding) return;
        _binding = binding;
        _lastPersistAt = null;
        _retryAt = null;
        var saved = binding is { Disconnected: false } ? store?.Read(binding, now) : null;
        _state = saved?.State ?? ClaudeResetTimingState.Empty;
        // A previous backup cannot prove that a later invalidation was not committed.
        if (saved?.Unavailable == true) _state = _state with { BlockedThrough = now };
        _dirty = saved?.Unavailable == true;
        _urgent = _dirty;
    }

    public IReadOnlyList<CodexQuotaWindow> Update(ClaudeConnectionBinding? binding,
        IReadOnlyList<ClaudeResetSourceReading> readings, ClaudeResetSource selectedSource,
        DateTimeOffset? selectedAt, IReadOnlyList<CodexQuotaWindow> windows,
        ClaudeResetCreditInventory? inventory, string? failure, DateTimeOffset now)
    {
        Bind(binding, now);
        if (binding is { Disconnected: false })
        {
            var previousFiveReset = _state.FiveHour.ResetAt;
            var previousWeekReset = _state.SevenDay.ResetAt;
            foreach (var reading in readings.OrderBy(r => r.At).ThenBy(r => r.Source == ClaudeResetSource.Server ? 1 : 0))
            {
                if (reading.At < binding.ConnectedAt || reading.At > now
                    || reading.Source == ClaudeResetSource.StatusLine && reading.At == binding.ConnectedAt) continue;
                Set(CodexWindowKind.FiveHour, Observe(_state.FiveHour, reading.Source, reading.At, reading.FiveHour, 300, now));
                Set(CodexWindowKind.Weekly, Observe(_state.SevenDay, reading.Source, reading.At, reading.SevenDay, 10080, now));
            }
            if (inventory is { Eligible: true } && inventory.ObservedAt >= binding.ConnectedAt && inventory.ObservedAt <= now)
            {
                Set(CodexWindowKind.FiveHour, Grants(_state.FiveHour, inventory, "five_hour", previousFiveReset));
                Set(CodexWindowKind.Weekly, Grants(_state.SevenDay, inventory, "seven_day", previousWeekReset));
            }
        }
        return windows.Select(window => Project(window, selectedSource, selectedAt, binding, failure, now)).ToArray();
    }

    private void Set(CodexWindowKind kind, ClaudeResetWindowEvidence next)
    {
        var previous = kind == CodexWindowKind.FiveHour ? _state.FiveHour : _state.SevenDay;
        if (next == previous) return;
        _dirty = true;
        _urgent |= next.ResetAt != previous.ResetAt || next.Invalidated != previous.Invalidated
            || next.InvalidatedAt != previous.InvalidatedAt;
        _state = kind == CodexWindowKind.FiveHour ? _state with { FiveHour = next } : _state with { SevenDay = next };
    }

    private ClaudeResetWindowEvidence Observe(ClaudeResetWindowEvidence state, ClaudeResetSource source,
        DateTimeOffset at, ClaudeLiveUsageWindow? value, int minutes, DateTimeOffset now)
    {
        var seen = state.Seen.FirstOrDefault(s => s.Source == source);
        var nextSeen = new ClaudeResetSeen(source, at, value?.UsedPercentage, value?.ResetsAt);
        if (seen == nextSeen) return state;
        var newServerDateAtSameClock = source == ClaudeResetSource.Server && seen?.At == at
            && value?.ResetsAt is not null && value.ResetsAt != seen.ResetAt;
        if (seen is not null && (at < seen.At || at == seen.At && !newServerDateAtSameClock))
            return Invalidate(state, state.ProgressAt ?? seen.At);
        state = state with { Seen = state.Seen.Where(s => s.Source != source).Append(nextSeen).ToArray() };
        if (state.ProgressAt is { } progress && at < progress)
            return Invalidate(state, progress);
        var conflict = state.ProgressAt == at && state.Seen.Any(s => s.Source != source && s.At == at
            && (s.Used != value?.UsedPercentage || (s.ResetAt is not null && value?.ResetsAt is not null && s.ResetAt != value.ResetsAt)));
        var decreased = state.Used is { } oldUsed && value is { } current && current.UsedPercentage < oldUsed;
        // A delayed local callback does not prove a different current cycle while the
        // last server period remains future and no observed period-change signal exists.
        var serverEvidence = state.Seen.FirstOrDefault(s => s.Source == ClaudeResetSource.Server);
        var contradictsServer = source == ClaudeResetSource.StatusLine && serverEvidence?.ResetAt == state.ResetAt
            && value?.ResetsAt is { } localReset && localReset != state.ResetAt && state.ResetAt > now
            && !decreased && !state.Invalidated;
        conflict |= contradictsServer;
        var gap = state.ProgressAt is { } previousAt && at - previousAt > ContinuityGap;
        if (conflict || decreased || gap || value is null) state = Invalidate(state, at);

        // Only an actual timestamp in a new observation can establish/reset evidence.
        // A cached Code callback with the same invalidated date cannot repair a period.
        if (value?.ResetsAt is { } reset && !conflict && reset > DateTimeOffset.UnixEpoch
            && reset <= at.AddMinutes(minutes).AddMinutes(5)
            && (_state.BlockedThrough is null || at > _state.BlockedThrough)
            && (!state.Invalidated || source == ClaudeResetSource.Server && at >= state.InvalidatedAt
                || reset != state.ResetAt && at >= state.InvalidatedAt))
            state = state with { ResetAt = reset, ResetObservedAt = at, ResetSource = source,
                Invalidated = false, InvalidatedAt = null };
        else if (value?.ResetsAt is not null && !conflict && value.ResetsAt != state.ResetAt)
            state = Invalidate(state, at);
        return state with { ProgressAt = at, Used = value?.UsedPercentage };
    }

    private static ClaudeResetWindowEvidence Invalidate(ClaudeResetWindowEvidence state, DateTimeOffset at) =>
        state with { Invalidated = true, InvalidatedAt = state.InvalidatedAt > at ? state.InvalidatedAt : at };

    private static ClaudeResetWindowEvidence Grants(ClaudeResetWindowEvidence state, ClaudeResetCreditInventory inventory, string target,
        DateTimeOffset? previousReset)
    {
        var remaining = inventory.Grants.Where(g => g.Clears.Count == 0 || g.Clears.Contains(target)).Sum(g => g.ResetsLeft);
        if (state.GrantsObservedAt > inventory.ObservedAt
            || state.GrantsObservedAt == inventory.ObservedAt && state.GrantsRemaining == remaining) return state;
        var confirmedNewDate = state.ResetSource == ClaudeResetSource.Server && state.ResetObservedAt == inventory.ObservedAt
            && state.ResetAt != previousReset;
        if (!confirmedNewDate && state.GrantsRemaining is { } old && remaining < old && state.ResetObservedAt <= inventory.ObservedAt)
            state = Invalidate(state, inventory.ObservedAt);
        return state with { GrantsRemaining = remaining, GrantsObservedAt = inventory.ObservedAt };
    }

    private CodexQuotaWindow Project(CodexQuotaWindow window, ClaudeResetSource source, DateTimeOffset? at,
        ClaudeConnectionBinding? binding, string? failure, DateTimeOffset now)
    {
        var state = window.Kind == CodexWindowKind.FiveHour ? _state.FiveHour : _state.SevenDay;
        var reset = window.ResetsAt;
        var direct = reset is not null;
        if (direct && binding is { Disconnected: false }
            && (state.Invalidated || reset != state.ResetAt || source != state.ResetSource || at != state.ResetObservedAt))
            reset = null;
        if (!direct && binding is { Disconnected: false } && !state.Invalidated && state.ResetAt is { } candidate
            && state.ResetObservedAt is { } origin && at is { } usageAt
            && usageAt > origin && usageAt >= state.ProgressAt && now - usageAt <= ContinuityGap
            && candidate > now + BoundaryMargin && window.UsedPercent is { } used && used >= state.Used
            && failure is not "claude-live-auth-required" and not "claude-auth-required"
            && (_state.BlockedThrough is null || origin > _state.BlockedThrough)
            && (state.ResetSource != ClaudeResetSource.StatusLine || origin > binding.ConnectedAt))
            reset = candidate;
        var status = reset is { } known && known <= now ? ClaudeResetObservationState.Elapsed
            : reset is not null ? direct ? ClaudeResetObservationState.Direct : ClaudeResetObservationState.Preserved
            : state.ResetAt <= now && state.ResetAt is not null ? ClaudeResetObservationState.Elapsed : ClaudeResetObservationState.NotProvided;
        return window with { ResetsAt = reset, ResetObservation = new(status,
            direct && reset is not null ? source : state.ResetSource,
            direct && reset is not null ? at : state.ResetObservedAt,
            reset ?? state.ResetAt) };
    }

    public async Task<bool> PersistAsync(CancellationToken token, DateTimeOffset now)
    {
        if (!_dirty || store is null || _binding is not { Disconnected: false } binding) return true;
        if (_retryAt > now) return false;
        if (!_urgent && _lastPersistAt is { } savedAt && now - savedAt < TimeSpan.FromMinutes(1)) return true;
        try { await store.SaveAsync(binding, _state, token).ConfigureAwait(false); _dirty = false; _urgent = false; _lastPersistAt = now; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _binding = null; Bind(binding, now); _retryAt = now.AddMinutes(1); return false; }
    }

    public static CodexQuotaSnapshot Age(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        var changed = false;
        var windows = snapshot.Windows.Select(window =>
        {
            if (window.ResetObservation is not { } observation) return window;
            if (observation.LastKnownResetAt <= now && observation.State != ClaudeResetObservationState.Elapsed)
            { changed = true; return window with { ResetObservation = observation with { State = ClaudeResetObservationState.Elapsed } }; }
            if (observation.State == ClaudeResetObservationState.Preserved
                && snapshot.LastSuccessfulRefresh is { } usageAt && now - usageAt > ContinuityGap)
            { changed = true; return window with { ResetsAt = null, ResetObservation = observation with { State = ClaudeResetObservationState.Stale } }; }
            return window;
        }).ToArray();
        return changed ? snapshot with { Windows = windows } : snapshot;
    }
}
