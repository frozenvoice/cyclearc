using CycleArc.Codex;

namespace CycleArc.UI;

public partial class FlyoutWindow
{
    private CodexAccountView? _observationAccount;
    public ObservedTrendView ObservedTrend { get; } = new();

    private void BindObservedTrend(CodexQuotaSnapshot snapshot)
    {
        if (ObservedTrendHost.Content is null) ObservedTrendHost.Content = ObservedTrend;
        ObservedTrend.Bind(_observationAccount, CodexRingPresentation.FromDetail(snapshot, UsagePeriod).Window,
            WidgetStatusPresentation.HidesQuota(snapshot));
    }
}
