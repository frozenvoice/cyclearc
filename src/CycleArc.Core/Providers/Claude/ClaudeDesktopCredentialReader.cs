using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CycleArc.Providers.Claude;

/// <summary>
/// A short-lived Claude Desktop OAuth access credential. The refresh token is
/// deliberately not represented by this type; Claude Desktop owns its refresh
/// lifecycle.
/// </summary>
public sealed class ClaudeDesktopCredential
{
    public ClaudeDesktopCredential(string accessToken, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("Access token is required.", nameof(accessToken));
        AccessToken = accessToken;
        ExpiresAt = expiresAt;
    }

    public string AccessToken { get; }
    public DateTimeOffset ExpiresAt { get; }

    // Do not let a diagnostic or an exception accidentally put the bearer
    // credential in logs or UI text.
    public override string ToString() => nameof(ClaudeDesktopCredential);
}

public sealed record ClaudeDesktopCredentialRead(
    IReadOnlyList<ClaudeDesktopCredential> Credentials,
    string? Failure = null);

public interface IClaudeDesktopCredentialSource
{
    ClaudeDesktopCredentialRead Read(DateTimeOffset now);
}

/// <summary>
/// Reads the OAuth cache used by the installed Claude Desktop application.
/// This is a bounded, read-only source. It never writes Desktop state and never
/// reads refresh tokens, cookies, prompts, responses, or conversation files.
/// </summary>
public sealed class ClaudeDesktopCredentialReader : IClaudeDesktopCredentialSource
{
    public const int MaxInputBytes = 1024 * 1024;
    public const int MaxJsonDepth = 16;
    public const int MaxCandidates = 4;
    public const string ConfigFileName = "config.json";
    public const string LocalStateFileName = "Local State";

    private const string TokenCacheProperty = "oauth:tokenCacheV2";
    private const string ApiHost = "https://api.anthropic.com";
    private const string EncryptionPrefix = "v10";
    private const string DpapiPrefix = "DPAPI";
    private const int MaxInspectedEntries = 64;
    private const uint CryptProtectUiForbidden = 0x1;

    // These are the production Claude Desktop OAuth clients observed in the
    // shipped application. An allowlist keeps arbitrary config keys from being
    // treated as Claude credentials.
    private static readonly HashSet<string> AcceptedClientIds = new(StringComparer.Ordinal)
    {
        "89355bc3-cbfd-4382-905b-976645cad410",
        "9d1c250a-e61b-44d9-88ed-5944d1962f5e",
        "a473d7bb-17ac-43a7-abc0-a1343d7c2805"
    };

    // A profile scope is required so the caller can verify the token's signed
    // in account and organization before accepting a quota response. The exact
    // least-scope entry is preferred below; broader production Code scopes are
    // accepted only as a fallback.
    private static readonly HashSet<string> AcceptedScopes = new(StringComparer.Ordinal)
    {
        "user:profile",
        "user:inference user:profile user:sessions:claude_code",
        "user:inference user:file_upload user:profile",
        "user:inference user:file_upload user:profile user:sessions:claude_code"
    };

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        MaxDepth = MaxJsonDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private readonly IReadOnlyList<string> _roots;
    private readonly Func<byte[], byte[]?> _unprotect;

    /// <param name="roots">
    /// Desktop data directories. Supplying this parameter is intended for
    /// isolated tests; the default contains only known Claude Desktop roots.
    /// </param>
    /// <param name="unprotect">
    /// Optional DPAPI adapter. It receives only the DPAPI blob (without the
    /// ASCII <c>DPAPI</c> framing) and returns the unwrapped AES key. Production
    /// uses the current Windows user's CryptUnprotectData implementation.
    /// </param>
    public ClaudeDesktopCredentialReader(
        IEnumerable<string>? roots = null,
        Func<byte[], byte[]?>? unprotect = null)
    {
        var candidates = roots ?? DiscoverDefaultRoots();
        _roots = candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _unprotect = unprotect ?? UnprotectWithWindowsDpapi;
    }

