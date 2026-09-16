using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CycleArc.Providers.Claude;

namespace CycleArc.Tests;

public sealed class ClaudeDesktopCredentialReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private const string AccountId = "b8e13d3b-6064-46d3-a221-9d5cb6d1ae70";
    private const string OrganizationId = "1b7ab5b2-e8e7-4d35-a9f6-5abce9da2b26";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string FakeToken = "sk-ant-oat-test-profile";
    private const string FakeDpapiBlob = "fixture-dpapi-blob";

    [Fact]
    public void Read_DecryptsV2Cache_AndPrefersLeastScopeProfileToken()
    {
        using var root = new TempRoot();
        var broadToken = "sk-ant-oat-test-broad";
        var entries = new Dictionary<string, object?>
        {
            [Key("user:inference user:file_upload user:profile user:sessions:claude_code")] =
                Entry(broadToken, Now.AddHours(2)),
            [Key("user:inference")] = Entry("sk-ant-oat-test-inference-only", Now.AddHours(3)),
            [Key("user:profile")] = Entry(FakeToken, Now.AddHours(1))
        };
        WriteFixture(root.Path, entries);

        var read = Reader(root.Path).Read(Now);

        var credential = Assert.Single(read.Credentials);
        Assert.Equal(FakeToken, credential.AccessToken);
        Assert.Equal(Now.AddHours(1), credential.ExpiresAt);
        Assert.DoesNotContain(FakeToken, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", typeof(ClaudeDesktopCredential).GetProperties()
            .Select(property => property.Name));
        Assert.Null(read.Failure);
    }

    [Fact]
    public void Read_FallsBackToMinimumRecognizedScopeContainingProfile()
    {
        using var root = new TempRoot();
        var entries = new Dictionary<string, object?>
        {
            [Key("user:inference user:file_upload user:profile user:sessions:claude_code")] =
                Entry("sk-ant-oat-test-full", Now.AddHours(2)),
            [Key("user:inference user:file_upload user:profile")] =
                Entry(FakeToken, Now.AddHours(1)),
            [Key("user:inference")] = Entry("sk-ant-oat-test-inference-only", Now.AddHours(3))
        };
        WriteFixture(root.Path, entries);

        var read = Reader(root.Path).Read(Now);

        Assert.Equal(FakeToken, Assert.Single(read.Credentials).AccessToken);
        Assert.Null(read.Failure);
    }

    [Fact]
    public void Read_RequiresAuthenticationWhenOnlyCredentialIsExpired()
    {
        using var root = new TempRoot();
        WriteFixture(root.Path, new Dictionary<string, object?>
        {
            [Key("user:profile")] = Entry(FakeToken, Now)
        });

        var read = Reader(root.Path).Read(Now);

        Assert.Empty(read.Credentials);
        Assert.Equal("claude-live-auth-required", read.Failure);
    }

    [Fact]
    public void Read_ReportsUnavailableForInvalidEncryption()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(Path.Combine(root.Path, ClaudeDesktopCredentialReader.ConfigFileName),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["oauth:tokenCacheV2"] = Convert.ToBase64String(Encoding.ASCII.GetBytes("v09-invalid"))
            }));
        File.WriteAllText(Path.Combine(root.Path, ClaudeDesktopCredentialReader.LocalStateFileName),
            JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = WrappedKey() } }));

        var read = Reader(root.Path).Read(Now);

        Assert.Empty(read.Credentials);
        Assert.Equal("claude-live-unavailable", read.Failure);
    }

    [Fact]
    public void Read_ReportsAuthenticationRequiredForMissingDesktopFiles()
    {
        using var root = new TempRoot();

        var read = Reader(root.Path).Read(Now);

        Assert.Empty(read.Credentials);
        Assert.Equal("claude-live-auth-required", read.Failure);
    }

    private static ClaudeDesktopCredentialReader Reader(string root)
    {
        var key = KeyBytes();
        return new([root], blob => blob.SequenceEqual(Encoding.UTF8.GetBytes(FakeDpapiBlob))
            ? key.ToArray()
            : null);
    }

    private static void WriteFixture(string root, IReadOnlyDictionary<string, object?> entries)
    {
        Directory.CreateDirectory(root);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(entries);
        var encrypted = Encrypt(plaintext, KeyBytes());
        CryptographicOperations.ZeroMemory(plaintext);
        File.WriteAllText(Path.Combine(root, ClaudeDesktopCredentialReader.ConfigFileName),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["oauth:tokenCacheV2"] = Convert.ToBase64String(encrypted)
            }));
        CryptographicOperations.ZeroMemory(encrypted);
        File.WriteAllText(Path.Combine(root, ClaudeDesktopCredentialReader.LocalStateFileName),
            JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = WrappedKey() } }));
    }

    private static object Entry(string token, DateTimeOffset expiresAt) => new
    {
        token,
        // This field is intentionally present in fixtures to prove that the
        // reader only projects the access token and expiry.
        refreshToken = "fixture-refresh-token-must-not-be-copied",
        expiresAt = expiresAt.ToUnixTimeMilliseconds()
    };

    private static string Key(string scope) =>
        $"acct:{AccountId}|{ClientId}:{OrganizationId}:https://api.anthropic.com:{scope}";

    private static string WrappedKey() => Convert.ToBase64String(
        Encoding.ASCII.GetBytes("DPAPI" + FakeDpapiBlob));

    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var encrypted = new byte[3 + nonce.Length + ciphertext.Length + tag.Length];
        Encoding.ASCII.GetBytes("v10").CopyTo(encrypted, 0);
        nonce.CopyTo(encrypted, 3);
        ciphertext.CopyTo(encrypted, 3 + nonce.Length);
        tag.CopyTo(encrypted, 3 + nonce.Length + ciphertext.Length);
        CryptographicOperations.ZeroMemory(nonce);
        CryptographicOperations.ZeroMemory(ciphertext);
        CryptographicOperations.ZeroMemory(tag);
        return encrypted;
    }

    private static byte[] KeyBytes() => Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "cyclearc-claude-desktop-credentials-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
