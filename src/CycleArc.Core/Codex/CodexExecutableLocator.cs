using CycleArc.Services;

namespace CycleArc.Codex;

public interface ICodexFileSystem
{
    bool FileExists(string path);
    IReadOnlyList<string> PathEntries();
    IReadOnlyList<string> CommonDirectories();
}

public sealed record CodexLaunchCommand(
    string FileName,
    string Arguments,
    string ResolvedExecutable,
    bool UsesCmd)
{
    // Applied to this child only. Never change the application's/global environment.
    public string? CodexHome { get; init; }
    public bool ManagedHome { get; init; }
}

public static class CodexProcessQuoting
{
    public const string AppServerArguments = "app-server --stdio";

    public static string CmdLaunchArguments(string batchPath) =>
        "/d /s /c \"\"" + batchPath + "\" " + AppServerArguments + "\"";
}

public sealed class CodexExecutableLocator
{
    private static readonly string[] Names = ["codex.exe", "codex.cmd", "codex.bat"];

    private readonly ICodexFileSystem _files;

    public CodexExecutableLocator(ICodexFileSystem files) => _files = files;

    public CodexLaunchCommand? Locate(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return ValidateConfigured(configuredPath);
        }

        foreach (var directory in _files.PathEntries().Concat(_files.CommonDirectories()))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
            {
                continue;
            }

            foreach (var name in Names)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, name));
                if (_files.FileExists(candidate))
                {
                    return ToCommand(candidate);
                }
            }
        }

        return null;
    }

    public static CodexLaunchCommand? ValidateConfigured(string path, ICodexFileSystem? files = null)
    {
        if (!Path.IsPathRooted(path))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }

        if (!string.Equals(full, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var extension = Path.GetExtension(full);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (files is not null && !files.FileExists(full))
        {
            return null;
        }

        return ToCommand(full);
    }

    private CodexLaunchCommand? ValidateConfigured(string path) => ValidateConfigured(path, _files);

    /// <summary>
    /// Why a path typed in settings would not start Codex, or null when it would (or when it is
    /// empty and Codex is found automatically). Shown beside the box; it never blocks saving.
    /// </summary>
    public static string? ConfiguredPathProblem(string? path, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim().Trim('"');
        if (ValidateConfigured(trimmed) is not { } command)
            return UiText.T("Enter the full path to codex.exe, codex.cmd or codex.bat.",
                "codex.exe, codex.cmd 또는 codex.bat의 전체 경로를 입력하세요.");
        return fileExists(command.ResolvedExecutable) ? null
            : UiText.T("No file exists at this path. Codex will not start until it does.",
                "이 경로에 파일이 없습니다. 파일이 있어야 Codex를 실행할 수 있습니다.");
    }

    private static CodexLaunchCommand ToCommand(string absolute)
    {
        var extension = Path.GetExtension(absolute);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexLaunchCommand(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                CodexProcessQuoting.CmdLaunchArguments(absolute),
                absolute,
                UsesCmd: true);
        }

        return new CodexLaunchCommand(absolute, CodexProcessQuoting.AppServerArguments, absolute, UsesCmd: false);
    }
}

public sealed class WindowsCodexFileSystem : ICodexFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> PathEntries()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public IReadOnlyList<string> CommonDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            Path.Combine(home, ".local", "bin"),
            Path.Combine(local, "Programs"),
            Path.Combine(roaming, "npm"),
            Path.Combine(local, "fnm"),
            Path.Combine(home, ".cargo", "bin")
        ];
    }
}