    public ClaudeDesktopCredentialRead Read(DateTimeOffset now)
    {
        var candidates = new List<TokenCandidate>();
        var unavailable = false;
        var sawCache = false;

        foreach (var root in _roots)
        {
            var result = ReadRoot(root, now);
            if (result.Candidates.Count > 0)
            {
                sawCache = true;
                candidates.AddRange(result.Candidates);
            }
            unavailable |= result.Unavailable;
            sawCache |= result.SawCache;
        }

        if (candidates.Count == 0)
            return new(Array.Empty<ClaudeDesktopCredential>(), unavailable && sawCache
                ? "claude-live-unavailable"
                : "claude-live-auth-required");

        var selected = candidates
            .GroupBy(candidate => (candidate.AccountId, candidate.OrganizationId))
            .Select(group => group
                .OrderBy(candidate => candidate.IsProfileOnly ? 0 : 1)
                .ThenBy(candidate => candidate.ScopeCount)
                .ThenByDescending(candidate => candidate.ExpiresAt)
                .First())
            .OrderBy(candidate => candidate.IsProfileOnly ? 0 : 1)
            .ThenBy(candidate => candidate.ScopeCount)
            .ThenBy(candidate => candidate.AccountId, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .Select(candidate => new ClaudeDesktopCredential(candidate.AccessToken, candidate.ExpiresAt))
            .ToArray();

        return new(selected);
    }

    private RootReadResult ReadRoot(string root, DateTimeOffset now)
    {
        var config = ReadBoundedFile(Path.Combine(root, ConfigFileName));
        if (config.Missing) return RootReadResult.Missing;
        if (config.Data is null) return RootReadResult.UnavailableResult;

        byte[]? encryptedCache = null;
        byte[]? localState = null;
        try
        {
            var cacheRead = ReadCacheValue(config.Data);
            if (cacheRead.Missing) return RootReadResult.Missing;
            if (cacheRead.Data is null) return RootReadResult.UnavailableResult;
            encryptedCache = cacheRead.Data;

            var state = ReadBoundedFile(Path.Combine(root, LocalStateFileName));
            if (state.Missing) return RootReadResult.Missing;
            if (state.Data is null) return RootReadResult.UnavailableResult;
            localState = state.Data;

            byte[]? encryptedKey = null;
            byte[]? dpapiBlob = null;
            byte[]? aesKey = null;
            byte[]? plaintext = null;
            try
            {
                var encryptedKeyRead = ReadEncryptedKey(localState);
                if (encryptedKeyRead.Missing) return RootReadResult.Missing;
                if (encryptedKeyRead.Data is null) return RootReadResult.UnavailableResult;
                encryptedKey = encryptedKeyRead.Data;
                if (encryptedKey.Length <= DpapiPrefix.Length
                    || !HasPrefix(encryptedKey, DpapiPrefix))
                    return RootReadResult.UnavailableResult;

                dpapiBlob = encryptedKey.AsSpan(DpapiPrefix.Length).ToArray();
                aesKey = _unprotect(dpapiBlob);
                if (aesKey is null || aesKey.Length != 32)
                    return RootReadResult.UnavailableResult;

                plaintext = DecryptCache(encryptedCache, aesKey);
                if (plaintext is null) return RootReadResult.UnavailableResult;
                return ParseCache(plaintext, now);
            }
            catch (Exception ex) when (IsExpectedReaderFailure(ex))
            {
                return RootReadResult.UnavailableResult;
            }
            finally
            {
                Zero(encryptedKey);
                Zero(dpapiBlob);
                Zero(aesKey);
                Zero(plaintext);
            }
        }
        catch (Exception ex) when (IsExpectedReaderFailure(ex))
        {
            return RootReadResult.UnavailableResult;
        }
        finally
        {
            Zero(config.Data);
            Zero(localState);
            Zero(encryptedCache);
        }
    }

    private static EncodedValueRead ReadCacheValue(byte[] configBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(configBytes, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return EncodedValueRead.InvalidValue;
            if (!TryGetUniqueProperty(root, TokenCacheProperty, out var value, out var present))
                return EncodedValueRead.InvalidValue;
            if (!present) return EncodedValueRead.MissingValue;
            if (value.ValueKind != JsonValueKind.String)
                return EncodedValueRead.InvalidValue;

            return value.TryGetBytesFromBase64(out var bytes)
                ? new(bytes, false, false)
                : EncodedValueRead.InvalidValue;
        }
        catch (Exception ex) when (IsExpectedReaderFailure(ex)) { return EncodedValueRead.InvalidValue; }
    }

    private static EncodedValueRead ReadEncryptedKey(byte[] localStateBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(localStateBytes, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return EncodedValueRead.InvalidValue;
            if (!TryGetUniqueProperty(root, "os_crypt", out var osCrypt, out var osCryptPresent))
                return EncodedValueRead.InvalidValue;
            if (!osCryptPresent) return EncodedValueRead.MissingValue;
            if (osCrypt.ValueKind != JsonValueKind.Object) return EncodedValueRead.InvalidValue;
            if (!TryGetUniqueProperty(osCrypt, "encrypted_key", out var value, out var valuePresent))
                return EncodedValueRead.InvalidValue;
            if (!valuePresent) return EncodedValueRead.MissingValue;
            if (value.ValueKind != JsonValueKind.String)
                return EncodedValueRead.InvalidValue;

            return value.TryGetBytesFromBase64(out var bytes)
                ? new(bytes, false, false)
                : EncodedValueRead.InvalidValue;
        }
        catch (Exception ex) when (IsExpectedReaderFailure(ex)) { return EncodedValueRead.InvalidValue; }
    }

    private static byte[]? DecryptCache(byte[] encrypted, byte[] aesKey)
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

    private static RootReadResult ParseCache(byte[] plaintext, DateTimeOffset now)
    {
        var candidates = new List<TokenCandidate>();
        try
        {
            using var document = JsonDocument.Parse(plaintext, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return RootReadResult.UnavailableResult;

            var inspected = 0;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (++inspected > MaxInspectedEntries) return RootReadResult.UnavailableResult;
                if (!names.Add(property.Name)) return RootReadResult.UnavailableResult;
                if (!TryParseCacheKey(property.Name, out var accountId, out var organizationId, out var scope,
                        out var clientId))
                    continue;
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetUniqueProperty(property.Value, "token", out var tokenValue, out var tokenPresent))
                    return RootReadResult.UnavailableResult;
                if (!tokenPresent || tokenValue.ValueKind != JsonValueKind.String)
                    continue;
                var token = tokenValue.GetString();
                if (string.IsNullOrWhiteSpace(token) || token.Length > 8192
                    || token.Any(char.IsControl)) continue;
                if (!TryGetUniqueProperty(property.Value, "expiresAt", out var expiresValue, out var expiresPresent))
                    return RootReadResult.UnavailableResult;
                if (!expiresPresent
                    || !TryReadExpiresAt(expiresValue, out var expiresAt)
                    || expiresAt <= now)
                    continue;

                candidates.Add(new(accountId, organizationId, clientId, scope, token, expiresAt));
            }

            return new(candidates, false, true);
        }
        catch (Exception ex) when (IsExpectedReaderFailure(ex))
        {
            return RootReadResult.UnavailableResult;
        }
    }

