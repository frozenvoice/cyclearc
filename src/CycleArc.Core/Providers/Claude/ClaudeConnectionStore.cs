using System.Text.Json;
using System.Text.Json.Serialization;
using CycleArc.Codex;

namespace CycleArc.Providers.Claude;

public static class ClaudeConnectionPaths
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
    public static string ImplicitDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    public static bool UsesImplicitDirectory => Normalize(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")) is null;
    public static string DefaultDirectory => Normalize(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")) ?? ImplicitDirectory;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClaudeConnectionBinding(int Version, string ProfileId, string ConfigDirectory,
    string CliExecutable, bool Managed, string IdentityFingerprint, DateTimeOffset ConnectedAt, bool UseDefaultConfig = false,
    bool Disconnected = false, string? BindingGeneration = null);

public sealed record ClaudeConnectionRead(ClaudeConnectionBinding? Binding, bool Unavailable = false);

// Separate provider metadata keeps Codex account/home/cache formats unchanged. No emails or credentials.
public sealed class ClaudeConnectionStore(CodexAccountStore accounts, string profileId)
{
    public string PathName => accounts.ClaudeConnectionPath(profileId);
    public ClaudeConnectionRead Read()
    {
        if (!File.Exists(PathName)) return new(null);
        try
        {
            using var stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is 0 or > 8192) return new(null, true);
            var binding = JsonSerializer.Deserialize<ClaudeConnectionBinding>(stream);
            return binding is not null && Valid(binding) ? new(binding) : new(null, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(null, true); }
    }
    public void Save(ClaudeConnectionBinding binding)
    {
        if (!Valid(binding)) throw new InvalidDataException("Invalid Claude connection.");
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, binding); stream.Flush(true);
            }
            File.Move(temporary, PathName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Delete() { if (File.Exists(PathName)) File.Delete(PathName); }
    private bool Valid(ClaudeConnectionBinding binding) => binding.Version == 1 && binding.ProfileId == profileId
        && Guid.TryParseExact(profileId, "N", out _) && ClaudeConnectionPaths.Normalize(binding.ConfigDirectory) is { } directory
        && directory == binding.ConfigDirectory
        && (!binding.UseDefaultConfig || string.Equals(directory, ClaudeConnectionPaths.ImplicitDirectory, StringComparison.OrdinalIgnoreCase))
        && ClaudeCli.IsExecutablePath(binding.CliExecutable) && binding.ConnectedAt > DateTimeOffset.UnixEpoch
        && binding.IdentityFingerprint is { Length: 64 } hash && hash.All(Uri.IsHexDigit)
        && (binding.BindingGeneration is null || Guid.TryParseExact(binding.BindingGeneration, "N", out _));
}
