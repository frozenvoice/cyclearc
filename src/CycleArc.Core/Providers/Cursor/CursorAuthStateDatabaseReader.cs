using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CycleArc.Providers.Cursor;

/// <summary>
/// Reads only Cursor's current access-token row from the bounded, read-only state database.
/// A <c>cursor-safe-storage:v1:</c> value is Electron safeStorage ciphertext and is unwrapped
/// in memory with the Chromium OSCrypt key from Cursor's Local State. Plaintext JWTs are
/// unchanged. The refresh-token row, cookies, history, WAL files and logs are not read.
/// </summary>
public sealed class CursorAuthStateDatabaseReader : ICursorAuthSource
{
    public const string AccessTokenKey = "cursorAuth/accessToken";
    public const string SafeStoragePrefix = "cursor-safe-storage:v1:";
    public const string DefaultDatabaseRelativePath = "Cursor\\User\\globalStorage\\state.vscdb";
    public const string LocalStateFileName = "Local State";
    public const int MaxTokenCharacters = 16 * 1024;
    public const int MaxLocalStateBytes = 1024 * 1024;

    private const string EncryptionPrefix = "v10";
    private const string DpapiPrefix = "DPAPI";
    private const int MaxEncryptedKeyBytes = 4096;
    private const uint CryptProtectUiForbidden = 0x1;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        MaxDepth = 8,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private readonly Func<string?> _path;
    private readonly Func<string?> _localStatePath;
    private readonly Func<byte[], byte[]?> _unprotect;

    public CursorAuthStateDatabaseReader(string? databasePath = null, string? localStatePath = null)
        : this(() => databasePath ?? DefaultDatabasePath(),
            () => localStatePath ?? DefaultLocalStatePath(),
            UnprotectWithWindowsDpapi)
    {
    }

    public CursorAuthStateDatabaseReader(Func<string?> path)
        : this(path, DefaultLocalStatePath, UnprotectWithWindowsDpapi)
    {
    }

    public CursorAuthStateDatabaseReader(Func<string?> path, Func<string?> localStatePath, Func<byte[], byte[]?> unprotect)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _localStatePath = localStatePath ?? throw new ArgumentNullException(nameof(localStatePath));
        _unprotect = unprotect ?? throw new ArgumentNullException(nameof(unprotect));
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

            var stored = ReadString(reader, 0);
            if (stored is not { Length: > 0 } || stored.Length > MaxTokenCharacters || stored.Any(char.IsControl))
                return new(null, "cursor-live-unavailable");
            if (!stored.StartsWith(SafeStoragePrefix, StringComparison.Ordinal)) return new(stored);

