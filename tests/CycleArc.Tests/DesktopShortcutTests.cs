using CycleArc.Setup;

namespace CycleArc.Tests;

public sealed class DesktopShortcutTests
{
    [Fact]
    public void FreshInstallLeavesDesktopEmptyWhenDeclined()
    {
        using var paths = new Paths();
        paths.MakeStartMenuLink();

        DesktopShortcuts.Apply(paths.Root, false, paths.Desktop, paths.Programs);

        Assert.False(File.Exists(paths.DesktopLink));
        Assert.False(DesktopShortcuts.IsPresent(paths.Root, paths.Desktop));
    }

    [Fact]
    public void AUserDeletedShortcutRemainsAbsentOnRepair()
    {
        using var paths = new Paths();
        paths.MakeStartMenuLink();
        DesktopShortcuts.Apply(paths.Root, true, paths.Desktop, paths.Programs);
        File.Delete(paths.DesktopLink);

        Assert.False(DesktopShortcuts.IsPresent(paths.Root, paths.Desktop));
        DesktopShortcuts.Apply(paths.Root, false, paths.Desktop, paths.Programs);

        Assert.False(File.Exists(paths.DesktopLink));
    }

    [Fact]
    public void SelectionCopiesTheInstalledStartMenuLink()
    {
        using var paths = new Paths();
        paths.MakeStartMenuLink();

        DesktopShortcuts.Apply(paths.Root, true, paths.Desktop, paths.Programs);

        Assert.True(DesktopShortcuts.IsPresent(paths.Root, paths.Desktop));
        Assert.Equal(File.ReadAllBytes(paths.StartMenuLink), File.ReadAllBytes(paths.DesktopLink));
    }

    [Fact]
    public void RepairRecognizesLegacyCurrentTargetAndRefreshesIt()
    {
        using var paths = new Paths();
        paths.MakeStartMenuLink();
        DesktopShortcuts.CreateLink(paths.DesktopLink, paths.CurrentExecutable);
        Assert.True(DesktopShortcuts.IsPresent(paths.Root, paths.Desktop));

        DesktopShortcuts.Apply(paths.Root, true, paths.Desktop, paths.Programs);

        Assert.Equal(File.ReadAllBytes(paths.StartMenuLink), File.ReadAllBytes(paths.DesktopLink));
        Assert.True(DesktopShortcuts.IsPresent(paths.Root, paths.Desktop));
    }

    [Fact]
    public void OptOutDeletesOnlyTheCurrentInstallationsLink()
    {
        using var paths = new Paths();
        paths.MakeStartMenuLink();
        DesktopShortcuts.Apply(paths.Root, true, paths.Desktop, paths.Programs);

        DesktopShortcuts.Apply(paths.Root, false, paths.Desktop, paths.Programs);

        Assert.False(File.Exists(paths.DesktopLink));
        Assert.True(File.Exists(paths.StartMenuLink));
    }

    [Fact]
    public void UnrelatedSameNameLinkIsNeitherDeletedNorOverwritten()
    {
        using var paths = new Paths();
        paths.MakeStartMenuLink();
        var foreign = Path.Combine(paths.Base, "other", "CycleArc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "other");
        DesktopShortcuts.CreateLink(paths.DesktopLink, foreign);
        var original = File.ReadAllBytes(paths.DesktopLink);

        Assert.False(DesktopShortcuts.IsPresent(paths.Root, paths.Desktop));
        DesktopShortcuts.Apply(paths.Root, false, paths.Desktop, paths.Programs);
        Assert.Throws<IOException>(() => DesktopShortcuts.Apply(paths.Root, true, paths.Desktop, paths.Programs));
        Assert.Equal(original, File.ReadAllBytes(paths.DesktopLink));
    }

    [Fact]
    public void EnablingRequiresAnOwnedStartMenuLink()
    {
        using var paths = new Paths();
        Assert.Throws<IOException>(() => DesktopShortcuts.Apply(paths.Root, true, paths.Desktop, paths.Programs));
        Assert.False(File.Exists(paths.DesktopLink));
    }

    [Fact]
    public void EnablingRejectsAStartMenuLinkForAnotherInstall()
    {
        using var paths = new Paths();
        var foreign = Path.Combine(paths.Base, "other", "CycleArc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "other");
        DesktopShortcuts.CreateLink(paths.StartMenuLink, foreign);

        Assert.Throws<IOException>(() => DesktopShortcuts.Apply(paths.Root, true, paths.Desktop, paths.Programs));
        Assert.False(File.Exists(paths.DesktopLink));
    }

    [Fact]
    public void MalformedDesktopLinkIsNeverOverwrittenOrRemoved()
    {
        using var paths = new Paths();
        paths.MakeStartMenuLink();
        var malformed = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(paths.DesktopLink, malformed);

        Assert.False(DesktopShortcuts.IsPresent(paths.Root, paths.Desktop));
        DesktopShortcuts.Apply(paths.Root, false, paths.Desktop, paths.Programs);
        Assert.Throws<IOException>(() => DesktopShortcuts.Apply(paths.Root, true, paths.Desktop, paths.Programs));

        Assert.Equal(malformed, File.ReadAllBytes(paths.DesktopLink));
    }

    [Fact]
    public void EmptyShellFoldersNeverFallBackToTheWorkingDirectory()
    {
        using var paths = new Paths();
        paths.MakeStartMenuLink();

        Assert.Throws<IOException>(() => DesktopShortcuts.IsPresent(paths.Root, ""));
        Assert.Throws<IOException>(() => DesktopShortcuts.Apply(paths.Root, true, "", paths.Programs));
        Assert.Throws<IOException>(() => DesktopShortcuts.Apply(paths.Root, true, paths.Desktop, ""));
        Assert.False(File.Exists(paths.DesktopLink));
    }

    private sealed class Paths : IDisposable
    {
        public string Base { get; } = Path.Combine(Path.GetTempPath(), "CycleArc-shortcuts-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Base, "install");
        public string Desktop => Path.Combine(Base, "desktop");
        public string Programs => Path.Combine(Base, "programs");
        public string Launcher => Path.Combine(Root, "CycleArc.exe");
        public string CurrentExecutable => Path.Combine(Root, "current", "CycleArc.exe");
        public string DesktopLink => Path.Combine(Desktop, "CycleArc.lnk");
        public string StartMenuLink => Path.Combine(Programs, "CycleArc.lnk");

        public Paths()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CurrentExecutable)!);
            Directory.CreateDirectory(Desktop);
            Directory.CreateDirectory(Programs);
            File.WriteAllText(Launcher, "launcher");
            File.WriteAllText(CurrentExecutable, "current");
        }

        public void MakeStartMenuLink() => DesktopShortcuts.CreateLink(StartMenuLink, Launcher);

        public void Dispose()
        {
            if (Directory.Exists(Base)) Directory.Delete(Base, recursive: true);
        }
    }
}
