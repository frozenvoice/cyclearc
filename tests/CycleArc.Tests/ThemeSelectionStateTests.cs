using System.Xml.Linq;
using CycleArc.Services;

namespace CycleArc.Tests;

public class ThemeSelectionStateTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CheckBoxTemplate_MakesCheckedStateVisibleAtRest()
    {
        var style = ImplicitStyle("CheckBox");
        var template = Template(style);
        Assert.NotNull(Named(template, "Box"));
        Assert.NotNull(Named(template, "CheckMark"));

        var checkMark = Named(template, "CheckMark")!;
        Assert.Equal("Collapsed", (string?)checkMark.Attribute("Visibility"));
        Assert.Contains("OnAccentBrush", (string?)checkMark.Attribute("Stroke"), StringComparison.Ordinal);

        var checkedTrigger = Triggers(template).Single(trigger =>
            (string?)trigger.Attribute("Property") == "IsChecked"
            && (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(Setters(checkedTrigger), setter =>
            (string?)setter.Attribute("TargetName") == "Box"
            && (string?)setter.Attribute("Property") == "Background"
            && ((string?)setter.Attribute("Value"))!.Contains("AccentBrush", StringComparison.Ordinal));
        Assert.Contains(Setters(checkedTrigger), setter =>
            (string?)setter.Attribute("TargetName") == "CheckMark"
            && (string?)setter.Attribute("Property") == "Visibility"
            && (string?)setter.Attribute("Value") == "Visible");
        Assert.Contains(Setters(checkedTrigger), setter =>
            (string?)setter.Attribute("TargetName") == "CheckMark"
            && (string?)setter.Attribute("Property") == "Stroke"
            && ((string?)setter.Attribute("Value"))!.Contains("OnAccentBrush", StringComparison.Ordinal));

        var mouseOver = Triggers(template).Single(trigger =>
            (string?)trigger.Attribute("Property") == "IsMouseOver"
            && (string?)trigger.Attribute("Value") == "True");
        Assert.DoesNotContain(Setters(mouseOver), setter =>
            (string?)setter.Attribute("TargetName") == "CheckMark"
            && (string?)setter.Attribute("Property") == "Visibility");

        var disabledChecked = MultiTriggers(template).Single(trigger =>
            HasCondition(trigger, "IsEnabled", "False") && HasCondition(trigger, "IsChecked", "True"));
        Assert.Contains(Setters(disabledChecked), setter =>
            (string?)setter.Attribute("TargetName") == "CheckMark"
            && (string?)setter.Attribute("Value") == "Visible");
        Assert.Contains(Setters(disabledChecked), setter =>
            (string?)setter.Attribute("TargetName") == "Box"
            && (string?)setter.Attribute("Property") == "Background"
            && ((string?)setter.Attribute("Value"))!.Contains("CheckBoxDisabledCheckedBrush", StringComparison.Ordinal));

        Assert.DoesNotContain("SystemColors", template.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CheckBoxChrome", template.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RadioButtonTemplate_MakesSelectedDotVisibleAtRest()
    {
        var style = ImplicitStyle("RadioButton");
        var template = Template(style);
        Assert.NotNull(Named(template, "Outer"));
        Assert.NotNull(Named(template, "Dot"));
        Assert.Equal("Collapsed", (string?)Named(template, "Dot")!.Attribute("Visibility"));

        var checkedTrigger = Triggers(template).Single(trigger =>
            (string?)trigger.Attribute("Property") == "IsChecked"
            && (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(Setters(checkedTrigger), setter =>
            (string?)setter.Attribute("TargetName") == "Dot"
            && (string?)setter.Attribute("Property") == "Visibility"
            && (string?)setter.Attribute("Value") == "Visible");
        Assert.Contains(Setters(checkedTrigger), setter =>
            (string?)setter.Attribute("TargetName") == "Outer"
            && (string?)setter.Attribute("Property") == "Background"
            && ((string?)setter.Attribute("Value"))!.Contains("AccentBrush", StringComparison.Ordinal));

        var mouseOver = Triggers(template).Single(trigger =>
            (string?)trigger.Attribute("Property") == "IsMouseOver"
            && (string?)trigger.Attribute("Value") == "True");
        Assert.DoesNotContain(Setters(mouseOver), setter =>
            (string?)setter.Attribute("TargetName") == "Dot"
            && (string?)setter.Attribute("Property") == "Visibility");

        var disabledChecked = MultiTriggers(template).Single(trigger =>
            HasCondition(trigger, "IsEnabled", "False") && HasCondition(trigger, "IsChecked", "True"));
        Assert.Contains(Setters(disabledChecked), setter =>
            (string?)setter.Attribute("TargetName") == "Dot"
            && (string?)setter.Attribute("Value") == "Visible");

        Assert.DoesNotContain("SystemColors", template.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("BulletChrome", template.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsScreenshotFixture_CheckedAndUncheckedResolveToDifferentRestingStates()
    {
        var settings = AppSettings.CreateDefaults();
        settings.AutoSync = false;
        settings.StartWithWindows = false;
        settings.FloatingWidgetEnabled = true;
        settings.TaskbarStatusEnabled = false;
        settings.WidgetAlwaysOnTop = true;
        settings.NotifyAt20 = true;
        settings.NotifyAt10 = false;
        settings.NotifyExhausted = true;
        settings.NotifyReset = false;
        settings.NotifySyncError = true;

        var boxes = new (string Name, bool Checked)[]
        {
            ("AutoSync", settings.AutoSync),
            ("StartWithWindows", settings.StartWithWindows),
            ("FloatingWidgetEnabled", settings.FloatingWidgetEnabled),
            ("TaskbarStatusEnabled", settings.TaskbarStatusEnabled),
            ("WidgetAlwaysOnTop", settings.WidgetAlwaysOnTop),
            ("NotifyAt20", settings.NotifyAt20),
            ("NotifyAt10", settings.NotifyAt10),
            ("NotifyExhausted", settings.NotifyExhausted),
            ("NotifyReset", settings.NotifyReset),
            ("NotifySyncError", settings.NotifySyncError)
        };

        Assert.Contains(boxes, box => box.Checked);
        Assert.Contains(boxes, box => !box.Checked);

        var template = Template(ImplicitStyle("CheckBox"));
        var checkedState = RestingState(template, isChecked: true);
        var uncheckedState = RestingState(template, isChecked: false);

        Assert.NotEqual(checkedState.BoxBackground, uncheckedState.BoxBackground);
        Assert.Equal("Visible", checkedState.GlyphVisibility);
        Assert.Equal("Collapsed", uncheckedState.GlyphVisibility);
        Assert.Contains("AccentBrush", checkedState.BoxBackground, StringComparison.Ordinal);
        Assert.Contains("OnAccentBrush", checkedState.GlyphBrush, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMouseOver", checkedState.SourceTrigger, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMouseOver", uncheckedState.SourceTrigger, StringComparison.Ordinal);

        foreach (var box in boxes)
        {
            var state = box.Checked ? checkedState : uncheckedState;
            Assert.Equal(box.Checked ? "Visible" : "Collapsed", state.GlyphVisibility);
        }
    }

    [Fact]
    public void ApplyTheme_UpdatesSelectionBrushesForDarkAndLight()
    {
        var source = File.ReadAllText(FindAppSource());

        Assert.Contains("CheckBoxBackgroundBrush", source, StringComparison.Ordinal);
        Assert.Contains("CheckBoxBorderBrush", source, StringComparison.Ordinal);
        Assert.Contains("CheckBoxDisabledCheckedBrush", source, StringComparison.Ordinal);
        Assert.Contains("MediaColor(42, 42, 42)", source, StringComparison.Ordinal);
        Assert.Contains("MediaColor(255, 255, 255)", source, StringComparison.Ordinal);
        Assert.Contains("MediaColor(59, 82, 122)", source, StringComparison.Ordinal);
        Assert.Contains("MediaColor(147, 197, 253)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TabItemSelectedState_IsExplicitAndDoesNotDependOnHover()
    {
        var template = Template(ImplicitStyle("TabItem"));
        var selected = Triggers(template).Single(trigger =>
            (string?)trigger.Attribute("Property") == "IsSelected"
            && (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(Setters(selected), setter =>
            (string?)setter.Attribute("Property") == "Background"
            || (string?)setter.Attribute("Property") == "Foreground");
        Assert.DoesNotContain(
            Triggers(template),
            trigger => (string?)trigger.Attribute("Property") == "IsMouseOver");
    }

    private static RestingVisual RestingState(XElement template, bool isChecked)
    {
        if (!isChecked)
        {
            var box = Named(template, "Box")!;
            var mark = Named(template, "CheckMark")!;
            return new RestingVisual(
                BoxBackground: (string?)box.Attribute("Background") ?? StyleBrush("CheckBox", "Background"),
                GlyphVisibility: (string?)mark.Attribute("Visibility") ?? "",
                GlyphBrush: (string?)mark.Attribute("Stroke") ?? "",
                SourceTrigger: "default");
        }

        var trigger = Triggers(template).Single(item =>
            (string?)item.Attribute("Property") == "IsChecked"
            && (string?)item.Attribute("Value") == "True");
        var background = Setters(trigger).Single(setter =>
            (string?)setter.Attribute("TargetName") == "Box"
            && (string?)setter.Attribute("Property") == "Background");
        var visibility = Setters(trigger).Single(setter =>
            (string?)setter.Attribute("TargetName") == "CheckMark"
            && (string?)setter.Attribute("Property") == "Visibility");
        var stroke = Setters(trigger).Single(setter =>
            (string?)setter.Attribute("TargetName") == "CheckMark"
            && (string?)setter.Attribute("Property") == "Stroke");
        return new RestingVisual(
            (string?)background.Attribute("Value") ?? "",
            (string?)visibility.Attribute("Value") ?? "",
            (string?)stroke.Attribute("Value") ?? "",
            "IsChecked=True");
    }

    private static string StyleBrush(string type, string property)
    {
        var setter = ImplicitStyle(type).Elements(Ns + "Setter")
            .First(item => (string?)item.Attribute("Property") == property);
        return (string?)setter.Attribute("Value") ?? "";
    }

    private static XElement ImplicitStyle(string type)
    {
        var document = XDocument.Load(ThemesPath());
        return document.Root!.Elements(Ns + "Style").Single(style =>
            (string?)style.Attribute("TargetType") == type && style.Attribute(X + "Key") is null);
    }

    private static XElement Template(XElement style)
    {
        var setter = style.Elements(Ns + "Setter")
            .Single(item => (string?)item.Attribute("Property") == "Template");
        return setter.Element(Ns + "Setter.Value")!.Element(Ns + "ControlTemplate")!;
    }

    private static XElement? Named(XElement template, string name) =>
        template.Descendants().FirstOrDefault(element => (string?)element.Attribute(X + "Name") == name);

    private static IEnumerable<XElement> Triggers(XElement template) =>
        template.Element(Ns + "ControlTemplate.Triggers")?.Elements(Ns + "Trigger") ?? [];

    private static IEnumerable<XElement> MultiTriggers(XElement template) =>
        template.Element(Ns + "ControlTemplate.Triggers")?.Elements(Ns + "MultiTrigger") ?? [];

    private static IEnumerable<XElement> Setters(XElement trigger) => trigger.Elements(Ns + "Setter");

    private static bool HasCondition(XElement multiTrigger, string property, string value) =>
        multiTrigger.Element(Ns + "MultiTrigger.Conditions")?
            .Elements(Ns + "Condition")
            .Any(condition =>
                (string?)condition.Attribute("Property") == property
                && (string?)condition.Attribute("Value") == value) == true;

    private static string ThemesPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Themes.xaml");
        Assert.True(File.Exists(path), "Themes.xaml was not copied to the test output");
        return path;
    }

    private static string FindAppSource()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "CycleArc", "App.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("App.xaml.cs");
    }

    private sealed record RestingVisual(
        string BoxBackground,
        string GlyphVisibility,
        string GlyphBrush,
        string SourceTrigger);
}
