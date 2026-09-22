using System.Globalization;

namespace CycleArc.Codex;

/// <summary>Text-only precision for usage surfaces. Never changes quota or ring geometry.</summary>
public static class UsagePercentFormatting
{
    public static string Widget(double? value)
    {
        if (!IsValid(value)) return "?";
        if (value == 0) return "0%";
        if (value is > 0 and < 1) return "<1%";
        if (value is > 99 and < 100) return ">99%";
        return Math.Round(value!.Value, MidpointRounding.AwayFromZero)
            .ToString(CultureInfo.InvariantCulture) + "%";
    }

    public static string Detail(double? value)
    {
        if (!IsValid(value)) return "?";
        if (value == 0) return "0%";
        if (value is > 0 and < 0.01) return "<0.01%";
        if (value is > 99.99 and < 100) return ">99.99%";
        return value!.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%";
    }

    public static string WidgetRemaining(CodexQuotaWindow window) =>
        WidgetRemaining(window.UsedPercent, DisplayableRemaining(window));

    public static string DetailRemaining(CodexQuotaWindow window) => Detail(DisplayableRemaining(window));

    // The model's derived remainder is clamped. Do not let an invalid source usage
    // masquerade as a valid endpoint on these surfaces; leave the model untouched.
    private static double? DisplayableRemaining(CodexQuotaWindow window) =>
        IsValid(window.UsedPercent) ? window.RemainingPercent : null;

    public static string WidgetRemaining(double? usedPercent, double? remainingPercent)
    {
        // Boundary markers take priority. Only a known complementary pair from this
        // same limit can share rounding; never infer an absent or independent value.
        if (IsValid(usedPercent) && remainingPercent is >= 1 and <= 99
            && Math.Abs(usedPercent!.Value + remainingPercent.Value - 100) <= 1e-12)
        {
            return (100 - Math.Round(usedPercent.Value, MidpointRounding.AwayFromZero))
                .ToString(CultureInfo.InvariantCulture) + "%";
        }
        return Widget(remainingPercent);
    }

    private static bool IsValid(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value);
}
