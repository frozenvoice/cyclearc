using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class DesktopDistributionTests
{
    [Fact]
    public void StandaloneAndVelopackPortableNeverAdoptAManagedInstallation()
    {
        var root = Directory.CreateTempSubdirectory("cyclearc-distribution-");
        try
        {
            var content = Directory.CreateDirectory(Path.Combine(root.FullName, "current"));
            var executable = Path.Combine(content.FullName, "CycleArc.exe");
            File.WriteAllText(executable, "synthetic executable");
            Assert.False(DesktopDistribution.HasManagedLayout(executable));
            File.WriteAllText(Path.Combine(root.FullName, "Update.exe"), "synthetic updater");
            Assert.False(DesktopDistribution.HasManagedLayout(executable));
            File.WriteAllText(Path.Combine(content.FullName, "sq.version"), "synthetic manifest");
            Assert.True(DesktopDistribution.HasManagedLayout(executable));
            File.WriteAllBytes(Path.Combine(root.FullName, ".portable"), []);
            Assert.False(DesktopDistribution.HasManagedLayout(executable));
            // A loose executable alongside a separate managed install is still standalone.
            Assert.False(DesktopDistribution.HasManagedLayout(Path.Combine(root.FullName, "CycleArc.exe")));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void MissingDataRootReportsItsActualFailureAndReleasesTheSessionMutex()
    {
        var root = Directory.CreateTempSubdirectory("cyclearc-data-root-");
        var absent = Path.Combine(root.FullName, "missing");
        var name = "CycleArc.Test." + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Throws<DirectoryNotFoundException>(() => DesktopInstanceLease.TryAcquire(name, absent));
            Directory.CreateDirectory(absent);
            using var next = DesktopInstanceLease.TryAcquire(name, absent);
            Assert.NotNull(next);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void DifferentSessionMutexesCannotWriteTheSameUserDataWhileTheDesktopIsOpen()
    {
        var root = Directory.CreateTempSubdirectory("cyclearc-data-lease-");
        var firstName = "CycleArc.Test." + Guid.NewGuid().ToString("N");
        var secondName = "CycleArc.Test." + Guid.NewGuid().ToString("N");
        try
        {
            using (var first = DesktopInstanceLease.TryAcquire(firstName, root.FullName))
            {
                Assert.NotNull(first);
                Assert.Throws<IOException>(() => DesktopInstanceLease.TryAcquire(secondName, root.FullName));
            }
            using var next = DesktopInstanceLease.TryAcquire(secondName, root.FullName);
            Assert.NotNull(next);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void OnlyTheExactDevelopmentExecutableKeepsDevelopmentStartupBehavior()
    {
        var development = Path.Combine(Path.GetTempPath(), "Programs", "CycleArc-dev", "CycleArc.exe");
        Assert.True(DesktopDistribution.IsDevelopmentInstall(development.ToUpperInvariant(), development));
        Assert.False(DesktopDistribution.IsDevelopmentInstall(Path.Combine(Path.GetTempPath(), "portable", "CycleArc.exe"), development));
        Assert.False(DesktopDistribution.IsDevelopmentInstall(null, development));
    }
}
