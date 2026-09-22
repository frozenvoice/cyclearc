namespace CycleArc.Services;

public static class SettingsMigration
{
    public static AppSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return AppSettings.CreateDefaults();
        }

        JsonObject? node;
        try
        {
            node = JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return AppSettings.CreateDefaults();
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(json, ChatGptJson.Options) ?? AppSettings.CreateDefaults();
        var hasTransport = HasProperty(node, "authTransport", "AuthTransport");
        if (!hasTransport)
        {
            settings.AuthTransport = AuthTransportKind.WebView2;
        }

        if (string.IsNullOrWhiteSpace(settings.ChromeExtensionId) && !string.IsNullOrWhiteSpace(settings.CompanionExtensionId))
        {
            settings.ChromeExtensionId = settings.CompanionExtensionId;
        }

        settings.Version = AppSettings.CurrentVersion;
        // Each window's own value, normalised separately: one must never overwrite the other.
        settings.FlyoutZoomPercent = Codex.FlyoutZoom.Normalize(settings.FlyoutZoomPercent);
        settings.WidgetZoomPercent = Codex.FlyoutZoom.Normalize(settings.WidgetZoomPercent);
        var widgetAnchors = new Codex.WindowEdgeAnchors(settings.WidgetHorizontalAnchor, settings.WidgetVerticalAnchor).Normalize();
        var flyoutAnchors = new Codex.WindowEdgeAnchors(settings.FlyoutHorizontalAnchor, settings.FlyoutVerticalAnchor).Normalize();
        settings.WidgetHorizontalAnchor = widgetAnchors.Horizontal;
        settings.WidgetVerticalAnchor = widgetAnchors.Vertical;
        settings.FlyoutHorizontalAnchor = flyoutAnchors.Horizontal;
        settings.FlyoutVerticalAnchor = flyoutAnchors.Vertical;
        if (!settings.SnapWindowsToScreenEdges) settings.ClearWindowEdgeAnchors();
        return settings;
    }

    private static bool HasProperty(JsonObject? node, params string[] names) =>
        node is not null && names.Any(name => node.ContainsKey(name));
}