            var token = Unwrap(stored);
            return token is { Length: > 0 } && token.Length <= MaxTokenCharacters && !token.Any(char.IsControl)
                ? new(token)
                : new(null, "cursor-live-unavailable");
        }
        catch (Exception ex) when (IsExpectedReaderFailure(ex))
        {
            return new(null, "cursor-live-unavailable");
        }
    }

    public static string? DefaultDatabasePath()
    {
        var appData = ApplicationDataPath();
        return appData is null ? null : Path.Combine(appData, DefaultDatabaseRelativePath);
    }

    public static string? DefaultLocalStatePath()
    {
        var appData = ApplicationDataPath();
        return appData is null ? null : Path.Combine(appData, "Cursor", LocalStateFileName);
    }

    private string? Unwrap(string stored)
    {
        byte[]? packed = null;
        byte[]? localState = null;
        byte[]? encryptedKey = null;
        byte[]? dpapiBlob = null;
        byte[]? aesKey = null;
        byte[]? plaintext = null;
        try
        {
            var payload = stored[SafeStoragePrefix.Length..];
            if (payload.Length == 0) return null;
            try { packed = Convert.FromBase64String(payload); }
            catch (FormatException) { return null; }
            if (packed.Length == 0 || packed.Length > MaxTokenCharacters) return null;

            var statePath = _localStatePath();
            if (string.IsNullOrWhiteSpace(statePath)) return null;
            localState = ReadBounded(Path.GetFullPath(statePath));
            if (localState is null) return null;
            encryptedKey = ReadEncryptedKey(localState);
            if (encryptedKey is null
                || encryptedKey.Length <= DpapiPrefix.Length
                || !HasPrefix(encryptedKey, DpapiPrefix))
                return null;

            dpapiBlob = encryptedKey.AsSpan(DpapiPrefix.Length).ToArray();
            aesKey = _unprotect(dpapiBlob);
            if (aesKey is null || aesKey.Length != 32) return null;
            plaintext = DecryptV10(packed, aesKey);
            if (plaintext is null || plaintext.Length == 0) return null;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(plaintext);
        }
        catch (Exception ex) when (IsExpectedReaderFailure(ex)) { return null; }
        finally
        {
            Zero(packed);
            Zero(localState);
            Zero(encryptedKey);
            Zero(dpapiBlob);
            Zero(aesKey);
            Zero(plaintext);
        }
    }

    private static byte[]? ReadEncryptedKey(byte[] localState)
    {
        using var document = JsonDocument.Parse(localState, JsonOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryUnique(root, "os_crypt", out var osCrypt)
            || osCrypt.ValueKind != JsonValueKind.Object
            || !TryUnique(osCrypt, "encrypted_key", out var value)
            || value.ValueKind != JsonValueKind.String
            || !value.TryGetBytesFromBase64(out var bytes)
            || bytes.Length is 0 or > MaxEncryptedKeyBytes)
            return null;
        return bytes;
    }

    private static byte[]? DecryptV10(byte[] encrypted, byte[] aesKey)
    {
        const int prefixLength = 3;
        const int nonceLength = 12;
        const int tagLength = 16;
        if (encrypted.Length <= prefixLength + nonceLength + tagLength
            || !HasPrefix(encrypted, EncryptionPrefix)) return null;

        var nonce = encrypted.AsSpan(prefixLength, nonceLength).ToArray();
        var cipherLength = encrypted.Length - prefixLength - nonceLength - tagLength;
        var ciphertext = encrypted.AsSpan(prefixLength + nonceLength, cipherLength).ToArray();
        var tag = encrypted.AsSpan(encrypted.Length - tagLength, tagLength).ToArray();
        var plaintext = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(aesKey, tagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (Exception ex) when (IsExpectedReaderFailure(ex))
        {
            Zero(plaintext);
            return null;
        }
        finally
        {
            Zero(nonce);
            Zero(ciphertext);
            Zero(tag);
        }
    }

    private static byte[]? ReadBounded(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxLocalStateBytes) return null;
            var bytes = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0) return null;
                offset += count;
            }
            return bytes;
        }
        catch (Exception ex) when (IsExpectedReaderFailure(ex)) { return null; }
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

    private static bool TryUnique(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.Ordinal)) continue;
            if (found) return false;
            value = property.Value;
            found = true;
        }
        return found;
    }

    private static string? ApplicationDataPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrWhiteSpace(appData) ? null : appData;
    }

    private static bool HasPrefix(byte[] value, string prefix) =>
        value.AsSpan().StartsWith(Encoding.ASCII.GetBytes(prefix));

    private static bool IsExpectedReaderFailure(Exception ex) => ex is
        IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException
        or ArgumentException or NotSupportedException or JsonException or FormatException
        or CryptographicException or OverflowException or PlatformNotSupportedException
        or DllNotFoundException or EntryPointNotFoundException or DecoderFallbackException;

    private static void Zero(byte[]? bytes)
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }

    private static byte[]? UnprotectWithWindowsDpapi(byte[] blob)
    {
        if (!OperatingSystem.IsWindows() || blob.Length == 0) return null;

        var input = IntPtr.Zero;
        var output = IntPtr.Zero;
        var outputBlob = default(DataBlob);
        try
        {
            input = Marshal.AllocHGlobal(blob.Length);
            Marshal.Copy(blob, 0, input, blob.Length);
            var inputBlob = new DataBlob { cbData = (uint)blob.Length, pbData = input };
            var success = CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob);
            output = outputBlob.pbData;
            if (!success || output == IntPtr.Zero
                || outputBlob.cbData == 0 || outputBlob.cbData > 1024)
                return null;

            var key = new byte[(int)outputBlob.cbData];
            Marshal.Copy(output, key, 0, (int)outputBlob.cbData);
            var zeros = new byte[(int)outputBlob.cbData];
            Marshal.Copy(zeros, 0, output, zeros.Length);
            return key;
        }
        catch (Exception ex) when (IsExpectedReaderFailure(ex)) { return null; }
        finally
        {
            if (input != IntPtr.Zero)
            {
                try { Marshal.Copy(new byte[blob.Length], 0, input, blob.Length); } catch { }
                Marshal.FreeHGlobal(input);
            }
            if (output != IntPtr.Zero)
            {
                if (outputBlob.cbData is > 0 and <= 1024)
                {
                    try { Marshal.Copy(new byte[(int)outputBlob.cbData], 0, output, (int)outputBlob.cbData); } catch { }
                }
                LocalFree(output);
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint cbData;
        public IntPtr pbData;
    }
}
