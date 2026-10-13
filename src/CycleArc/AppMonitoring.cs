using CycleArc.Codex;
using CycleArc.Monitoring;
using System.IO;

namespace CycleArc;

public partial class App
{
    private UsageHistoryStore? _history;
    private Task _historyTask = Task.CompletedTask;
    private DateTimeOffset _lastHistoryQueued;
    private void QueueHistory(IReadOnlyList<CodexAccountView> accounts)
    {
        var now = DateTimeOffset.UtcNow;
        if (_history is null || !_historyTask.IsCompleted || now - _lastHistoryQueued < TimeSpan.FromMinutes(1)) return;
        _lastHistoryQueued = now;
        // Immutable projected measurements only; no account home, label, email or credentials are serialized.
        _historyTask = Task.Run(() =>
        {
            try { _history.Record(accounts, now); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _log.Warn("Usage history storage unavailable"); }
        });
    }

    private (string Title, string Body, string ProfileId)? CollectFinancialAlerts(IReadOnlyList<CodexAccountView> accounts)
    {
        var result = FinancialAlerts.Evaluate(accounts, _settings.FinancialAlertMarks, _settings.FinancialAlerts ?? new(), DateTimeOffset.UtcNow);
        var previous = _settings.FinancialAlertMarks ?? [];
        if (result.Marks.Count != previous.Count || result.Marks.Any(p => !previous.TryGetValue(p.Key, out var value) || value != p.Value))
        {
            _settings.FinancialAlertMarks = result.Marks;
            try { _settingsStore.Save(_settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn("Financial alert marks could not be saved"); }
        }
        return result.Alerts.Count == 0 || IsExiting ? null : FinancialAlerts.Summary(result.Alerts);
    }

    private void NotifyAccountAlerts(IReadOnlyList<CodexAccountView> accounts)
    {
        var usage = CollectUsageAlerts(accounts);
        var financial = CollectFinancialAlerts(accounts);
        var combined = AccountAlertNotice.Combine(usage, financial);
        if (combined is not { } notice) return;
        var (title, body, profileId) = notice;
        // The tray owns one click callback. A snapshot can therefore deliver one combined
        // notice, preserving both policies without replacing a visible notice's target.
        _tray.Balloon(title, body, () => { _codex?.Select(profileId); ShowMain(); });
    }
}
