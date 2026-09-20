using System.Text;
using Microsoft.Data.Sqlite;

namespace CycleArc.Providers.Cursor;

/// <summary>
/// Reads only Cursor's current access-token row from the bounded, read-only state database.
/// It deliberately does not inspect refresh tokens, cookies, history, WAL files, or logs.
/// </summary>
public sealed class CursorAuthStateDatabaseReader : ICursorAuthSource
{
    public const string AccessTokenKey = "cursorAuth/accessToken";
    public const string DefaultDatabaseRelativePath = "Cursor\\User\\globalStorage\\state.vscdb";
    public const int MaxTokenCharacters = 16 * 1024;

    private readonly Func<string?> _path;

    public CursorAuthStateDatabaseReader(string? databasePath = null)
        : this(() => databasePath ?? DefaultDatabasePath())
    {
    }

    public CursorAuthStateDatabaseReader(Func<string?> path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public CursorAuthRead Read()
    {
        var path = _path();
        if (string.IsNullOrWhiteSpace(path)) return new(null, "cursor-live-auth-required");
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) return new(null, "cursor-live-auth-required");
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.DefaultTimeout = 2;
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only = ON;";
                pragma.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT substr(value, 1, $max) FROM ItemTable "
                + "WHERE key = $key AND length(value) <= $max LIMIT 1;";
            command.Parameters.AddWithValue("$key", AccessTokenKey);
            command.Parameters.AddWithValue("$max", MaxTokenCharacters);
            command.CommandTimeout = 2;
            using var reader = command.ExecuteReader(System.Data.CommandBehavior.SingleRow);
            if (!reader.Read() || reader.IsDBNull(0)) return new(null, "cursor-live-auth-required");

            var token = ReadString(reader, 0);
            return token is { Length: > 0 } && token.Length <= MaxTokenCharacters
                && !token.Any(char.IsControl)
                ? new(token)
                : new(null, "cursor-live-unavailable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new(null, "cursor-live-unavailable");
        }
    }

    public static string? DefaultDatabasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, DefaultDatabaseRelativePath);
    }

    private static string? ReadString(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetFieldType(ordinal) == typeof(byte[]))
        {
            var bytes = (byte[])reader.GetValue(ordinal);
            if (bytes.Length > MaxTokenCharacters * 4) return null;
            return Encoding.UTF8.GetString(bytes);
        }

        return reader.GetValue(ordinal) switch
        {
            string text => text,
            _ => null
        };
    }
}
