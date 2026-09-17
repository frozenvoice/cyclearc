namespace CycleArc.Services;

public static class AppPaths
{
    /// <summary>The data location without creating it. Uninstall cleanup must not create anything.</summary>
    public static string RootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LegacyInstallation.DataDirectoryName);

    public static string Root
    {
        get
        {
            var dir = RootPath;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Database => Path.Combine(Root, LegacyInstallation.DatabaseFileName);
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Logs => Directory.CreateDirectory(Path.Combine(Root, "logs")).FullName;
    public static string WebViewProfile => Directory.CreateDirectory(Path.Combine(Root, "webview")).FullName;
    public static string CompanionPairing => Path.Combine(Root, "companion-pairing.json");
    public static string CompanionHostManifest => Path.Combine(Root, LegacyInstallation.NativeHostManifestFileName);
    public static string NativeHostRoot => Directory.CreateDirectory(Path.Combine(Root, "native-host")).FullName;
    public static string CodexSnapshot => Path.Combine(Root, "codex-snapshot.json");
    public static string ProServerStatus => Path.Combine(Root, "pro-server-status.json");
}
