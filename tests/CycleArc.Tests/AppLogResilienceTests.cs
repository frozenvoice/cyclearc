using CycleArc.Services;

namespace CycleArc.Tests;

public class AppLogResilienceTests
{
    [Fact]
    public void OpeningTheLog_RemovesOnlyItsOwnExpiredDailyFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cyclearc-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
            string Make(string name) { var path = Path.Combine(directory, name); File.WriteAllText(path, "x"); return path; }
            var old = Make("cyclearc-20261001.log");
            var oldRotated = Make("cyclearc-20261001.log.1");
            var recent = Make("cyclearc-20261015.log");
            var today = Make("cyclearc-20261022.log");
            var unrelated = Make("cyclearc-install.log");
            var malformed = Make("cyclearc-2026xx01.log");
            var other = Make("notes-20200101.log");

            _ = new AppLog(directory, now);

            Assert.False(File.Exists(old));
            Assert.False(File.Exists(oldRotated));
            Assert.True(File.Exists(recent) && File.Exists(today));
            Assert.True(File.Exists(unrelated) && File.Exists(malformed) && File.Exists(other));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ALockedLogFile_DoesNotThrowFromLogging()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cyclearc-log-" + Guid.NewGuid().ToString("N"));
        var log = new AppLog(directory);
        try
        {
            using (new FileStream(log.CurrentFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                log.Info("written while another program holds the file");
                log.Error("failure while locked", new InvalidOperationException("boom"));
            }
            log.Info("after release");
            Assert.Contains("after release", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Errors_RecordTheInnermostCauseAndWhereItWasThrown_WithoutSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cyclearc-log-" + Guid.NewGuid().ToString("N"));
        var log = new AppLog(directory);
        try
        {
            try { Throw(); }
            catch (Exception ex) { log.Error("refresh failed", ex); }
            var text = File.ReadAllText(log.CurrentFile);
            Assert.Contains("InvalidOperationException: outer", text, StringComparison.Ordinal);
            Assert.Contains("<- TimeoutException: inner", text, StringComparison.Ordinal);
            Assert.Contains(nameof(Throw), text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-token-value", text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }

        static void Throw()
        {
            try { throw new TimeoutException("inner Bearer secret-token-value"); }
            catch (Exception inner) { throw new InvalidOperationException("outer", inner); }
        }
    }
}
