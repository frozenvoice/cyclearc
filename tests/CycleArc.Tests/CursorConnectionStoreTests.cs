using System.Security.Cryptography;
using System.Text;
using CycleArc.Codex;
using CycleArc.Providers.Cursor;

namespace CycleArc.Tests;

public sealed class CursorConnectionStoreTests
{
    [Fact]
    public void BackupRecoveryPreservesDisconnectTombstone()
    {
        using var data = new Data();
        var store = new CursorConnectionStore(data.Accounts, data.Profile.Id);
        var connected = Binding(data.Profile.Id, false);
        store.Save(connected);
        store.Save(connected with { Disconnected = true });
        File.WriteAllText(store.Path, "{broken");

        var read = store.Read();

        Assert.False(read.Unavailable);
        Assert.True(read.Binding!.Disconnected);
    }

    [Fact]
    public void AStoreCannotAcceptABindingForAnotherProfile()
    {
        using var data = new Data();
        var store = new CursorConnectionStore(data.Accounts, data.Profile.Id);
        var other = Guid.NewGuid().ToString("N");

        Assert.Throws<InvalidDataException>(() => store.Save(Binding(other, false)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InterruptedTwoFileCommitKeepsTheDisconnectedCopy(bool disconnectInterrupted)
    {
        using var data = new Data();
        var store = new CursorConnectionStore(data.Accounts, data.Profile.Id);
        var connected = Binding(data.Profile.Id, false);
        store.Save(connected);
        var connectedJson = File.ReadAllText(store.Path);
        store.Save(connected with { Disconnected = true });
        // Simulate either crash between backup and primary replacement. Reconnection
        // only takes effect when both connected copies have committed.
        File.WriteAllText(disconnectInterrupted ? store.Path : store.Path + ".bak", connectedJson);

        Assert.True(new CursorConnectionStore(data.Accounts, data.Profile.Id).Read().Binding!.Disconnected);
        store.Save(connected with { Generation = Guid.NewGuid().ToString("N") });
        Assert.False(store.Read().Binding!.Disconnected);
    }

    [Fact]
    public void NullFingerprintCannotEscapeAsAnExceptionOrAConnection()
    {
        using var data = new Data();
        var store = new CursorConnectionStore(data.Accounts, data.Profile.Id);
        store.Save(Binding(data.Profile.Id, false));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!;
        json["identityFingerprint"] = null;
        File.WriteAllText(store.Path, json.ToJsonString());
        File.WriteAllText(store.Path + ".bak", json.ToJsonString());

        var result = store.Read();
        Assert.True(result.Unavailable);
        Assert.Null(result.Binding);
    }

    private static CursorConnectionBinding Binding(string profileId, bool disconnected) => new(1, profileId,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cursor-account"))),
        DateTimeOffset.UtcNow.AddMinutes(-1), Guid.NewGuid().ToString("N"), disconnected);

    private sealed class Data : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "cyclearc-cursor-store-" + Guid.NewGuid().ToString("N"));
        public CodexAccountStore Accounts { get; }
        public CodexAccountProfile Profile { get; }
        public Data()
        {
            Accounts = new(Root);
            var initial = Accounts.LoadOrMigrate(System.IO.Path.Combine(Root, "codex-home"));
            Profile = Accounts.NewCursor("Cursor fixture");
            Accounts.Save(initial with { Version = 3, Profiles = initial.Profiles.Append(Profile).ToArray() });
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