    private static bool TryParseCacheKey(string key, out string accountId, out string organizationId,
        out string scope, out string clientId)
    {
        accountId = string.Empty;
        organizationId = string.Empty;
        scope = string.Empty;
        clientId = string.Empty;
        const string prefix = "acct:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var separator = key.IndexOf('|', prefix.Length);
        if (separator <= prefix.Length) return false;
        accountId = key[prefix.Length..separator];
        if (!Guid.TryParse(accountId, out _)) return false;

        var rest = key[(separator + 1)..];
        var firstColon = rest.IndexOf(':');
        if (firstColon <= 0) return false;
        clientId = rest[..firstColon];
        if (!AcceptedClientIds.Contains(clientId)) return false;

        var secondColon = rest.IndexOf(':', firstColon + 1);
        if (secondColon <= firstColon + 1) return false;
        organizationId = rest[(firstColon + 1)..secondColon];
        if (!Guid.TryParse(organizationId, out _)) return false;

        var hostStart = secondColon + 1;
        var hostMarker = ApiHost + ":";
        if (!rest.AsSpan(hostStart).StartsWith(hostMarker.AsSpan(), StringComparison.Ordinal)) return false;
        var scopeStart = hostStart + hostMarker.Length;
        if (scopeStart >= rest.Length) return false;
        scope = rest[scopeStart..];
        return AcceptedScopes.Contains(scope) && scope.Contains("user:profile", StringComparison.Ordinal);
    }

