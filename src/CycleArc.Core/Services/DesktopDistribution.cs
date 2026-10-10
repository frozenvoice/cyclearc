namespace CycleArc.Services;

/// <summary>Loose releases run in place; only an explicit development install may copy itself.</summary>
public static class DesktopDistribution
{
    public static bool IsDevelopmentInstall(string? executable, string developmentExecutable) =>
        executable is not null && Path.GetFullPath(executable).Equals(
            Path.GetFullPath(developmentExecutable), StringComparison.OrdinalIgnoreCase);

    // Inspect only adjacent installation files, never registered installations elsewhere.
    // The standalone ZIP has neither file and must not initialize an updater or its hooks.
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
