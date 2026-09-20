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
