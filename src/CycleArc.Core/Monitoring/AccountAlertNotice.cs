using CycleArc.Services;

namespace CycleArc.Monitoring;

public static class AccountAlertNotice
{
    public static (string Title, string Body, string ProfileId)? Combine(
        (string Title, string Body, string ProfileId)? usage,
        (string Title, string Body, string ProfileId)? financial)
    {
        if (usage is not { } first) return financial;
        if (financial is not { } second) return first;
        return (UiText.T("CycleArc account alerts", "CycleArc 계정 알림"),
            Clip(first.Title, 48) + ": " + Clip(first.Body, 75) + "\n"
                + Clip(second.Title, 48) + ": " + Clip(second.Body, 75),
            first.ProfileId);
    }

    // NOTIFYICONDATA has a 256-character, null-terminated body. Reserve space for
    // both policies before handing it to Windows, so the second warning cannot vanish.
    private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}
