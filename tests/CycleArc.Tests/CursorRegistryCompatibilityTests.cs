using System.Text.Json;
using CycleArc.Codex;

namespace CycleArc.Tests;

/// <summary>
/// Compatibility fixtures for the v3 registry boundary. These tests only use temporary
/// directories and serialized registry files; they never inspect or modify a real profile.
/// </summary>
public sealed class CursorRegistryCompatibilityTests
{
    [Fact]
    public void V3UpgradeMarksBothCopiesAndRejectsStaleV2SaveWithoutOverwritingEither()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var v1 = store.LoadOrMigrate(data.Home("codex"));
        var claude = store.NewClaude("Claude");
        var v2 = v1 with { Version = 2, Profiles = [.. v1.Profiles, claude] };
        store.Save(v2);
        store.Save(v2); // Establish a complete v2 primary/backup pair.

        var cursor = store.NewCursor("Cursor");
        var v3 = v2 with { Version = 3, Profiles = [.. v2.Profiles, cursor] };
        store.Save(v3);

        var primaryPath = Path.Combine(data.Root, "codex-accounts.json");
        var backupPath = primaryPath + ".bak";
        var primary = File.ReadAllText(primaryPath);
        var backup = File.ReadAllText(backupPath);
        Assert.Equal(3, ReadVersion(primary));
        Assert.Equal(3, ReadVersion(backup));
        Assert.Contains(cursor.Id, ReadProfileIds(primary));
        // During the version transition the backup is the previous-good profile set,
        // with only its version raised to v3. This keeps both copies unreadable by
        // pre-Cursor binaries while preserving rollback data. A later successful save
        // publishes the new Cursor profile to the backup as well.
        Assert.DoesNotContain(cursor.Id, ReadProfileIds(backup));

        // The pre-Cursor reader accepted only registry versions 1 and 2. Both copies
        // carry v3 so an older executable cannot treat the Cursor registry as Codex-only.
        Assert.False(LegacyV2ReaderAccepts(primary));
        Assert.False(LegacyV2ReaderAccepts(backup));

        Assert.Throws<InvalidDataException>(() => store.Save(v2 with { SelectedId = v2.Profiles[0].Id }));
        Assert.Equal(primary, File.ReadAllText(primaryPath));
        Assert.Equal(backup, File.ReadAllText(backupPath));
        Assert.Contains(cursor.Id, store.LoadOrMigrate(data.Root).Profiles.Select(profile => profile.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecoveryUsesOnlyTheValidV3CopyAndNeverRewritesTheOther(bool corruptPrimary)
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var initial = store.LoadOrMigrate(data.Home("codex"));
        var cursor = store.NewCursor("Cursor");
        var state = initial with { Version = 3, Profiles = [.. initial.Profiles, cursor] };
        store.Save(state);
        store.Save(state); // Ensure primary and backup both contain the Cursor profile.

        var primaryPath = Path.Combine(data.Root, "codex-accounts.json");
        var backupPath = primaryPath + ".bak";
        var validPrimary = File.ReadAllText(primaryPath);
        var validBackup = File.ReadAllText(backupPath);
        var corruptPath = corruptPrimary ? primaryPath : backupPath;
        File.WriteAllText(corruptPath, "{ damaged registry }");

        var recoveredStore = new CodexAccountStore(data.Root);
        var recovered = recoveredStore.LoadOrMigrate(data.Root);
        Assert.Contains(cursor.Id, recovered.Profiles.Select(profile => profile.Id));
        Assert.Equal(corruptPrimary, recoveredStore.RecoveredFromBackup);
        if (corruptPrimary)
        {
            Assert.Equal("{ damaged registry }", File.ReadAllText(primaryPath));
            Assert.Equal(validBackup, File.ReadAllText(backupPath));
        }
        else
        {
            Assert.Equal(validPrimary, File.ReadAllText(primaryPath));
            Assert.Equal("{ damaged registry }", File.ReadAllText(backupPath));
        }
    }

    [Fact]
    public void SavingAfterPrimaryRecoveryKeepsV3ProviderReferencesInBothCopies()
    {
        using var data = new AccountTestDirectory();
        var store = new CodexAccountStore(data.Root);
        var initial = store.LoadOrMigrate(data.Home("codex"));
        var cursor = store.NewCursor("Cursor");
        var state = initial with { Version = 3, Profiles = [.. initial.Profiles, cursor] };
        store.Save(state);
        store.Save(state);

        var primaryPath = Path.Combine(data.Root, "codex-accounts.json");
        File.WriteAllText(primaryPath, "{ damaged primary }");
        var recoveredStore = new CodexAccountStore(data.Root);
        var recovered = recoveredStore.LoadOrMigrate(data.Root);
        Assert.True(recoveredStore.RecoveredFromBackup);

        recoveredStore.Save(recovered with { SelectedId = cursor.Id });

        var primary = File.ReadAllText(primaryPath);
        var backup = File.ReadAllText(primaryPath + ".bak");
        Assert.Equal(3, ReadVersion(primary));
        Assert.Equal(3, ReadVersion(backup));
        Assert.Contains(cursor.Id, ReadProfileIds(primary));
        Assert.Contains(cursor.Id, ReadProfileIds(backup));
        Assert.Equal(cursor.Id, recoveredStore.LoadOrMigrate(data.Root).SelectedId);
    }

    private static int ReadVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Version").GetInt32();
    }

    private static IReadOnlyList<string> ReadProfileIds(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Profiles").EnumerateArray()
            .Select(profile => profile.GetProperty("Id").GetString()!)
            .ToArray();
    }

    private static bool LegacyV2ReaderAccepts(string json)
    {
        using var document = JsonDocument.Parse(json);
        var version = document.RootElement.GetProperty("Version").GetInt32();
        return version is 1 or 2;
    }
}