    private static bool TryReadExpiresAt(JsonElement value, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var milliseconds)) return false;
        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return expiresAt > DateTimeOffset.UnixEpoch;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool TryGetUniqueProperty(JsonElement objectValue, string name,
        out JsonElement value, out bool present)
    {
        value = default;
        present = false;
        if (objectValue.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in objectValue.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.Ordinal)) continue;
            if (present) return false;
            value = property.Value;
            present = true;
        }
        return true;
    }

    private static BoundedFile ReadBoundedFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);
            if (stream.Length is < 0 or > MaxInputBytes) return BoundedFile.InvalidFile;
            var bytes = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0) return BoundedFile.InvalidFile;
                offset += count;
            }
            return new(bytes, false);
        }
        catch (FileNotFoundException) { return BoundedFile.MissingFile; }
        catch (DirectoryNotFoundException) { return BoundedFile.MissingFile; }
        catch (Exception ex) when (IsExpectedReaderFailure(ex)) { return BoundedFile.InvalidFile; }
    }

    private static IEnumerable<string> DiscoverDefaultRoots()
    {
        var roots = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            roots.Add(Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude"));
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming)) roots.Add(Path.Combine(roaming, "Claude"));
        return roots;
    }

    private static string? NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (IsExpectedReaderFailure(ex)) { return null; }
    }

    private static bool HasPrefix(byte[] value, string prefix) =>
        value.AsSpan().StartsWith(Encoding.ASCII.GetBytes(prefix));

    private static bool IsExpectedReaderFailure(Exception ex) => ex is
        IOException or UnauthorizedAccessException or JsonException or ArgumentException
        or InvalidOperationException or FormatException or CryptographicException
        or OverflowException or NotSupportedException or PlatformNotSupportedException
        or DllNotFoundException or EntryPointNotFoundException;

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

    private sealed record TokenCandidate(
        string AccountId,
        string OrganizationId,
        string ClientId,
        string Scope,
        string AccessToken,
        DateTimeOffset ExpiresAt)
    {
        public bool IsProfileOnly => string.Equals(Scope, "user:profile", StringComparison.Ordinal);
        public int ScopeCount => Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        public override string ToString() => nameof(TokenCandidate);
    }

    private readonly record struct RootReadResult(
        IReadOnlyList<TokenCandidate> Candidates,
        bool Unavailable,
        bool SawCache)
    {
        public static RootReadResult Missing => new(Array.Empty<TokenCandidate>(), false, false);
        public static RootReadResult UnavailableResult => new(Array.Empty<TokenCandidate>(), true, true);
    }

    private readonly record struct BoundedFile(byte[]? Data, bool Missing)
    {
        public static BoundedFile MissingFile => new(null, true);
        public static BoundedFile InvalidFile => new(null, false);
    }

    private readonly record struct EncodedValueRead(byte[]? Data, bool Missing, bool Invalid)
    {
        public static EncodedValueRead MissingValue => new(null, true, false);
        public static EncodedValueRead InvalidValue => new(null, false, true);
    }
}
