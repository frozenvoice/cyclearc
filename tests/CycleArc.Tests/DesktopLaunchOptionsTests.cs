using CycleArc.Services;

namespace CycleArc.Tests;

public class DesktopLaunchOptionsTests
{
    [Fact]
    public void OrdinaryAndAutorunNeverReplace()
    {
        Assert.False(DesktopLaunchOptions.Parse([]).Replace);
        Assert.False(DesktopLaunchOptions.Parse(["--show"]).Replace);
        var autorun = DesktopLaunchOptions.Parse(["--autorun"]);
        Assert.True(autorun.Autorun);
        Assert.False(autorun.Show);
        Assert.False(autorun.Replace);
    }

    [Theory]
    [InlineData("--replace")]
    [InlineData("--install")]
    public void ExplicitSwitchRequiresVerifiedIdentity(string argument)
    {
        Assert.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse([argument]));
        var options = DesktopLaunchOptions.Parse([argument, "--expected-sha256", new string('A', 64), "--expected-version", "0.5.8.0"]);
        Assert.True(options.Replace);
        Assert.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse([argument, "--autorun", "--expected-sha256", new string('A', 64), "--expected-version", "0.5.8.0"]));
    }

    [Fact]
    public void DesktopStatusAndShutdownAreExclusive()
    {
        var status = DesktopLaunchOptions.Parse(["--desktop-status"]);
        Assert.True(status.StatusOnly);
        Assert.False(status.ShutdownOnly);
        var shutdown = DesktopLaunchOptions.Parse(["--desktop-shutdown"]);
        Assert.True(shutdown.ShutdownOnly);
        Assert.False(shutdown.StatusOnly);
        Assert.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse(["--desktop-status", "--desktop-shutdown"]));
        Assert.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse(["--desktop-shutdown", "--show"]));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("\"CycleArc.exe", false)]
    [InlineData("CycleArc.exe \"--claude-statusline", false)]
    [InlineData("\"C:\\app path\\CycleArc.exe\"", true)]
    [InlineData("C:\\app\\CycleArc.exe --show", true)]
    [InlineData("\"C:\\app path\\CycleArc.exe\" --claude-statusline --show", false)]
    [InlineData("CycleArc.exe \"--claude-statusline-bridge\" payload", false)]
    [InlineData("CycleArc.exe --claude-stop-failure-bridge payload", false)]
    [InlineData("CycleArc.exe --apply-update job.json", false)]
    [InlineData("CycleArc.exe --install --expected-sha256 value", false)]
    [InlineData("CycleArc.exe --replace --show", false)]
    [InlineData("CycleArc.exe --desktop-status", false)]
    [InlineData("CycleArc.exe --desktop-shutdown", false)]
    [InlineData("CycleArc.exe --show --claude-statusline", true)]
    public void LegacyFallbackExcludesCallbacksAndInstallers(string? command, bool desktop)
        => Assert.Equal(desktop, DesktopLaunchOptions.IsLegacyDesktopCommand(command));
}
