namespace CycleArc.Services;

/// <summary>Loose releases run in place; only an explicit development install may copy itself.</summary>
public static class DesktopDistribution
{
    // Match Velopack 1.2.0's fast-exit dispatch. Its updater also invokes these
    // hooks in disposable .portable layouts, before any desktop may start.
    public static bool IsInstallerHook(IReadOnlyList<string> args) => args.Count >= 2
        && new[] { "--veloapp-install", "--veloapp-updated", "--veloapp-obsolete", "--veloapp-uninstall",
            "--squirrel-install", "--squirrel-updated", "--squirrel-obsolete", "--squirrel-uninstall" }
            .Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static bool IsDevelopmentInstall(string? executable, string developmentExecutable) =>
        executable is not null && Path.GetFullPath(executable).Equals(
            Path.GetFullPath(developmentExecutable), StringComparison.OrdinalIgnoreCase);

    // Inspect only adjacent installation files, never registered installations elsewhere.
    // The standalone ZIP has neither file; ordinary launches must not initialize an updater.
    public static bool HasManagedLayout(string? executable)
    {
        if (executable is null) return false;
        var content = Path.GetDirectoryName(Path.GetFullPath(executable));
        var root = content is null ? null : Path.GetDirectoryName(content);
        return root is not null && File.Exists(Path.Combine(content!, "sq.version"))
            && File.Exists(Path.Combine(root, "Update.exe"))
            && !File.Exists(Path.Combine(root, ".portable"));
    }
}
