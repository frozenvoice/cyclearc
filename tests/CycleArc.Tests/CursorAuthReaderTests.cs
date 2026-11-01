using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using CycleArc.Providers.Cursor;

namespace CycleArc.Tests;

public sealed class CursorAuthReaderTests
{
    [Fact]
    public void ReadsOnlyTheExactAccessTokenKey()
    {
        using var root = new TempRoot();
        using (var db = Open(root.Path))
        {
            using var create = db.CreateCommand();
            create.CommandText = "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);";
            create.ExecuteNonQuery();
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO ItemTable(key,value) VALUES ($key,$value),($other,$secret);";
            insert.Parameters.AddWithValue("$key", CursorAuthStateDatabaseReader.AccessTokenKey);
            insert.Parameters.AddWithValue("$value", "jwt-fixture");
            insert.Parameters.AddWithValue("$other", "cursorAuth/refreshToken");
            insert.Parameters.AddWithValue("$secret", "refresh-secret-must-not-be-read");
            insert.ExecuteNonQuery();
        }

        var read = new CursorAuthStateDatabaseReader(Path.Combine(root.Path, "state.vscdb")).Read();

        Assert.Equal("jwt-fixture", read.AccessToken);
        Assert.Null(read.Failure);
        Assert.DoesNotContain("refresh", read.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingOrOversizedExactValueDoesNotBecomeAUsableToken()
    {
        using var root = new TempRoot();
        using (var db = Open(root.Path))
        {
            using var create = db.CreateCommand();
            create.CommandText = "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);";
            create.ExecuteNonQuery();
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO ItemTable(key,value) VALUES ($key,$value);";
            insert.Parameters.AddWithValue("$key", CursorAuthStateDatabaseReader.AccessTokenKey);
            insert.Parameters.AddWithValue("$value", new string('x', CursorAuthStateDatabaseReader.MaxTokenCharacters + 1));
            insert.ExecuteNonQuery();
        }

        var read = new CursorAuthStateDatabaseReader(Path.Combine(root.Path, "state.vscdb")).Read();

        Assert.Null(read.AccessToken);
        Assert.Equal("cursor-live-auth-required", read.Failure);
    }

    [Fact]
    public void UnwrapsSafeStorageAccessTokenAndDoesNotReadTheRefreshToken()
    {
        var jwt = "header.payload.signature";
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        using var root = new TempRoot();
        var database = Path.Combine(root.Path, "state.vscdb");
        var localState = Path.Combine(root.Path, CursorAuthStateDatabaseReader.LocalStateFileName);
        WriteDatabase(database, Seal(jwt, key), Seal("refresh-secret-must-not-be-read", key));
        WriteLocalState(localState);

        var read = new CursorAuthStateDatabaseReader(() => database, () => localState, blob => key).Read();

        Assert.Equal(jwt, read.AccessToken);
        Assert.Null(read.Failure);
        Assert.Equal("available", read.ToString());
        Assert.DoesNotContain("refresh", read.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CursorAuthStateDatabaseReader.SafeStoragePrefix, read.AccessToken, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaintextTokenDoesNotOpenLocalState()
    {
        using var root = new TempRoot();
        var database = Path.Combine(root.Path, "state.vscdb");
        WriteDatabase(database, "jwt-fixture", "refresh-secret-must-not-be-read");

        var read = new CursorAuthStateDatabaseReader(
            () => database,
            () => throw new InvalidOperationException("local state should stay unread"),
            _ => throw new InvalidOperationException("dpapi should stay unused")).Read();

        Assert.Equal("jwt-fixture", read.AccessToken);
        Assert.Null(read.Failure);
    }

    [Fact]
    public void SealedTokenThatCannotBeUnwrappedIsUnreadableRatherThanSignedOut()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        using var root = new TempRoot();
        var database = Path.Combine(root.Path, "state.vscdb");
        var stored = Seal("header.payload.signature", key);
        WriteDatabase(database, stored, "refresh-secret-must-not-be-read");

        var read = new CursorAuthStateDatabaseReader(() => database, () => Path.Combine(root.Path, "missing"), _ => key).Read();

        Assert.Null(read.AccessToken);
        Assert.Equal("cursor-live-unavailable", read.Failure);
        Assert.DoesNotContain(stored, read.ToString(), StringComparison.Ordinal);
    }

    private static void WriteDatabase(string path, string accessToken, string refreshToken)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var create = db.CreateCommand();
        create.CommandText = "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);";
        create.ExecuteNonQuery();
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO ItemTable(key,value) VALUES ($key,$value),($other,$secret);";
        insert.Parameters.AddWithValue("$key", CursorAuthStateDatabaseReader.AccessTokenKey);
        insert.Parameters.AddWithValue("$value", accessToken);
        insert.Parameters.AddWithValue("$other", "cursorAuth/refreshToken");
        insert.Parameters.AddWithValue("$secret", refreshToken);
        insert.ExecuteNonQuery();
    }

    private static void WriteLocalState(string path)
    {
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes("DPAPI").Concat(new byte[] { 9 }).ToArray());
        File.WriteAllText(path, "{\"os_crypt\":{\"encrypted_key\":\"" + encoded + "\"}}");
    }

    private static string Seal(string token, byte[] key)
    {
        var nonce = Enumerable.Range(0, 12).Select(i => (byte)(20 + i)).ToArray();
        var plain = Encoding.UTF8.GetBytes(token);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(nonce, plain, cipher, tag);
        var packed = new byte[3 + nonce.Length + cipher.Length + tag.Length];
        Encoding.ASCII.GetBytes("v10").CopyTo(packed, 0);
        nonce.CopyTo(packed, 3);
        cipher.CopyTo(packed, 15);
        tag.CopyTo(packed, 15 + cipher.Length);
        return CursorAuthStateDatabaseReader.SafeStoragePrefix + Convert.ToBase64String(packed);
    }

    private static SqliteConnection Open(string root)
    {
        var path = Path.Combine(root, "state.vscdb");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "cyclearc-cursor-auth-" + Guid.NewGuid().ToString("N"));
        public TempRoot() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
