using CycleArc.Codex;
using CycleArc.Providers.Cursor;

namespace CycleArc.Services;

public static class WidgetStatusFormatter
{
    public static string ProLine(ProStatusPresentation presentation) =>
        $"{UiText.GptPro}   {presentation.ProStateText}";

    public static string ResetLine(ProStatusPresentation presentation)
    {
        if (presentation.HasServerReset && presentation.ServerResetAt is { } reset)
        {
            return reset.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        }

        return presentation.ResetAmbiguous ? UiText.MultipleProResets : "";
    }

    public static string CodexLine(CodexQuotaSnapshot snapshot)
    {
        if (CursorUsagePresentation.IsCursor(snapshot))
        {
            var quotas = string.Join(" · ", snapshot.Windows.Select(window =>
                CursorUsagePresentation.QuotaDisplayLabel(window.LimitId) + " "
                + CursorUsagePresentation.RemainingText(window)));
            return string.IsNullOrWhiteSpace(quotas)
                ? $"Cursor {CursorUsagePresentation.StatusText(snapshot)}"
                : $"Cursor {quotas} · {CursorUsagePresentation.UpdatedText(snapshot)}";
        }
        var status = CodexDisplayFormatting.StatusText(snapshot);
        if (snapshot.CompactWindow?.UsedPercent is { } percent)
        {
            var kind = CodexDisplayFormatting.CompactWindowKindLabel(snapshot.CompactWindow);
            return $"Codex {kind}   {CodexDisplayFormatting.PercentText(percent)}";
        }

        return string.IsNullOrWhiteSpace(status) ? $"Codex {UiText.CodexUnavailable}" : $"Codex {status}";
    }

    public static string HistoryLine(ProStatusPresentation presentation, QuotaSnapshot snapshot) =>
        $"{UiText.ExtraHigh} {snapshot.Reasoning.ExtraHigh.ToString(CultureInfo.InvariantCulture)}";
}
