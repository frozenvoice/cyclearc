using CycleArc.Services;

namespace CycleArc.Codex;

/// <summary>
/// Color band of one ring, taken from the unrounded used percentage of the limit that ring
/// represents. It only chooses the arc color: exhaustion itself stays
/// <see cref="CodexRingPresentation.IsDangerLevel"/>, and stale/unknown states keep their
/// own presentation ahead of the band.
/// </summary>
public enum UsageRingBand
{
    Normal,
    Caution,
    NearLimit,
    Exhausted
}

public static class UsageRingBands
{
    public const double CautionPercent = 70;
    public const double NearLimitPercent = 85;
    public const double ExhaustedPercent = 100;

    /// <summary>Never round first: 99.6% is shown as 100% but is still near the limit.</summary>
    public static UsageRingBand From(double? usedPercent) => usedPercent switch
    {
        null or double.NaN => UsageRingBand.Normal,
        double.PositiveInfinity or double.NegativeInfinity => UsageRingBand.Normal,
        >= ExhaustedPercent => UsageRingBand.Exhausted,
        >= NearLimitPercent => UsageRingBand.NearLimit,
        >= CautionPercent => UsageRingBand.Caution,
        _ => UsageRingBand.Normal
    };

    /// <summary>Theme resource for the arc; stale data keeps its existing color first.</summary>
    public static string ArcBrushKey(UsageRingBand band, bool stale) => stale ? "StaleBrush" : band switch
    {
        UsageRingBand.Caution => "RingCautionBrush",
        UsageRingBand.NearLimit => "RingNearLimitBrush",
        UsageRingBand.Exhausted => "RingExhaustedBrush",
        _ => "AccentBrush"
    };

    /// <summary>Text appended to tooltips/accessible names; empty for the normal band.</summary>
    public static string Label(UsageRingBand band) => band switch
    {
        UsageRingBand.Caution => UiText.T("Caution", "주의"),
        UsageRingBand.NearLimit => UiText.T("Near limit", "소진 임박"),
        UsageRingBand.Exhausted => UiText.T("Limit reached", "소진"),
        _ => ""
    };

    /// <summary>Appends the band label only when the ring is actually colored by it.</summary>
    public static string WithLabel(string text, CodexRingPresentation ring, bool stale)
    {
        if (stale || !ring.IsAvailable) return text;
        var label = Label(ring.Band);
        return label.Length == 0 ? text : $"{text} · {label}";
    }
}
